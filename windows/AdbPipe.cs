using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Phonestick
{
    /// <summary>
    /// "Phonestick.exe --pipe SERIAL": the external ssh command rclone uses to reach the
    /// phone's SFTP server. It runs the server script through the adb server's shell v2
    /// protocol and relays the SFTP packets both ways, shaping file reads (see ReadShaper).
    ///
    /// Why not just "adb shell -T": on Windows the adb client writes stdout in text mode
    /// and turns every LF byte into CR LF, which corrupts binary SFTP packets. And
    /// "adb exec-out" is binary safe but does not forward stdin.
    /// </summary>
    internal static class AdbPipe
    {
        private const byte IdStdin = 0, IdStdout = 1, IdExit = 3, IdCloseStdin = 4;

        public static int Run(string serial, string command)
        {
            try
            {
                using (var tcp = new TcpClient("127.0.0.1", AppInfo.AdbPort))
                {
                    tcp.NoDelay = true;
                    var net = tcp.GetStream();
                    Request(net, "host:transport:" + serial);
                    Request(net, "shell,v2,raw:" + command);

                    var stdin = Console.OpenStandardInput();
                    var stdout = Console.OpenStandardOutput();
                    var sendLock = new object();
                    var outLock = new object();
                    var shaper = new ReadShaper((b, n) => { lock (outLock) { stdout.Write(b, 0, n); stdout.Flush(); } });
                    bool shape = Environment.GetEnvironmentVariable("PHONESTICK_NO_SHAPING") != "1";

                    var up = new Thread(() =>
                    {
                        // Header room in front of the data: one TCP segment per shell packet
                        // instead of two (NoDelay sends each write at once).
                        var buf = new byte[5 + 65536];
                        var send = new byte[5 + 65536];
                        try
                        {
                            int n;
                            while ((n = stdin.Read(buf, 5, buf.Length - 5)) > 0)
                            {
                                if (!shape) { lock (sendLock) WritePacket(net, IdStdin, buf, n); continue; }
                                // The SFTP packets complete so far, minus the reads held back.
                                int len = shaper.FromClient(buf, 5, n, ref send);
                                if (len > 0) lock (sendLock) SendData(net, send, len);
                            }
                            lock (sendLock) WritePacket(net, IdCloseStdin, buf, 0);
                        }
                        catch { }
                    }) { IsBackground = true };
                    up.Start();
                    if (shape) shaper.StartTimer();

                    var header = new byte[5];
                    var data = new byte[65536];
                    while (ReadExact(net, header, 5))
                    {
                        int len = BitConverter.ToInt32(header, 1);
                        if (len > data.Length) data = new byte[len];
                        if (len > 0 && !ReadExact(net, data, len)) break;
                        if (header[0] == IdStdout)
                        {
                            if (shape) shaper.FromPhone(data, len);
                            else lock (outLock) { stdout.Write(data, 0, len); stdout.Flush(); }
                        }
                        else if (header[0] == IdExit)
                        {
                            return len > 0 ? data[0] : 0;
                        }
                        // stderr (2) is dropped: it must never mix into the SFTP stream.
                    }
                    return 0;
                }
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine(AppInfo.Name + " pipe: " + ex.Message); } catch { }
                return 255;
            }
        }

        private static void Request(NetworkStream s, string msg)
        {
            var body = Encoding.UTF8.GetBytes(msg);
            var head = Encoding.ASCII.GetBytes(body.Length.ToString("x4"));
            s.Write(head, 0, 4);
            s.Write(body, 0, body.Length);
            var status = new byte[4];
            if (!ReadExact(s, status, 4)) throw new IOException("adb server closed the connection");
            var st = Encoding.ASCII.GetString(status);
            if (st == "OKAY") return;
            var lenBuf = new byte[4];
            string reason = "";
            if (ReadExact(s, lenBuf, 4))
            {
                int n = Convert.ToInt32(Encoding.ASCII.GetString(lenBuf), 16);
                var r = new byte[n];
                if (ReadExact(s, r, n)) reason = Encoding.UTF8.GetString(r);
            }
            throw new IOException("adb: " + st + " " + reason);
        }

        /// <summary>Sends n bytes at buf[5..] as stdin, in shell packets of at most 64 KB.</summary>
        private static void SendData(NetworkStream s, byte[] buf, int n)
        {
            if (n <= 65536) { WritePacket(s, IdStdin, buf, n); return; }
            var piece = new byte[5 + 65536];
            for (int off = 0; off < n; off += 65536)
            {
                int k = Math.Min(65536, n - off);
                Buffer.BlockCopy(buf, 5 + off, piece, 5, k);
                WritePacket(s, IdStdin, piece, k);
            }
        }

        /// <summary>Sends one shell v2 packet: <paramref name="n"/> bytes of data at buf[5..], after the header room.</summary>
        private static void WritePacket(NetworkStream s, byte id, byte[] buf, int n)
        {
            buf[0] = id;
            buf[1] = (byte)n; buf[2] = (byte)(n >> 8); buf[3] = (byte)(n >> 16); buf[4] = (byte)(n >> 24);
            s.Write(buf, 0, 5 + n);
        }

        private static bool ReadExact(Stream s, byte[] buf, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) return false;
                off += r;
            }
            return true;
        }
    }

    /// <summary>
    /// Shapes rclone's file reads like TCP slow start. On every open, rclone's SFTP client asks
    /// for up to 64 chunks of 32 KB at once (2 MB), even when the reader only wants the first
    /// few KB: Explorer reading a photo's header for its thumbnail or date paid for 2 MB each
    /// time, a long wait on Wi-Fi. Here only the first 4 asks of a file go to the phone; every
    /// further ask the client makes (it makes one each time the reader has taken a chunk) lets
    /// two more through, so a real copy is back at full speed after a few round trips.
    ///
    /// When the reader stopped (file closed, or a reader pausing) the client waits for answers
    /// to the asks still held here: after a short quiet time they are answered "end of file".
    /// For a reader that only paused, rclone then reopens the file at that point and goes on.
    ///
    /// Anything unexpected in the stream turns shaping off for the rest of the connection: the
    /// bytes are then relayed exactly as they come.
    /// </summary>
    internal sealed class ReadShaper
    {
        private const byte FxpClose = 4, FxpRead = 5, FxpStatus = 101, FxpData = 103;
        private const int FirstAsks = 4;
        /// <summary>Quiet time after which held asks of a stopped reader are answered.</summary>
        private const int IdleMs = 30;
        private const int MaxPacket = 4 << 20;
        /// <summary>Handles are opaque bytes: one char per byte.</summary>
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private sealed class Handle
        {
            public string Name;
            public int Credit = FirstAsks, InFlight, Answered;
            public readonly Queue<byte[]> Held = new Queue<byte[]>();
            public long LastActivity;
        }

        private readonly object _gate = new object();
        private readonly Action<byte[], int> _toClient;
        private readonly Dictionary<string, Handle> _handles = new Dictionary<string, Handle>();
        private readonly Dictionary<uint, Handle> _asks = new Dictionary<uint, Handle>();
        private byte[] _up = new byte[1 << 16], _down = new byte[1 << 17];
        private int _upLen, _downLen;
        private volatile bool _raw;

        public ReadShaper(Action<byte[], int> toClient) { _toClient = toClient; }

        private static long Now => Environment.TickCount;

        /// <summary>
        /// Bytes from rclone: returns how many bytes to send to the phone, written into
        /// <paramref name="send"/> from index 5 (shell header room). Partial packets wait.
        /// </summary>
        public int FromClient(byte[] buf, int off, int n, ref byte[] send)
        {
            if (_raw) { Ensure(ref send, 5 + n); Buffer.BlockCopy(buf, off, send, 5, n); return n; }
            Append(ref _up, ref _upLen, buf, off, n);
            int outLen = 0, pos = 0;
            lock (_gate)
            {
                while (_upLen - pos >= 4)
                {
                    int len = BE32(_up, pos);
                    if (len < 5 || len > MaxPacket) { GoRaw(); break; }
                    if (_upLen - pos < 4 + len) break;
                    byte type = _up[pos + 4];
                    if (type == FxpRead && len >= 13)
                    {
                        uint id = (uint)BE32(_up, pos + 5);
                        int hlen = BE32(_up, pos + 9);
                        if (hlen < 0 || 13 + hlen > 4 + len) { GoRaw(); break; }
                        var name = Latin1.GetString(_up, pos + 13, hlen);
                        if (!_handles.TryGetValue(name, out var h)) _handles[name] = h = new Handle { Name = name };
                        // An ask after answers came back means the reader took a chunk.
                        if (h.Answered > 0) h.Credit += 2;
                        h.LastActivity = Now;
                        var p = new byte[4 + len];
                        Buffer.BlockCopy(_up, pos, p, 0, p.Length);
                        h.Held.Enqueue(p);
                        while (h.Credit > 0 && h.Held.Count > 0)
                        {
                            var q = h.Held.Dequeue();
                            h.Credit--;
                            h.InFlight++;
                            _asks[(uint)BE32(q, 5)] = h;
                            AppendOut(ref send, ref outLen, q, 0, q.Length);
                        }
                    }
                    else
                    {
                        if (type == FxpClose && len >= 9)
                        {
                            int hlen = BE32(_up, pos + 9);
                            if (hlen >= 0 && 13 + hlen <= 4 + len)
                            {
                                var name = Latin1.GetString(_up, pos + 13, hlen);
                                if (_handles.TryGetValue(name, out var h)) { AnswerHeld(h); _handles.Remove(name); }
                            }
                        }
                        AppendOut(ref send, ref outLen, _up, pos, 4 + len);
                    }
                    pos += 4 + len;
                }
                if (_raw)
                {
                    // Everything still here goes as is, held asks first (they came first).
                    foreach (var h in _handles.Values)
                        while (h.Held.Count > 0) { var q = h.Held.Dequeue(); AppendOut(ref send, ref outLen, q, 0, q.Length); }
                    _handles.Clear();
                    _asks.Clear();
                    AppendOut(ref send, ref outLen, _up, pos, _upLen - pos);
                    pos = _upLen;
                }
            }
            Buffer.BlockCopy(_up, pos, _up, 0, _upLen - pos);
            _upLen -= pos;
            return outLen;
        }

        /// <summary>Bytes from the phone: complete packets go to rclone, answered asks are counted.</summary>
        public void FromPhone(byte[] buf, int n)
        {
            if (_raw && _downLen == 0) { _toClient(buf, n); return; }
            Append(ref _down, ref _downLen, buf, 0, n);
            int pos = 0;
            lock (_gate)
            {
                while (!_raw && _downLen - pos >= 4)
                {
                    int len = BE32(_down, pos);
                    if (len < 1 || len > MaxPacket) { GoRaw(); break; }
                    if (_downLen - pos < 4 + len) break;
                    byte type = _down[pos + 4];
                    if ((type == FxpData || type == FxpStatus) && len >= 5
                        && _asks.TryGetValue((uint)BE32(_down, pos + 5), out var h))
                    {
                        _asks.Remove((uint)BE32(_down, pos + 5));
                        h.InFlight--;
                        h.Answered++;
                        h.LastActivity = Now;
                    }
                    pos += 4 + len;
                }
                if (_raw) pos = _downLen;
                // Whole packets only, under the output lock: an answer made up here (AnswerHeld)
                // can then never land inside a packet from the phone.
                if (pos > 0) _toClient(_down, pos);
            }
            Buffer.BlockCopy(_down, pos, _down, 0, _downLen - pos);
            _downLen -= pos;
        }

        public void StartTimer()
        {
            new Thread(() =>
            {
                var stale = new List<Handle>();
                while (true)
                {
                    Thread.Sleep(10);
                    if (_raw) return;
                    lock (_gate)
                    {
                        long now = Now;
                        stale.Clear();
                        // Nothing on the way and nothing asked for a while: the reader stopped.
                        foreach (var h in _handles.Values)
                            if (h.Held.Count > 0 && h.InFlight == 0 && unchecked(now - h.LastActivity) >= IdleMs) stale.Add(h);
                        foreach (var h in stale) { AnswerHeld(h); _handles.Remove(h.Name); }
                    }
                }
            }) { IsBackground = true }.Start();
        }

        /// <summary>Answers the asks held for this file "end of file" (SSH_FXP_STATUS, SSH_FX_EOF). Under _gate.</summary>
        private void AnswerHeld(Handle h)
        {
            if (h.Held.Count == 0) return;
            var msg = new byte[h.Held.Count * 21];
            int o = 0;
            while (h.Held.Count > 0)
            {
                var q = h.Held.Dequeue();
                // length 17: type, id, code, empty message, empty language tag
                PutBE32(msg, o, 17); msg[o + 4] = FxpStatus;
                Buffer.BlockCopy(q, 5, msg, o + 5, 4);
                PutBE32(msg, o + 9, 1); PutBE32(msg, o + 13, 0); PutBE32(msg, o + 17, 0);
                o += 21;
            }
            _toClient(msg, o);
        }

        private void GoRaw() { _raw = true; }

        private static void AppendOut(ref byte[] send, ref int outLen, byte[] src, int off, int n)
        {
            Ensure(ref send, 5 + outLen + n);
            Buffer.BlockCopy(src, off, send, 5 + outLen, n);
            outLen += n;
        }

        private static void Append(ref byte[] dst, ref int len, byte[] src, int off, int n)
        {
            Ensure(ref dst, len + n);
            Buffer.BlockCopy(src, off, dst, len, n);
            len += n;
        }

        private static void Ensure(ref byte[] b, int size)
        {
            if (b.Length >= size) return;
            var nb = new byte[Math.Max(size, b.Length * 2)];
            Buffer.BlockCopy(b, 0, nb, 0, b.Length);
            b = nb;
        }

        private static int BE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static void PutBE32(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
    }
}

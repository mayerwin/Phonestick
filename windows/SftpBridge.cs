using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Phonestick
{
    /// <summary>
    /// The phone's SFTP server, offered on a loopback port for sshfs ("-o directport"). Each
    /// connection runs the server on the phone through the adb server's shell v2 protocol and
    /// relays the SFTP packets both ways (SftpRelay).
    ///
    /// Why a port and not a pipe: sshfs talks to its "ssh" command over a Cygwin socket pair,
    /// which a Windows program cannot read. Only the sshfs process this bridge was made for
    /// may connect (checked by the connection's owning process), so the port gives no other
    /// program a way in.
    /// </summary>
    internal sealed class SftpBridge : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _serial, _command;
        private volatile bool _closed;

        /// <summary>The loopback port to give sshfs.</summary>
        public int Port { get; }

        /// <summary>The process allowed to connect (sshfs), set once it is started.</summary>
        public volatile int ClientPid;

        private long _bytes;
        /// <summary>Bytes relayed both ways so far (for the idle check).</summary>
        public long Bytes => Interlocked.Read(ref _bytes);

        public SftpBridge(string serial, string command)
        {
            _serial = serial;
            _command = command;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(Accept) { IsBackground = true, Name = "sftp bridge" }.Start();
        }

        private void Accept()
        {
            while (!_closed)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (_closed) return; continue; }
                var remote = (IPEndPoint)c.Client.RemoteEndPoint;
                int owner = OwnerPid(remote.Port);
                // sshfs may connect before Process.Start has returned its id to us.
                for (int i = 0; i < 40 && ClientPid == 0; i++) Thread.Sleep(50);
                if (ClientPid == 0 || owner != ClientPid)
                {
                    Log.Write("sftp bridge: refused a connection from process " + owner);
                    c.Close();
                    continue;
                }
                c.NoDelay = true;
                new Thread(() =>
                {
                    using (c)
                    {
                        try { new SftpRelay(c.GetStream(), n => Interlocked.Add(ref _bytes, n)).Run(_serial, _command); }
                        catch (Exception ex) { Log.Write("sftp relay: " + ex.Message); }
                    }
                }) { IsBackground = true, Name = "sftp relay" }.Start();
            }
        }

        public void Dispose()
        {
            _closed = true;
            try { _listener.Stop(); } catch { }
        }

        // ------------------------------------------------------------ who connected

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        /// <summary>The process owning the local TCP endpoint 127.0.0.1:port (the client side), or -1.</summary>
        private static int OwnerPid(int port)
        {
            const int AF_INET = 2, TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0) return -1;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 x 4 bytes)
                    var row = buf + 4 + i * 24;
                    int localPort = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row + 8));
                    uint localAddr = (uint)Marshal.ReadInt32(row + 4);
                    if (localPort == port && localAddr == 0x0100007F) return Marshal.ReadInt32(row + 20);
                }
                return -1;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    /// <summary>
    /// Relays one SFTP session between sshfs and the server on the phone, packet by packet,
    /// saving Wi-Fi round trips where it safely can. sshfs asks for exactly what Windows reads,
    /// in 32 KB pieces, and waits a round trip for each; Explorer making a thumbnail opens the
    /// same file several times and reads it in many small steps, so those waits add up.
    ///
    /// - Read ahead: when a file is read in sequence, the next pieces are asked for at once (the
    ///   window doubles while the reads keep following on, up to 1 MB). sshfs's request for a
    ///   piece is then answered from what already arrived, also when it reads it again.
    /// - Reopen: a file opened for reading and closed is kept open on the phone for a few
    ///   seconds, so opening it again is answered at once, with what was read ahead.
    ///
    /// It only ever answers a request with the phone's own answer to that same request (same
    /// file handle, offset and length), at most a few seconds old. Anything that may change a
    /// file (a write, a rename, a delete...) first drops everything read ahead and closes the
    /// files kept open, and a request waiting for a piece dropped that way goes to the phone
    /// itself. Nothing is read ahead while such a change is on its way: the phone runs reads
    /// and writes in parallel, so a read sent after a write could otherwise see the file
    /// before it.
    /// </summary>
    internal sealed class SftpRelay
    {
        private const byte FxpOpen = 3, FxpClose = 4, FxpRead = 5, FxpLstat = 7, FxpFstat = 8, FxpOpendir = 11,
            FxpReaddir = 12, FxpRealpath = 16, FxpStat = 17, FxpReadlink = 19, FxpStatus = 101, FxpHandle = 102, FxpData = 103;
        /// <summary>How long a file closed by sshfs stays open on the phone, ready to be opened again.</summary>
        private const int KeepOpenMs = 5000;
        private const int MaxKeptOpen = 64;
        private const int Piece = 32768, FirstWindow = 64 * 1024, MaxWindow = 1024 * 1024;
        private const long MaxCached = 32L * 1024 * 1024;
        /// <summary>A piece read ahead is used only this long after it was asked for.</summary>
        private const int FreshMs = 3000;
        private const int MaxPacket = 1 << 20;

        private readonly Stream _client;
        private readonly Action<int> _moved;
        private readonly object _gate = new object(), _toClient = new object(), _toPhone = new object();
        private NetworkStream _net;

        private sealed class Chunk
        {
            public uint Id;            // our own request id
            public byte[] Answer;      // the phone's answer (whole packet), or null while on its way
            public int Asked;          // Environment.TickCount when asked
            public List<byte[]> Waiting; // sshfs's own requests waiting for this answer
        }

        private sealed class FileState
        {
            public long Next = -1;     // where the next read would follow on
            public int Window = FirstWindow;
            public readonly Dictionary<long, Chunk> Chunks = new Dictionary<long, Chunk>(); // by offset (32 KB pieces)
        }

        private readonly Dictionary<string, FileState> _files = new Dictionary<string, FileState>();
        private readonly Dictionary<uint, (FileState f, long off, Chunk c)> _ours = new Dictionary<uint, (FileState, long, Chunk)>();
        /// <summary>Requests that may change a file, sent and not answered yet.</summary>
        private readonly HashSet<uint> _changing = new HashSet<uint>();
        /// <summary>Read-only opens on their way to the phone: request id, path.</summary>
        private readonly Dictionary<uint, string> _opening = new Dictionary<uint, string>();
        /// <summary>Read-only handles sshfs holds: handle, path.</summary>
        private readonly Dictionary<string, string> _readOnly = new Dictionary<string, string>();
        /// <summary>Files sshfs closed, still open on the phone: path, (handle, when closed).</summary>
        private readonly Dictionary<string, (string handle, int at)> _kept = new Dictionary<string, (string, int)>();
        private volatile bool _ended;
        private uint _nextId = 0x80000000;
        private long _cached;
        private volatile bool _raw;

        public SftpRelay(Stream client, Action<int> moved)
        {
            _client = client;
            _moved = moved;
        }

        public void Run(string serial, string command)
        {
            _net = AdbShell.Open(serial, command);
            try
            {
                var up = new Thread(Up) { IsBackground = true, Name = "sftp to phone" };
                up.Start();
                new Thread(CloseKept) { IsBackground = true, Name = "sftp close kept" }.Start();
                Down();
            }
            finally { _ended = true; try { _net.Dispose(); } catch { } }
        }

        // ------------------------------------------------------------ sshfs to phone

        private void Up()
        {
            var buf = new byte[1 << 16];
            int len = 0;
            try
            {
                int n;
                while ((n = _client.Read(buf, len, buf.Length - len)) > 0)
                {
                    _moved(n);
                    len += n;
                    int pos = 0;
                    while (len - pos >= 4)
                    {
                        int plen = BE32(buf, pos);
                        if (plen < 1 || plen > MaxPacket) { _raw = true; break; }
                        if (len - pos < 4 + plen) break;
                        var p = new byte[4 + plen];
                        Buffer.BlockCopy(buf, pos, p, 0, p.Length);
                        pos += p.Length;
                        FromClient(p);
                    }
                    if (_raw)
                    {
                        // Unexpected framing: relay the rest as it comes, untouched.
                        SendToPhone(buf, pos, len - pos);
                        while ((n = _client.Read(buf, 0, buf.Length)) > 0) { _moved(n); SendToPhone(buf, 0, n); }
                        break;
                    }
                    Buffer.BlockCopy(buf, pos, buf, 0, len - pos);
                    len -= pos;
                    if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2); // a packet bigger than the buffer
                }
                lock (_toPhone) AdbShell.CloseInput(_net);
            }
            catch { try { _net.Dispose(); } catch { } }
        }

        private void FromClient(byte[] p)
        {
            byte type = p[4];
            if (type == FxpRead && p.Length >= 4 + 1 + 4 + 4)
            {
                int hlen = BE32(p, 9);
                if (hlen >= 0 && 13 + hlen + 12 <= p.Length)
                {
                    var handle = Encoding.GetEncoding(28591).GetString(p, 13, hlen);
                    long off = BE64(p, 13 + hlen);
                    int want = BE32(p, 13 + hlen + 8);
                    if (Read(p, handle, off, want)) return; // answered, or waiting for a piece on its way
                }
                SendToPhone(p, 0, p.Length);
                return;
            }
            if (type == FxpOpen && ReadOnlyOpen(p))
            {
                var path = Str(p, 9);
                uint id = (uint)BE32(p, 5);
                string reuse = null;
                lock (_gate)
                {
                    if (_changing.Count == 0 && _kept.TryGetValue(path, out var k))
                    {
                        _kept.Remove(path);
                        _readOnly[k.handle] = path;
                        reuse = k.handle;
                    }
                    else _opening[id] = path;
                }
                if (reuse != null) { SendToClient(HandleAnswer(id, reuse)); return; }
                SendToPhone(p, 0, p.Length);
                return;
            }
            if (type == FxpClose && p.Length >= 13)
            {
                int hlen = BE32(p, 9);
                if (hlen >= 0 && 13 + hlen <= p.Length)
                {
                    var handle = Encoding.GetEncoding(28591).GetString(p, 13, hlen);
                    bool keep = false;
                    lock (_gate)
                    {
                        if (_readOnly.TryGetValue(handle, out var path))
                        {
                            _readOnly.Remove(handle);
                            if (_changing.Count == 0 && !_kept.ContainsKey(path) && _kept.Count < MaxKeptOpen)
                            {
                                _kept[path] = (handle, Environment.TickCount);
                                keep = true;
                            }
                        }
                        if (!keep && _files.TryGetValue(handle, out var f)) { Drop(f); _files.Remove(handle); }
                    }
                    if (keep) { SendToClient(OkAnswer((uint)BE32(p, 5))); return; }
                }
            }
            else if (!(type == FxpLstat || type == FxpFstat || type == FxpOpendir || type == FxpReaddir || type == FxpRealpath
                       || type == FxpStat || type == FxpReadlink || type == 1 /* init */ || (type == FxpOpen && ReadOnlyOpen(p))))
            {
                // Anything that may change a file: nothing read ahead is used after it.
                lock (_gate)
                {
                    if (p.Length >= 9) _changing.Add((uint)BE32(p, 5));
                    foreach (var f in _files.Values) Drop(f);
                    // Files kept open could be the ones changing: closed for real first.
                    foreach (var k in _kept.Values) { _files.Remove(k.handle); SendToPhone(CloseRequest(NewId(), k.handle)); }
                    _kept.Clear();
                }
            }
            SendToPhone(p, 0, p.Length);
        }

        /// <summary>An OPEN that cannot change the file (no write, append, create or truncate flag).</summary>
        private static bool ReadOnlyOpen(byte[] p)
        {
            if (p.Length < 13) return false;
            int nlen = BE32(p, 9);
            if (nlen < 0 || 13 + nlen + 4 > p.Length) return false;
            int pflags = BE32(p, 13 + nlen);
            return (pflags & (0x02 | 0x04 | 0x08 | 0x10)) == 0;
        }

        /// <summary>A read from sshfs: true if answered here (or set to be once the piece arrives).</summary>
        private bool Read(byte[] request, string handle, long off, int want)
        {
            var ahead = new List<byte[]>();
            bool served = false;
            byte[] answer = null;
            lock (_gate)
            {
                if (!_files.TryGetValue(handle, out var f)) _files[handle] = f = new FileState();
                if (want == Piece && f.Chunks.TryGetValue(off, out var c) && unchecked(Environment.TickCount - c.Asked) < FreshMs)
                {
                    served = true;
                    if (c.Answer != null) answer = c.Answer; // kept: the same piece is often read again
                    else (c.Waiting ?? (c.Waiting = new List<byte[]>())).Add(request);
                }
                // Follows on from the last read: widen the window; a jump starts it again.
                if (off == f.Next) f.Window = Math.Min(f.Window * 2, MaxWindow);
                else if (f.Next >= 0) f.Window = FirstWindow;
                f.Next = off + want;
                // Pieces too old to be used are let go.
                if (f.Chunks.Count > 0)
                    foreach (var k in new List<long>(f.Chunks.Keys))
                    {
                        var x = f.Chunks[k];
                        if (x.Answer != null && unchecked(Environment.TickCount - x.Asked) >= FreshMs) { _cached -= x.Answer.Length; f.Chunks.Remove(k); }
                    }
                if (want == Piece && _cached < MaxCached && _changing.Count == 0)
                    for (long o = off + want; o < off + want + f.Window; o += Piece)
                    {
                        if (f.Chunks.ContainsKey(o)) continue;
                        var id = NewId();
                        var chunk = new Chunk { Id = id, Asked = Environment.TickCount };
                        f.Chunks[o] = chunk;
                        _ours[id] = (f, o, chunk);
                        ahead.Add(ReadRequest(id, handle, o, Piece));
                    }
            }
            if (answer != null) SendToClient(WithId(answer, BE32(request, 5)));
            if (ahead.Count > 0)
            {
                var all = new byte[ahead.Sum(a => a.Length)];
                int o = 0;
                foreach (var a in ahead) { Buffer.BlockCopy(a, 0, all, o, a.Length); o += a.Length; }
                if (!served) SendToPhone(Concat(request, all), 0, request.Length + all.Length);
                else SendToPhone(all, 0, all.Length);
                return true;
            }
            return served;
        }

        /// <summary>Forgets what was read ahead for a file; requests waiting on a piece go to the phone. Under _gate.</summary>
        private void Drop(FileState f)
        {
            foreach (var c in f.Chunks.Values)
            {
                if (c.Answer != null) _cached -= c.Answer.Length;
                else
                {
                    _ours.Remove(c.Id); // its answer, when it comes, is thrown away
                    if (c.Waiting != null) foreach (var w in c.Waiting) SendToPhone(w, 0, w.Length);
                }
            }
            f.Chunks.Clear();
            f.Next = -1;
            f.Window = FirstWindow;
        }

        // ------------------------------------------------------------ phone to sshfs

        private void Down()
        {
            var buf = new byte[1 << 17];
            int len = 0;
            foreach (var (data, n) in AdbShell.ReadOutput(_net))
            {
                _moved(n);
                if (_raw && len == 0) { SendToClient(data, 0, n); continue; }
                if (len + n > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, len + n));
                Buffer.BlockCopy(data, 0, buf, len, n);
                len += n;
                int pos = 0;
                while (len - pos >= 4)
                {
                    int plen = BE32(buf, pos);
                    if (plen < 1 || plen > MaxPacket) { _raw = true; break; }
                    if (len - pos < 4 + plen) break;
                    FromPhone(buf, pos, 4 + plen);
                    pos += 4 + plen;
                }
                if (_raw) { SendToClient(buf, pos, len - pos); len = 0; continue; }
                Buffer.BlockCopy(buf, pos, buf, 0, len - pos);
                len -= pos;
            }
            try { _client.Close(); } catch { }
        }

        private void FromPhone(byte[] buf, int pos, int n)
        {
            byte type = buf[pos + 4];
            if (n >= 9 && type != 2 /* version */)
            {
                uint rid = (uint)BE32(buf, pos + 5);
                lock (_gate) if (_changing.Remove(rid)) foreach (var f in _files.Values) Drop(f);
            }
            if ((type == FxpData || type == FxpStatus) && n >= 9)
            {
                uint id = (uint)BE32(buf, pos + 5);
                if ((id & 0x80000000) != 0)
                {
                    List<byte[]> waiting = null;
                    byte[] answer = null;
                    lock (_gate)
                    {
                        if (!_ours.TryGetValue(id, out var x)) return; // dropped meanwhile
                        _ours.Remove(id);
                        answer = new byte[n];
                        Buffer.BlockCopy(buf, pos, answer, 0, n);
                        if (x.c.Waiting != null && x.c.Waiting.Count > 0) { waiting = x.c.Waiting; x.c.Waiting = null; }
                        x.c.Answer = answer;
                        _cached += n;
                    }
                    if (waiting != null) foreach (var w in waiting) SendToClient(WithId(answer, BE32(w, 5)));
                    return;
                }
            }
            if (n >= 9 && (type == FxpHandle || type == FxpStatus))
            {
                uint id = (uint)BE32(buf, pos + 5);
                lock (_gate)
                    if (_opening.TryGetValue(id, out var path))
                    {
                        _opening.Remove(id);
                        if (type == FxpHandle) _readOnly[Str(buf, pos + 9, n - 9)] = path;
                    }
            }
            SendToClient(buf, pos, n);
        }

        /// <summary>Closes for real, on the phone, the files sshfs closed a while ago.</summary>
        private void CloseKept()
        {
            while (!_ended)
            {
                Thread.Sleep(1000);
                lock (_gate)
                {
                    if (_kept.Count == 0) continue;
                    foreach (var path in new List<string>(_kept.Keys))
                    {
                        var k = _kept[path];
                        if (unchecked(Environment.TickCount - k.at) < KeepOpenMs) continue;
                        _kept.Remove(path);
                        if (_files.TryGetValue(k.handle, out var f)) { Drop(f); _files.Remove(k.handle); }
                        try { SendToPhone(CloseRequest(NewId(), k.handle)); } catch { return; }
                    }
                }
            }
        }

        // ------------------------------------------------------------ plumbing

        /// <summary>An id for a request of ours (high bit set: sshfs counts up from 1). Under _gate.</summary>
        private uint NewId()
        {
            var id = _nextId++;
            if (_nextId == 0) _nextId = 0x80000000;
            return id;
        }

        /// <summary>The SFTP string at <paramref name="o"/> (length, then bytes), one char per byte.</summary>
        private static string Str(byte[] b, int o, int max = int.MaxValue)
        {
            int len = BE32(b, o);
            if (len < 0 || o + 4 + len > b.Length || 4 + len > max) return "";
            return Encoding.GetEncoding(28591).GetString(b, o + 4, len);
        }

        private static byte[] HandleAnswer(uint id, string handle)
        {
            var h = Encoding.GetEncoding(28591).GetBytes(handle);
            var p = new byte[4 + 1 + 4 + 4 + h.Length];
            PutBE32(p, 0, p.Length - 4);
            p[4] = FxpHandle;
            PutBE32(p, 5, (int)id);
            PutBE32(p, 9, h.Length);
            Buffer.BlockCopy(h, 0, p, 13, h.Length);
            return p;
        }

        /// <summary>SSH_FXP_STATUS, SSH_FX_OK, empty message and language.</summary>
        private static byte[] OkAnswer(uint id)
        {
            var p = new byte[4 + 17];
            PutBE32(p, 0, 17);
            p[4] = FxpStatus;
            PutBE32(p, 5, (int)id);
            return p;
        }

        private static byte[] CloseRequest(uint id, string handle)
        {
            var h = Encoding.GetEncoding(28591).GetBytes(handle);
            var p = new byte[4 + 1 + 4 + 4 + h.Length];
            PutBE32(p, 0, p.Length - 4);
            p[4] = FxpClose;
            PutBE32(p, 5, (int)id);
            PutBE32(p, 9, h.Length);
            Buffer.BlockCopy(h, 0, p, 13, h.Length);
            return p;
        }

        private void SendToPhone(byte[] p) => SendToPhone(p, 0, p.Length);

        private void SendToClient(byte[] p) => SendToClient(p, 0, p.Length);

        private void SendToClient(byte[] b, int off, int n)
        {
            lock (_toClient) { _client.Write(b, off, n); _client.Flush(); }
        }

        private void SendToPhone(byte[] b, int off, int n)
        {
            lock (_toPhone) AdbShell.Write(_net, b, off, n);
        }

        private static byte[] ReadRequest(uint id, string handle, long off, int len)
        {
            var h = Encoding.GetEncoding(28591).GetBytes(handle);
            var p = new byte[4 + 1 + 4 + 4 + h.Length + 8 + 4];
            PutBE32(p, 0, p.Length - 4);
            p[4] = FxpRead;
            PutBE32(p, 5, (int)id);
            PutBE32(p, 9, h.Length);
            Buffer.BlockCopy(h, 0, p, 13, h.Length);
            PutBE32(p, 13 + h.Length, (int)(off >> 32));
            PutBE32(p, 17 + h.Length, (int)off);
            PutBE32(p, 21 + h.Length, len);
            return p;
        }

        private static byte[] WithId(byte[] answer, int id)
        {
            var p = (byte[])answer.Clone();
            PutBE32(p, 5, id);
            return p;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        private static int BE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        private static long BE64(byte[] b, int o) => ((long)(uint)BE32(b, o) << 32) | (uint)BE32(b, o + 4);

        private static void PutBE32(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
    }

    /// <summary>
    /// Runs a command on the phone through the adb server's shell v2 protocol: its stdin and
    /// stdout byte for byte, stderr dropped.
    ///
    /// Why not "adb shell -T": on Windows the adb client writes stdout in text mode and turns
    /// every LF byte into CR LF, which corrupts binary data. And "adb exec-out" is binary safe
    /// but does not forward stdin.
    /// </summary>
    internal static class AdbShell
    {
        private const byte IdStdin = 0, IdStdout = 1, IdExit = 3, IdCloseStdin = 4;
        private const int MaxData = 65536;

        /// <summary>A connection to the adb server running <paramref name="command"/> on the phone.</summary>
        public static NetworkStream Open(string serial, string command)
        {
            var tcp = new TcpClient("127.0.0.1", AppInfo.AdbPort) { NoDelay = true };
            var net = tcp.GetStream();
            try
            {
                Request(net, "host:transport:" + serial);
                Request(net, "shell,v2,raw:" + command);
                return net;
            }
            catch { net.Dispose(); tcp.Close(); throw; }
        }

        /// <summary>Sends bytes to the command's stdin, in shell packets of at most 64 KB. One write per packet.</summary>
        public static void Write(NetworkStream s, byte[] b, int off, int n)
        {
            while (n > 0)
            {
                int k = Math.Min(n, MaxData);
                var p = new byte[5 + k];
                p[0] = IdStdin;
                p[1] = (byte)k; p[2] = (byte)(k >> 8); p[3] = (byte)(k >> 16); p[4] = (byte)(k >> 24);
                Buffer.BlockCopy(b, off, p, 5, k);
                s.Write(p, 0, p.Length);
                off += k;
                n -= k;
            }
        }

        public static void CloseInput(NetworkStream s) => s.Write(new byte[] { IdCloseStdin, 0, 0, 0, 0 }, 0, 5);

        /// <summary>The command's stdout, as it comes, until it exits or the link drops.</summary>
        public static IEnumerable<(byte[] data, int n)> ReadOutput(NetworkStream s)
        {
            var header = new byte[5];
            var data = new byte[MaxData];
            while (true)
            {
                bool ok;
                try { ok = ReadExact(s, header, 5); } catch { ok = false; }
                if (!ok) yield break;
                int len = BitConverter.ToInt32(header, 1);
                if (len > data.Length) data = new byte[len];
                try { ok = len == 0 || ReadExact(s, data, len); } catch { ok = false; }
                if (!ok) yield break;
                if (header[0] == IdStdout && len > 0) yield return (data, len);
                else if (header[0] == IdExit) yield break;
                // stderr (2) is dropped: it must never mix into the data stream.
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
}

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Phonestick
{
    /// <summary>
    /// "Phonestick.exe --pipe SERIAL": the external ssh command rclone uses to reach the
    /// phone's SFTP server. It runs the server script through the adb server's shell v2
    /// protocol and relays stdin and stdout byte for byte.
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

                    var up = new Thread(() =>
                    {
                        // Header and data in one buffer, sent with one write: one TCP segment
                        // per packet instead of two (NoDelay sends each write at once).
                        var buf = new byte[5 + 65536];
                        try
                        {
                            int n;
                            while ((n = stdin.Read(buf, 5, buf.Length - 5)) > 0)
                                lock (sendLock) WritePacket(net, IdStdin, buf, n);
                            lock (sendLock) WritePacket(net, IdCloseStdin, buf, 0);
                        }
                        catch { }
                    }) { IsBackground = true };
                    up.Start();

                    var header = new byte[5];
                    var data = new byte[65536];
                    while (ReadExact(net, header, 5))
                    {
                        int len = BitConverter.ToInt32(header, 1);
                        if (len > data.Length) data = new byte[len];
                        if (len > 0 && !ReadExact(net, data, len)) break;
                        if (header[0] == IdStdout)
                        {
                            stdout.Write(data, 0, len);
                            stdout.Flush();
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
}

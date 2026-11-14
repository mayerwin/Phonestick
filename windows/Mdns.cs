using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Phonestick
{
    /// <summary>
    /// Asks phones directly (DNS-SD over unicast mDNS, RFC 6762 5.5) whether they offer wireless
    /// debugging, and on which port.
    ///
    /// Why not adb's own discovery: a freshly started adb server can take minutes to see a phone
    /// (seen 2026-11-14: over two minutes, so "Open phone files" failed right after setup).
    /// Why unicast to each address instead of the multicast group: Windows Firewall drops
    /// multicast answers unless the app has an inbound rule, and creating one means a prompt.
    /// A unicast query is answered straight back to the sending socket, which the firewall
    /// lets through like any reply, so no rule and no prompt are ever needed. The socket is
    /// never bound to a fixed port, so Windows never asks about it either.
    /// </summary>
    internal static class Mdns
    {
        public const string Connect = "_adb-tls-connect._tcp.local";
        public const string Pairing = "_adb-tls-pairing._tcp.local";

        public sealed class Service
        {
            public string Instance; // e.g. "adb-65200DLCH000ED-54VSIL"
            public string Host;     // target host name from SRV
            public int Port;
            public IPAddress Address;
            public string Endpoint => Address + ":" + Port;
        }

        /// <summary>
        /// Services of this type. Asks <paramref name="first"/> (the phone's last known address)
        /// alone first; if it does not answer, every address of the local networks (up to /22).
        /// </summary>
        public static List<Service> Find(string type, IPAddress first = null)
        {
            try
            {
                if (first != null)
                {
                    var quick = Query(type, new[] { first }, 600);
                    if (quick.Count > 0) return quick;
                }
                return Query(type, SubnetHosts(), 1500);
            }
            catch (Exception ex)
            {
                Log.Write("mdns: " + ex.Message);
                return new List<Service>();
            }
        }

        public static List<Service> Query(string type, IEnumerable<IPAddress> hosts, int waitMs)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var srv = new Dictionary<string, (string host, int port)>(StringComparer.OrdinalIgnoreCase);
            var addr = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
            var from = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
            var targets = hosts.Select(a => new IPEndPoint(a, 5353)).ToList();
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                var query = BuildQuery(type, 12 /* PTR */);
                void SendAll() { foreach (var t in targets) try { s.SendTo(query, t); } catch { } }
                SendAll();
                var buf = new byte[9000];
                var until = DateTime.UtcNow.AddMilliseconds(waitMs);
                bool resent = false;
                while (DateTime.UtcNow < until)
                {
                    if (!resent && DateTime.UtcNow > until.AddMilliseconds(-waitMs / 2)) { resent = true; SendAll(); }
                    if (!s.Poll(100000, SelectMode.SelectRead)) continue;
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref ep); } catch { continue; }
                    try { Parse(buf, n, type, found, srv, addr, ((IPEndPoint)ep).Address, from); } catch { }
                    if (found.Count > 0 && found.All(k => srv.ContainsKey(k)) && resent) break;
                }
            }
            var list = new List<Service>();
            foreach (var inst in found)
            {
                if (!srv.TryGetValue(inst, out var sp)) continue;
                // The A record when given; otherwise the address the answer came from.
                if (!addr.TryGetValue(sp.host, out var ip) && !from.TryGetValue(inst, out ip)) continue;
                list.Add(new Service { Instance = inst.Split('.')[0], Host = sp.host, Port = sp.port, Address = ip });
            }
            return list;
        }

        /// <summary>Every host address of the local IPv4 networks (prefix /22 or longer), own addresses excluded.</summary>
        private static List<IPAddress> SubnetHosts()
        {
            var result = new List<IPAddress>();
            var own = new HashSet<uint>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    int prefix = ua.PrefixLength;
                    if (prefix < 22 || prefix > 30) continue;
                    uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                    own.Add(ip);
                    uint mask = prefix == 0 ? 0 : 0xFFFFFFFFu << (32 - prefix);
                    uint net = ip & mask, bcast = net | ~mask;
                    for (uint h = net + 1; h < bcast; h++)
                        result.Add(new IPAddress(new[] { (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h }));
                }
            }
            return result.Where(a => { var b = a.GetAddressBytes(); return !own.Contains((uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3])); })
                         .Distinct().ToList();
        }

        private static byte[] BuildQuery(string name, int qtype)
        {
            var b = new List<byte> { 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }; // id 1, flags 0, 1 question
            foreach (var label in name.Split('.'))
            {
                var l = Encoding.UTF8.GetBytes(label);
                b.Add((byte)l.Length);
                b.AddRange(l);
            }
            b.Add(0);
            b.Add((byte)(qtype >> 8)); b.Add((byte)qtype);
            // Class IN with the "unicast response" bit (RFC 6762 5.4): Android then answers
            // straight to this socket. Without it, it ignores a query sent to its own address.
            b.Add(0x80); b.Add(1);
            return b.ToArray();
        }

        private static void Parse(byte[] p, int n, string type, HashSet<string> found,
            Dictionary<string, (string, int)> srv, Dictionary<string, IPAddress> addr,
            IPAddress sender, Dictionary<string, IPAddress> from)
        {
            if (n < 12) return;
            int flags = p[2] << 8 | p[3];
            if ((flags & 0x8000) == 0) return; // a query, not a response
            int qd = p[4] << 8 | p[5];
            int records = (p[6] << 8 | p[7]) + (p[8] << 8 | p[9]) + (p[10] << 8 | p[11]);
            int i = 12;
            for (int q = 0; q < qd; q++) { ReadName(p, n, ref i); i += 4; }
            for (int r = 0; r < records && i < n; r++)
            {
                var name = ReadName(p, n, ref i);
                if (i + 10 > n) return;
                int rtype = p[i] << 8 | p[i + 1];
                int len = p[i + 8] << 8 | p[i + 9];
                i += 10;
                int start = i;
                if (start + len > n) return;
                if (rtype == 12 && name.Equals(type, StringComparison.OrdinalIgnoreCase)) // PTR
                {
                    int j = start;
                    var inst = ReadName(p, n, ref j);
                    found.Add(inst);
                    from[inst] = sender;
                }
                else if (rtype == 33 && len >= 7) // SRV
                {
                    int port = p[start + 4] << 8 | p[start + 5];
                    int j = start + 6;
                    srv[name] = (ReadName(p, n, ref j), port);
                    if (name.EndsWith(type, StringComparison.OrdinalIgnoreCase)) { found.Add(name); from[name] = sender; }
                }
                else if (rtype == 1 && len == 4) // A
                {
                    addr[name] = new IPAddress(new[] { p[start], p[start + 1], p[start + 2], p[start + 3] });
                }
                i = start + len;
            }
        }

        private static string ReadName(byte[] p, int n, ref int i)
        {
            var parts = new List<string>();
            int pos = i, jumps = 0;
            bool jumped = false;
            while (pos < n)
            {
                int len = p[pos];
                if (len == 0) { pos++; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= n || ++jumps > 20) break;
                    int target = (len & 0x3F) << 8 | p[pos + 1];
                    if (!jumped) i = pos + 2;
                    jumped = true;
                    pos = target;
                    continue;
                }
                pos++;
                if (pos + len > n) break;
                parts.Add(Encoding.UTF8.GetString(p, pos, len));
                pos += len;
            }
            if (!jumped) i = pos;
            return string.Join(".", parts);
        }
    }
}

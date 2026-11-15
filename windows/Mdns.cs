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
            public string Type;     // Connect or Pairing
            public string Instance; // e.g. "adb-65200DLCH000ED-54VSIL"
            public string Host;     // target host name from SRV
            public int Port;
            public IPAddress Address;
            public string Endpoint => Address + ":" + Port;
        }

        public static List<Service> Find(string type, IPAddress first = null, Func<Service, bool> want = null) =>
            Find(new[] { type }, first, want);

        /// <summary>Addresses that answered before: asked first, every time.</summary>
        private static readonly HashSet<IPAddress> Responders = new HashSet<IPAddress>();
        private static DateTime _lastSweep = DateTime.MinValue;
        /// <summary>A sweep of the whole network at most this often (a thousand or so packets each).</summary>
        private static readonly TimeSpan SweepGap = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Services of these types (one query asks for all of them). Asks <paramref name="first"/>
        /// (the phone's last known address) and the phones that answered before; if no wanted
        /// service answers there, every address of the local networks, at most every few seconds
        /// (callers ask in a loop).
        /// </summary>
        public static List<Service> Find(string[] types, IPAddress first = null, Func<Service, bool> want = null)
        {
            try
            {
                var near = new List<IPAddress>();
                if (first != null) near.Add(first);
                lock (Responders) near.AddRange(Responders.Where(a => !a.Equals(first)));
                var quick = new List<Service>();
                if (near.Count > 0)
                {
                    quick = Query(types, near, 600, want);
                    if (want != null && quick.Any(want)) return quick;
                }
                if (DateTime.UtcNow - _lastSweep < SweepGap) return quick;
                _lastSweep = DateTime.UtcNow;
                var all = Query(types, SubnetHosts(), 1500, want);
                all.AddRange(quick.Where(q => !all.Any(a => a.Endpoint == q.Endpoint && a.Instance == q.Instance)));
                return all;
            }
            catch (Exception ex)
            {
                Log.Write("mdns: " + ex.Message);
                return new List<Service>();
            }
        }

        /// <summary>
        /// Asks these hosts and collects the answers for up to <paramref name="waitMs"/>. Ends early once
        /// every answer is complete and either one host was asked or a <paramref name="want"/>ed service
        /// answered; otherwise waits for the second round (other phones may still answer).
        /// </summary>
        public static List<Service> Query(string[] types, IEnumerable<IPAddress> hosts, int waitMs, Func<Service, bool> want = null)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var srv = new Dictionary<string, (string host, int port)>(StringComparer.OrdinalIgnoreCase);
            var addr = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
            var from = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
            var targets = hosts.Select(a => new IPEndPoint(a, 5353)).ToList();

            List<Service> Build()
            {
                var list = new List<Service>();
                foreach (var inst in found)
                {
                    if (!srv.TryGetValue(inst, out var sp)) continue;
                    // The address the answer came from: each query went to one address, so that one
                    // is reachable (a phone may also list hotspot or VPN addresses in A records,
                    // and two phones can share a host name). The A record only as a fallback.
                    if (!from.TryGetValue(inst, out var ip) && !addr.TryGetValue(sp.host, out ip)) continue;
                    list.Add(new Service
                    {
                        Type = types.FirstOrDefault(t => inst.EndsWith("." + t, StringComparison.OrdinalIgnoreCase)),
                        Instance = inst.Split('.')[0],
                        Host = sp.host,
                        Port = sp.port,
                        Address = ip,
                    });
                }
                return list;
            }

            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                // Hosts without mDNS answer "port unreachable": without this, each such answer
                // makes the next receive fail (an exception per host during a sweep).
                try { s.IOControl(unchecked((int)0x9800000C) /* SIO_UDP_CONNRESET */, new byte[4], null); } catch { }
                var query = BuildQuery(types, 12 /* PTR */);
                void SendAll() { foreach (var t in targets) try { s.SendTo(query, t); } catch { } }
                SendAll();
                var buf = new byte[9000];
                var until = DateTime.UtcNow.AddMilliseconds(waitMs);
                var resendAt = DateTime.UtcNow.AddMilliseconds(waitMs / 2);
                bool resent = false;
                while (DateTime.UtcNow < until)
                {
                    // Once more halfway, for a packet lost on Wi-Fi or a phone that was dozing.
                    if (!resent && DateTime.UtcNow > resendAt) { resent = true; SendAll(); }
                    if (!s.Poll(100000, SelectMode.SelectRead)) continue;
                    EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref ep); } catch { continue; }
                    try { Parse(buf, n, types, found, srv, addr, ((IPEndPoint)ep).Address, from); } catch { }
                    if (found.Count > 0 && found.All(k => srv.ContainsKey(k))
                        && (resent || targets.Count == 1 || (want != null && Build().Any(want)))) break;
                }
            }
            var result = Build();
            lock (Responders)
                foreach (var x in result)
                    if (Responders.Count < 32) Responders.Add(x.Address);
            return result;
        }

        /// <summary>
        /// Every host address of the local IPv4 networks, own addresses excluded. Only networks
        /// with a gateway (the phone's Wi-Fi has one; the virtual adapters of VMs usually do not),
        /// unless there are none. A network bigger than /22 is swept in the /22 around this PC.
        /// </summary>
        private static List<IPAddress> SubnetHosts()
        {
            var own = new HashSet<uint>();
            var withGw = new List<(uint net, uint bcast)>();
            var others = new List<(uint net, uint bcast)>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                          && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    int prefix = ua.PrefixLength;
                    if (prefix < 8 || prefix > 30) continue;
                    uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                    own.Add(ip);
                    uint mask = 0xFFFFFFFFu << (32 - Math.Max(prefix, 22));
                    uint net = ip & mask;
                    (gw ? withGw : others).Add((net, net | ~mask));
                }
            }
            var result = new List<IPAddress>();
            var seen = new HashSet<uint>();
            foreach (var (net, bcast) in withGw.Count > 0 ? withGw : others)
                for (uint h = net + 1; h < bcast; h++)
                    if (!own.Contains(h) && seen.Add(h))
                        result.Add(new IPAddress(new[] { (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h }));
            return result;
        }

        private static byte[] BuildQuery(string[] names, int qtype)
        {
            var b = new List<byte> { 0, 1, 0, 0, 0, (byte)names.Length, 0, 0, 0, 0, 0, 0 }; // id 1, flags 0, n questions
            foreach (var name in names)
            {
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
            }
            return b.ToArray();
        }

        private static void Parse(byte[] p, int n, string[] types, HashSet<string> found,
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
                if (rtype == 12 && types.Any(t => name.Equals(t, StringComparison.OrdinalIgnoreCase))) // PTR
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
                    if (types.Any(t => name.EndsWith("." + t, StringComparison.OrdinalIgnoreCase))) { found.Add(name); from[name] = sender; }
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

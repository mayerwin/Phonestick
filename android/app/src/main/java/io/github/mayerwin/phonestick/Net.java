package io.github.mayerwin.phonestick;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.net.RouteInfo;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * Recognises networks without location permission (Android hides the Wi-Fi name
 * from apps without it). A network is identified by what its router hands out:
 * gateway, IPv4 subnet, DNS servers and DNS domain.
 */
final class Net {
    private Net() {}

    static NetworkRequest wifiRequest() {
        return new NetworkRequest.Builder()
                .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .build();
    }

    /** The current Wi-Fi network, or null. */
    static Network currentWifi(Context c) {
        ConnectivityManager cm = c.getSystemService(ConnectivityManager.class);
        // Prefer the network Android routes through: during a switch, or with a second
        // (local-only) Wi-Fi such as a camera's setup hotspot, the list can hold several.
        Network active = cm.getActiveNetwork();
        if (active != null) {
            NetworkCapabilities ac = cm.getNetworkCapabilities(active);
            if (ac != null && ac.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return active;
        }
        for (Network n : cm.getAllNetworks()) {
            NetworkCapabilities nc = cm.getNetworkCapabilities(n);
            if (nc != null && nc.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return n;
        }
        return null;
    }

    private static List<String> parts(Context c, Network n) {
        List<String> out = new ArrayList<>();
        if (n == null) return out;
        LinkProperties lp = c.getSystemService(ConnectivityManager.class).getLinkProperties(n);
        if (lp == null) return out;
        for (RouteInfo r : lp.getRoutes()) {
            InetAddress gw = r.getGateway();
            if (r.isDefaultRoute() && gw instanceof Inet4Address) out.add("gw " + gw.getHostAddress());
        }
        for (LinkAddress la : lp.getLinkAddresses()) {
            if (la.getAddress() instanceof Inet4Address) {
                out.add("net " + subnet(la.getAddress().getAddress(), la.getPrefixLength()));
            }
        }
        for (InetAddress d : lp.getDnsServers()) {
            if (d instanceof Inet4Address) out.add("dns " + d.getHostAddress());
        }
        if (lp.getDomains() != null && !lp.getDomains().isEmpty()) out.add("dom " + lp.getDomains());
        Collections.sort(out);
        return out;
    }

    private static String subnet(byte[] a, int len) {
        byte[] m = a.clone();
        for (int i = 0; i < m.length; i++) {
            int bits = Math.max(0, Math.min(8, len - i * 8));
            m[i] = (byte) (m[i] & (0xFF << (8 - bits)));
        }
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < m.length; i++) sb.append(i == 0 ? "" : ".").append(m[i] & 0xFF);
        return sb.append('/').append(len).toString();
    }

    /**
     * Stable id for the network, or null when there is nothing to go on. Only the gateway
     * and the subnet count: DNS servers and the DNS domain can differ between reconnects
     * (mesh nodes, IPv6 DNS, Private DNS), which used to make a known network look new.
     */
    static String fingerprint(Context c, Network n) {
        List<String> p = new ArrayList<>();
        for (String s : parts(c, n)) if (s.startsWith("gw ") || s.startsWith("net ")) p.add(s);
        return hash(p);
    }

    /** Current and older fingerprint formulas, so networks approved by older versions still match. */
    static String[] fingerprints(Context c, Network n) {
        return new String[] { fingerprint(c, n), hash(parts(c, n)) };
    }

    private static String hash(List<String> p) {
        if (p.isEmpty()) return null;
        try {
            byte[] h = MessageDigest.getInstance("SHA-256")
                    .digest(String.join(";", p).getBytes(StandardCharsets.UTF_8));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 8; i++) sb.append(String.format("%02x", h[i]));
            return sb.toString();
        } catch (Exception e) {
            return null;
        }
    }

    /** Human readable summary, e.g. "192.168.1.0/24 via 192.168.1.1 (lan)". */
    static String label(Context c, Network n) {
        String net = "", gw = "", dom = "";
        for (String s : parts(c, n)) {
            if (s.startsWith("net ")) net = s.substring(4);
            else if (s.startsWith("gw ")) gw = s.substring(3);
            else if (s.startsWith("dom ")) dom = s.substring(4);
        }
        String l = net + (gw.isEmpty() ? "" : " via " + gw);
        return dom.isEmpty() ? l : l + " (" + dom + ")";
    }
}

package io.github.mayerwin.phonestick;

import android.content.Context;
import android.content.SharedPreferences;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;

/** All persistent state. Small, local, never leaves the phone. */
final class Prefs {
    private static final String FILE = "phonestick";
    private static final int LOG_MAX = 60;
    private final SharedPreferences sp;

    Prefs(Context c) {
        sp = c.getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    boolean armed() { return sp.getBoolean("armed", false); }
    void armed(boolean v) { sp.edit().putBoolean("armed", v).commit(); }

    /** USB lockdown starts with the first arm, so setup over USB is not cut off midway. */
    boolean lockdownActive() { return sp.getBoolean("lockdownActive", false); }
    void lockdownActive(boolean v) { sp.edit().putBoolean("lockdownActive", v).apply(); }

    boolean allowUsb() { return sp.getBoolean("allowUsb", false); }
    void allowUsb(boolean v) { sp.edit().putBoolean("allowUsb", v).apply(); }

    long attemptHandle() { return sp.getLong("attemptHandle", 0); }
    long attemptAt() { return sp.getLong("attemptAt", 0); }
    int attemptCount() { return sp.getInt("attemptCount", 0); }
    void attempt(long handle, long at, int count) {
        sp.edit().putLong("attemptHandle", handle).putLong("attemptAt", at).putInt("attemptCount", count).apply();
    }

    /** When the app last switched wireless debugging on (any reason). */
    long lastCheckAt() { return sp.getLong("lastCheckAt", 0); }
    void lastCheckAt(long v) { sp.edit().putLong("lastCheckAt", v).apply(); }

    long lastWriteAt() { return sp.getLong("lastWriteAt", 0); }
    void lastWriteAt(long v) { sp.edit().putLong("lastWriteAt", v).commit(); }

    /** Optional 15-minute safety check while access is on (off by default). */
    boolean safetyCheck() { return sp.getBoolean("safetyCheck", false); }
    void safetyCheck(boolean v) { sp.edit().putBoolean("safetyCheck", v).apply(); }

    /** Whether our tile is in Quick Settings (from the tile's added/removed callbacks). */
    boolean tileAdded() { return sp.getBoolean("tileAdded", false); }
    void tileAdded(boolean v) { sp.edit().putBoolean("tileAdded", v).apply(); }

    boolean askedTile() { return sp.getBoolean("askedTile", false); }
    void askedTile(boolean v) { sp.edit().putBoolean("askedTile", v).apply(); }

    // ------------------------------------------------------------ event log

    /** Appends one line to a small on-phone event log (newest last), to diagnose drops. */
    synchronized void log(String what) {
        String old = sp.getString("log", "");
        String line = new SimpleDateFormat("MM-dd HH:mm:ss", Locale.US).format(new Date()) + "  " + what;
        String[] lines = old.isEmpty() ? new String[0] : old.split("\n");
        StringBuilder sb = new StringBuilder();
        for (int i = Math.max(0, lines.length - (LOG_MAX - 1)); i < lines.length; i++) sb.append(lines[i]).append('\n');
        sb.append(line);
        sp.edit().putString("log", sb.toString()).commit();
    }

    String log() { return sp.getString("log", ""); }

    // ------------------------------------------------------------ approved networks

    /** Approved networks, stored as "fingerprint|label". */
    Set<String> approved() {
        return new HashSet<>(sp.getStringSet("approved", new HashSet<>()));
    }

    boolean isApproved(String fingerprint) {
        if (fingerprint == null) return false;
        for (String e : approved()) {
            if (e.startsWith(fingerprint + "|")) return true;
        }
        return false;
    }

    /** True if any of the network's fingerprints (current or older formula) was approved. */
    boolean isApproved(String[] fingerprints) {
        for (String f : fingerprints) if (isApproved(f)) return true;
        return false;
    }

    void approve(String fingerprint, String label) {
        if (fingerprint == null || isApproved(fingerprint)) return;
        Set<String> s = approved();
        s.add(fingerprint + "|" + label);
        sp.edit().putStringSet("approved", s).apply();
    }

    void forget(String entry) {
        Set<String> s = approved();
        s.remove(entry);
        sp.edit().putStringSet("approved", s).apply();
    }
}

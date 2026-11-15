package io.github.mayerwin.phonestick;

import android.Manifest;
import android.content.ContentResolver;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;

/** Reads and writes the system debugging switches. */
final class Adb {
    static final String WIFI = "adb_wifi_enabled";              // Wireless debugging
    static final String USB = Settings.Global.ADB_ENABLED;      // USB debugging
    static final String DEV = Settings.Global.DEVELOPMENT_SETTINGS_ENABLED;

    private Adb() {}

    static boolean canWrite(Context c) {
        return c.checkSelfPermission(Manifest.permission.WRITE_SECURE_SETTINGS)
                == PackageManager.PERMISSION_GRANTED;
    }

    /**
     * Whether the real value of the debugging switches can be read. Since Android 17 the
     * usual read shows apps 0 whatever the real value. The settings provider's direct "get"
     * call still answers with the real value; this is checked on Developer options, which
     * is always on where Wireless debugging is used: a redacted read shows 0 there. Should
     * a later Android redact that call too, this turns false and the app falls back to
     * writing blind.
     */
    static boolean readable(Context c) {
        return Build.VERSION.SDK_INT < 37 || "1".equals(direct(c, DEV));
    }

    static int get(Context c, String key) {
        if (Build.VERSION.SDK_INT < 37) return Settings.Global.getInt(c.getContentResolver(), key, 0);
        String v = direct(c, key);
        try { return v == null ? 0 : Integer.parseInt(v.trim()); } catch (NumberFormatException e) { return 0; }
    }

    /** The settings provider's own "get" call, without the generation tracking the usual read adds. */
    private static String direct(Context c, String key) {
        try {
            Bundle b = c.getContentResolver().call(Settings.Global.CONTENT_URI, "GET_global", key, null);
            return b == null ? null : b.getString(Settings.NameValueTable.VALUE);
        } catch (RuntimeException e) {
            return null;
        }
    }

    static boolean put(Context c, String key, int value) {
        ContentResolver cr = c.getContentResolver();
        try {
            return Settings.Global.putInt(cr, key, value);
        } catch (SecurityException e) {
            return false;
        }
    }
}

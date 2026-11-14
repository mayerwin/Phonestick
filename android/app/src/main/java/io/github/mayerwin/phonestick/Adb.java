package io.github.mayerwin.phonestick;

import android.Manifest;
import android.content.ContentResolver;
import android.content.Context;
import android.content.pm.PackageManager;
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

    /** Up to Android 16 apps can read the debugging switches; since 17 they are shown 0. */
    static boolean readable() {
        return android.os.Build.VERSION.SDK_INT < 37;
    }

    static int get(Context c, String key) {
        return Settings.Global.getInt(c.getContentResolver(), key, 0);
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

package io.github.mayerwin.phonestick;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.net.Network;

/**
 * Commands from the computer over adb, for example:
 *   adb shell am broadcast -a io.github.mayerwin.phonestick.STATUS -n io.github.mayerwin.phonestick/.CommandReceiver
 * Actions: ARM, DISARM, STATUS, LOG. The manifest requires the sender to hold
 * WRITE_SECURE_SETTINGS, which the adb shell has and ordinary apps do not.
 * Result code: 1 if armed afterwards. Result data: a one-line status, or the event log.
 */
public class CommandReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context c, Intent intent) {
        String a = intent.getAction();
        String pkg = c.getPackageName();
        if ((pkg + ".ARM").equals(a)) Access.arm(c);
        else if ((pkg + ".DISARM").equals(a)) Access.disarm(c);

        Prefs p = new Prefs(c);
        setResultCode(p.armed() ? 1 : 0);
        if ((pkg + ".LOG").equals(a)) {
            setResultData(p.log());
            return;
        }
        Network n = Net.currentWifi(c);
        setResultData("armed=" + (p.armed() ? 1 : 0)
                + " network=" + (n == null ? "none" : p.isApproved(Net.fingerprints(c, n)) ? "approved" : "unknown")
                + " approvedCount=" + p.approved().size()
                + " usbAllowed=" + (p.allowUsb() ? 1 : 0)
                + " permission=" + (Adb.canWrite(c) ? 1 : 0)
                + " dozeExempt=" + (Access.dozeExempt(c) ? 1 : 0)
                + " tile=" + (p.tileAdded() ? 1 : 0)
                + " wifiDebugging=" + (Adb.readable(c) ? String.valueOf(Adb.get(c, Adb.WIFI)) : "unknown"));
    }
}

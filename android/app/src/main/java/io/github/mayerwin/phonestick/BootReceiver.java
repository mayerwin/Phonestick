package io.github.mayerwin.phonestick;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** After a reboot or an app update, put the event hooks back. */
public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context c, Intent intent) {
        String a = intent.getAction();
        if (Intent.ACTION_BOOT_COMPLETED.equals(a) || Intent.ACTION_MY_PACKAGE_REPLACED.equals(a)) {
            new Prefs(c).log(Intent.ACTION_BOOT_COMPLETED.equals(a) ? "woken: phone started" : "woken: app updated");
            Access.restore(c);
        }
    }
}

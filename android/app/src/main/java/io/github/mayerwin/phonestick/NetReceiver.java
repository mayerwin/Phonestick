package io.github.mayerwin.phonestick;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Woken by Android when a Wi-Fi network becomes available. */
public class NetReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context c, Intent intent) {
        Prefs p = new Prefs(c);
        if (!p.armed()) return;
        p.log("woken: Wi-Fi connected");
        Access.scheduleEnsure(c, 3_000);
        ListenService.start(c); // in case Android stopped it
    }
}

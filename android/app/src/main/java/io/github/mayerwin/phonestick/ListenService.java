package io.github.mayerwin.phonestick;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ServiceInfo;
import android.database.ContentObserver;
import android.net.ConnectivityManager;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.wifi.WifiInfo;
import android.os.Handler;
import android.os.IBinder;
import android.provider.Settings;

/**
 * Listens while access is on, so the reaction is immediate and needs no timer.
 *
 * Three kinds of events:
 *  - the Wireless debugging setting changed: switched back on within about a second
 *    (the content-trigger job does the same without the listener, but Android can
 *    hold jobs back for many seconds);
 *  - any change of the Wi-Fi connection, for example moving to another access point
 *    of the same network, which sends nothing a sleeping app can receive;
 *  - the screen coming on.
 * Android 17 does not always tell apps when it switches Wireless debugging off, so on
 * the last two the listener writes "on" again, which costs nothing when it is already on.
 *
 * It sleeps between events: no polling. The app never asks for the notification
 * permission, so on Android 13 and later the required notification is not shown; the
 * app only appears in the "active apps" list of Quick Settings. (On Android 11 and 12 it
 * shows as a silent, minimised notification.)
 */
public class ListenService extends Service {
    private static final String CHANNEL = "listen";
    private static final int NOTIFICATION_ID = 1;
    /** Wait for the change to settle before acting. */
    private static final long SETTLE_MS = 2_000;

    static volatile boolean running;

    private Handler h;
    /** This instance's listeners are registered (running can be cleared early by stop()). */
    private boolean hooked;
    private ConnectivityManager.NetworkCallback cb;
    private BroadcastReceiver screen;
    private ContentObserver setting;
    private boolean settingPending;
    private long handle;
    private int freq = -1;
    private String fp;
    private boolean nudgePending;
    private boolean roamPending;
    private String nudgeWhy;

    /** Starts the listener if access is on. Safe to call often. */
    static void start(Context c) {
        if (running || !new Prefs(c).armed()) return;
        try {
            c.startForegroundService(new Intent(c, ListenService.class));
        } catch (RuntimeException e) {
            // Android refuses background starts unless the app is exempt from battery
            // optimisation. The other hooks still work; the next foreground moment retries.
            new Prefs(c).log("listener could not start (" + e.getClass().getSimpleName()
                    + "), allow \"Wake without delay\" in the app");
        }
    }

    /**
     * Stops the listener. Through onStartCommand, not stopService: a service just started with
     * startForegroundService must still call startForeground, or Android ends the app.
     */
    static void stop(Context c) {
        running = false;
        try {
            c.startService(new Intent(c, ListenService.class)); // sees access off, stops itself
        } catch (RuntimeException e) {
            c.stopService(new Intent(c, ListenService.class));
        }
    }

    @Override
    public void onCreate() {
        super.onCreate();
        h = new Handler(getMainLooper());
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        try {
            goForeground(); // required after startForegroundService, also when stopping
        } catch (RuntimeException e) {
            new Prefs(this).log("listener could not start: " + e);
            stopSelf();
            return START_NOT_STICKY;
        }
        if (!new Prefs(this).armed()) {
            stopSelf();
            return START_NOT_STICKY;
        }
        running = true;
        if (!hooked) {
            hooked = true;
            hook();
            new Prefs(this).log("listener started");
        }
        return START_STICKY;
    }

    private void goForeground() {
        NotificationManager nm = getSystemService(NotificationManager.class);
        NotificationChannel ch = new NotificationChannel(CHANNEL,
                getString(R.string.listen_channel), NotificationManager.IMPORTANCE_MIN);
        ch.setShowBadge(false);
        nm.createNotificationChannel(ch);
        Notification n = new Notification.Builder(this, CHANNEL)
                .setSmallIcon(R.drawable.ic_tile_on)
                .setContentTitle(getString(R.string.listen_title))
                .setContentText(getString(R.string.listen_text))
                .setOngoing(true)
                .setContentIntent(PendingIntent.getActivity(this, 0,
                        new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE))
                .build();
        startForeground(NOTIFICATION_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
    }

    private void hook() {
        cb = new ConnectivityManager.NetworkCallback() {
            @Override
            public void onAvailable(Network n) {
                // A new connection: NetReceiver handles it (it also works without this
                // listener). Only remember it, so later changes can be compared.
                handle = n.getNetworkHandle();
                freq = -1;
                fp = Net.fingerprint(ListenService.this, n);
            }

            @Override
            public void onCapabilitiesChanged(Network n, NetworkCapabilities nc) {
                if (n.getNetworkHandle() != handle) return;
                // Called on every change of signal strength too: only another frequency (another
                // access point or band) means anything here. Android 11 does not give it.
                int f = nc.getTransportInfo() instanceof WifiInfo
                        ? ((WifiInfo) nc.getTransportInfo()).getFrequency() : -1;
                boolean moved = freq > 0 && f > 0 && f != freq;
                if (f > 0) freq = f;
                if (moved) roamSoon();
            }

            @Override
            public void onLinkPropertiesChanged(Network n, LinkProperties lp) {
                if (n.getNetworkHandle() != handle) return;
                String now = Net.fingerprint(lp);
                if (now != null && fp != null && !now.equals(fp)) roamSoon();
                else nudgeSoon("Wi-Fi changed");
                if (now != null) fp = now;
            }

            @Override
            public void onLost(Network n) {
                if (n.getNetworkHandle() != handle) return;
                // Follow the Wi-Fi that remains, if any (a second, local-only one was lost).
                Network cur = Net.currentWifi(ListenService.this);
                handle = cur == null ? 0 : cur.getNetworkHandle();
                freq = -1;
                fp = cur == null ? null : Net.fingerprint(ListenService.this, cur);
            }
        };
        try {
            getSystemService(ConnectivityManager.class).registerNetworkCallback(Net.wifiRequest(), cb, h);
        } catch (RuntimeException e) {
            cb = null;
            new Prefs(this).log("listener: network callback failed: " + e);
        }

        setting = new ContentObserver(h) {
            @Override
            public void onChange(boolean selfChange) {

                if (settingPending) return;
                settingPending = true;
                h.postDelayed(() -> {
                    settingPending = false;
                    try {
                        Access.ensureWifi(ListenService.this, null, true, "setting changed");
                    } catch (RuntimeException e) {
                        new Prefs(ListenService.this).log("listener: " + e);
                    }
                }, 1_000);
            }
        };
        getContentResolver().registerContentObserver(Settings.Global.getUriFor(Adb.WIFI), false, setting);

        screen = new BroadcastReceiver() {
            @Override
            public void onReceive(Context c, Intent i) {
                nudgeSoon("screen on");
            }
        };
        IntentFilter f = new IntentFilter(Intent.ACTION_SCREEN_ON);
        f.addAction(Intent.ACTION_USER_PRESENT);
        registerReceiver(screen, f, Context.RECEIVER_NOT_EXPORTED);
    }

    /** Probably another access point: a full, logged check. */
    private void roamSoon() {
        if (roamPending) return;
        roamPending = true;
        h.postDelayed(() -> {
            roamPending = false;
            try {
                Access.newAccessPoint(this);
                Access.ensureWifi(this, null, false, "access point changed");
            } catch (RuntimeException e) {
                new Prefs(this).log("listener: " + e);
            }
        }, SETTLE_MS);
    }

    /**
     * Any other change: write "on" again, quietly and spaced out (see Access.NUDGE_GAP_MS; a
     * change inside the gap is handled when it ends).
     */
    private void nudgeSoon(String why) {
        nudgeWhy = why;
        if (nudgePending) return;
        nudgePending = true;
        long gap = Access.nudgeGap(this);
        long since = System.currentTimeMillis() - new Prefs(this).lastWriteAt();
        long wait = (since >= 0 && since < gap) ? gap - since : 0;
        h.postDelayed(() -> {
            nudgePending = false;
            try {
                Access.nudge(this, nudgeWhy, false);
            } catch (RuntimeException e) {
                new Prefs(this).log("listener: " + e);
            }
        }, Math.max(SETTLE_MS, wait));
    }

    @Override
    public void onDestroy() {
        running = false;
        hooked = false;
        h.removeCallbacksAndMessages(null);
        if (cb != null) {
            try {
                getSystemService(ConnectivityManager.class).unregisterNetworkCallback(cb);
            } catch (RuntimeException ignored) {
            }
            cb = null;
        }
        if (setting != null) {
            getContentResolver().unregisterContentObserver(setting);
            setting = null;
        }
        if (screen != null) {
            try {
                unregisterReceiver(screen);
            } catch (RuntimeException ignored) {
            }
            screen = null;
        }
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}

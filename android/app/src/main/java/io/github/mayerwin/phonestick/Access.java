package io.github.mayerwin.phonestick;

import android.app.PendingIntent;
import android.app.job.JobInfo;
import android.app.job.JobScheduler;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.net.ConnectivityManager;
import android.net.Network;
import android.os.PowerManager;
import android.provider.Settings;
import android.service.quicksettings.TileService;

/**
 * The whole behaviour. Nothing here runs on a timer: work happens only when
 * Android reports an event (Wi-Fi connected, a debugging switch changed, boot).
 *
 *  - Wi-Fi connected: a PendingIntent network callback wakes NetReceiver, which
 *    queues a short job (Android needs a moment to learn the access point).
 *  - Wireless debugging switched off within a connection (roaming to another
 *    access point, or Android's own idle timeout): a content-trigger job on the
 *    setting wakes us.
 *  - USB debugging switched on while locked down: a content-trigger job turns it off.
 *
 * The app only ever WRITES the debugging switches, it never relies on reading them:
 * since Android 17 apps are shown 0 for developer settings (development, USB and
 * wireless debugging) whatever their real value. Writing a value that is already
 * set changes nothing. A short quiet period after each write stops the app from
 * reacting to the change it just caused; a change seen during that period is
 * re-checked when it ends instead of being dropped.
 *
 * Jobs are deferred by Doze unless the app is exempt from battery optimisation.
 * Setup exempts it (the app does no work while idle, so this costs nothing).
 */
final class Access {
    static final int JOB_WIFI_WATCH = 1;
    static final int JOB_ENSURE = 2;
    static final int JOB_USB_WATCH = 4;
    static final int JOB_SAFETY = 5;
    /**
     * Since Android 17 apps are not told when wireless debugging is switched off (the setting
     * is hidden from them, change notifications included), and Android switches it off by
     * itself after a while without use. An optional safety check (off by default) runs about
     * every 15 minutes on Wi-Fi while access is on and switches it back on. Each run takes a few
     * milliseconds.
     */
    private static final long SAFETY_PERIOD_MS = 15 * 60_000;
    private static final int JOB_PROBE_OLD = 3; // removed; cancelled on restore

    /** Ignore setting changes this soon after our own write (ours, or Android refusing it). */
    private static final long QUIET_MS = 10_000;
    /** Safety cap: at most this many writes per network connection per window. */
    private static final int MAX_ATTEMPTS = 4;
    private static final long ATTEMPT_WINDOW_MS = 10 * 60_000;
    private static final long SETTLE_MS = 3_000;

    private Access() {}

    // ---------------------------------------------------------------- public actions

    /**
     * Switch access on. Switching it on by hand while on Wi-Fi approves that network:
     * from then on access comes back by itself there.
     */
    static void arm(Context c) {
        Prefs p = new Prefs(c);
        p.armed(true);
        p.lockdownActive(true);
        p.attempt(0, 0, 0);
        p.lastWriteAt(0);
        Network n = Net.currentWifi(c);
        p.log("switched on" + (n == null ? " (no Wi-Fi now)" : " on " + Net.label(c, n)));
        if (n != null) {
            String fp = Net.fingerprint(c, n);
            if (fp == null) p.log("this network could not be identified (no IPv4 address yet), not approved");
            else if (!p.isApproved(Net.fingerprints(c, n))) {
                p.approve(fp, Net.label(c, n));
                p.log("network approved");
            }
        }
        enforceUsb(c);
        registerNetCallback(c);
        scheduleWifiWatch(c);
        scheduleSafety(c);
        if (n != null) ensureWifi(c, n, false, "switch on");
        refreshTile(c);
    }

    /** Works even without the permission, so the tile can always be switched off. */
    static void disarm(Context c) {
        Prefs p = new Prefs(c);
        p.armed(false);
        unregisterNetCallback(c);
        JobScheduler js = c.getSystemService(JobScheduler.class);
        js.cancel(JOB_WIFI_WATCH);
        js.cancel(JOB_ENSURE);
        js.cancel(JOB_SAFETY);
        boolean ok = Adb.put(c, Adb.WIFI, 0);
        p.log("switched off" + (ok ? "" : ": could not switch Wireless debugging off (permission missing), switch it off in Developer options"));
        refreshTile(c);
    }

    /** After boot, app update, or opening the app: put the event hooks back. */
    static void restore(Context c) {
        Prefs p = new Prefs(c);
        c.getSystemService(JobScheduler.class).cancel(JOB_PROBE_OLD);
        if (p.armed()) {
            rehook(c);
            scheduleSafety(c);
            scheduleEnsure(c, SETTLE_MS);
        } else if (Adb.canWrite(c)) {
            // Off means off, also after a crash between "switched off" and the write.
            Adb.put(c, Adb.WIFI, 0);
        }
        if (p.lockdownActive()) enforceUsb(c);
    }

    /** True once this process registered the network callback (a force-stop removes it). */
    private static boolean netHooked;

    /**
     * Puts back the hooks a force-stop or an aggressive battery saver removes, without
     * disturbing the ones still in place.
     */
    static void rehook(Context c) {
        if (!new Prefs(c).armed()) return;
        if (!netHooked) registerNetCallback(c);
        if (c.getSystemService(JobScheduler.class).getPendingJob(JOB_WIFI_WATCH) == null) scheduleWifiWatch(c);
    }

    /**
     * Quick Settings opened: make sure the hooks are there and wireless debugging is on
     * (since Android 17 apps are not told when Android switches it off). Quiet, at most
     * once a minute, and not counted as a retry.
     */
    static void tileShown(Context c) {
        Prefs p = new Prefs(c);
        if (!p.armed() || !Adb.canWrite(c)) return;
        rehook(c);
        if (p.lockdownActive()) enforceUsb(c);
        long now = System.currentTimeMillis();
        long since = now - p.lastWriteAt();
        if (since >= 0 && since < 60_000) return;
        Network n = Net.currentWifi(c);
        if (n == null || !p.isApproved(Net.fingerprints(c, n))) return;
        if (p.attemptHandle() == n.getNetworkHandle() && p.attemptCount() > MAX_ATTEMPTS) return; // Android refuses here
        p.lastWriteAt(now);
        if (!Adb.put(c, Adb.WIFI, 1)) p.log("tile shown: wireless debugging on FAILED");
    }

    static void setAllowUsb(Context c, boolean allow) {
        Prefs p = new Prefs(c);
        p.allowUsb(allow);
        p.log(allow ? "USB debugging allowed" : "USB debugging locked off");
        if (allow) c.getSystemService(JobScheduler.class).cancel(JOB_USB_WATCH);
        else enforceUsb(c);
    }

    /** True when Doze cannot hold back our wake-ups. */
    static boolean dozeExempt(Context c) {
        return c.getSystemService(PowerManager.class).isIgnoringBatteryOptimizations(c.getPackageName());
    }

    // ---------------------------------------------------------------- event handling

    /**
     * Turns wireless debugging on if this Wi-Fi network is approved.
     * @param fromSettingChange true when woken by a change of the setting itself
     */
    static void ensureWifi(Context c, Network n, boolean fromSettingChange, String why) {
        ensureWifi(c, n, fromSettingChange, why, false);
    }

    /** @param quiet do not log routine success (the periodic safety check) */
    static void ensureWifi(Context c, Network n, boolean fromSettingChange, String why, boolean quiet) {
        Prefs p = new Prefs(c);
        if (!p.armed()) return;
        if (!Adb.canWrite(c)) { p.log(why + ": skipped, permission missing"); return; }
        if (n == null) n = Net.currentWifi(c);
        if (n == null) { if (!quiet) p.log(why + ": skipped, no Wi-Fi"); return; }
        if (!p.isApproved(Net.fingerprints(c, n))) {
            if (!quiet) p.log(why + ": skipped, network not approved (" + Net.label(c, n) + ")");
            return; // unknown network: stay silent
        }

        long h = n.getNetworkHandle();
        long now = System.currentTimeMillis(); // wall clock: survives reboots
        long since = now - p.lastWriteAt();
        if (fromSettingChange && since >= 0 && since < QUIET_MS) {
            // Probably the echo of our own write. Look again once the quiet period is over,
            // in case Android switched it off again meanwhile.
            scheduleEnsure(c, QUIET_MS - since + 1_000);
            return;
        }

        // Within one connection the count resets after the window (Android's idle auto-off
        // over hours is normal), except once we gave up: Android keeps refusing on this
        // network (for example its own "Allow on this network?" was declined), so stay
        // quiet until the next connection or until access is switched on again.
        boolean same = p.attemptHandle() == h;
        if (same && p.attemptCount() > MAX_ATTEMPTS) return;
        long age = now - p.attemptAt();
        int count = (same && age >= 0 && age < ATTEMPT_WINDOW_MS) ? p.attemptCount() : 0;
        if (count >= MAX_ATTEMPTS) {
            if (count == MAX_ATTEMPTS) p.log(why + ": gave up for this connection (Android keeps refusing)");
            p.attempt(h, p.attemptAt(), count + 1);
            return;
        }
        p.attempt(h, count == 0 ? now : p.attemptAt(), count + 1);
        p.lastWriteAt(now);
        boolean ok = Adb.put(c, Adb.WIFI, 1);
        p.lastCheckAt(now);
        if (!quiet || !ok) p.log(why + ": wireless debugging on" + (ok ? "" : " FAILED"));
    }

    static void enforceUsb(Context c) {
        Prefs p = new Prefs(c);
        if (!p.lockdownActive() || p.allowUsb()) return;
        if (Adb.canWrite(c)) Adb.put(c, Adb.USB, 0); // no-op when already off
        scheduleUsbWatch(c);
    }

    // ---------------------------------------------------------------- scheduling

    private static ComponentName job(Context c) {
        return new ComponentName(c, WatchJob.class);
    }

    static void scheduleWifiWatch(Context c) {
        if (!new Prefs(c).armed()) return;
        JobInfo ji = new JobInfo.Builder(JOB_WIFI_WATCH, job(c))
                .addTriggerContentUri(new JobInfo.TriggerContentUri(
                        Settings.Global.getUriFor(Adb.WIFI), 0))
                .setTriggerContentUpdateDelay(SETTLE_MS)
                .setRequiredNetwork(Net.wifiRequest())
                .build();
        c.getSystemService(JobScheduler.class).schedule(ji);
    }

    static void setSafetyCheck(Context c, boolean on) {
        Prefs p = new Prefs(c);
        p.safetyCheck(on);
        p.log(on ? "15-minute safety check on" : "15-minute safety check off");
        if (on) scheduleSafety(c);
        else c.getSystemService(JobScheduler.class).cancel(JOB_SAFETY);
    }

    static void scheduleSafety(Context c) {
        Prefs p = new Prefs(c);
        if (!p.armed() || !p.safetyCheck()) {
            c.getSystemService(JobScheduler.class).cancel(JOB_SAFETY);
            return;
        }
        JobInfo ji = new JobInfo.Builder(JOB_SAFETY, job(c))
                .setPeriodic(SAFETY_PERIOD_MS, 5 * 60_000)
                .setRequiredNetwork(Net.wifiRequest())
                .setPersisted(true)
                .build();
        c.getSystemService(JobScheduler.class).schedule(ji);
    }

    static void scheduleEnsure(Context c, long delayMs) {
        JobInfo ji = new JobInfo.Builder(JOB_ENSURE, job(c))
                .setMinimumLatency(delayMs)
                .setRequiredNetwork(Net.wifiRequest())
                .build();
        c.getSystemService(JobScheduler.class).schedule(ji);
    }

    static void scheduleUsbWatch(Context c) {
        JobInfo ji = new JobInfo.Builder(JOB_USB_WATCH, job(c))
                .addTriggerContentUri(new JobInfo.TriggerContentUri(
                        Settings.Global.getUriFor(Adb.USB), 0))
                .setTriggerContentUpdateDelay(500)
                .build();
        c.getSystemService(JobScheduler.class).schedule(ji);
    }

    // ---------------------------------------------------------------- network callback

    private static PendingIntent netIntent(Context c) {
        Intent i = new Intent(c, NetReceiver.class);
        // Mutable: Android adds the Network to the intent when it fires.
        return PendingIntent.getBroadcast(c, 0, i,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_MUTABLE);
    }

    static void registerNetCallback(Context c) {
        c.getSystemService(ConnectivityManager.class)
                .registerNetworkCallback(Net.wifiRequest(), netIntent(c));
        netHooked = true;
    }

    static void unregisterNetCallback(Context c) {
        netHooked = false;
        try {
            c.getSystemService(ConnectivityManager.class).unregisterNetworkCallback(netIntent(c));
        } catch (IllegalArgumentException ignored) {
            // was not registered
        }
    }

    static void refreshTile(Context c) {
        TileService.requestListeningState(c, new ComponentName(c, PhonestickTileService.class));
    }
}

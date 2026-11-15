package io.github.mayerwin.phonestick;

import android.app.PendingIntent;
import android.app.job.JobInfo;
import android.app.job.JobScheduler;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.net.ConnectivityManager;
import android.net.Network;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.Settings;
import android.service.quicksettings.TileService;

/**
 * The whole behaviour. Nothing here runs on a timer: work happens only when
 * Android reports an event (Wi-Fi connected or changed, a debugging switch changed,
 * screen on, boot).
 *
 *  - Wi-Fi connected: a PendingIntent network callback wakes NetReceiver, which
 *    queues a short job (Android needs a moment to learn the access point).
 *  - Wireless debugging switched off within a connection (roaming to another
 *    access point): up to Android 16 a content-trigger job on the setting wakes us.
 *    Android 17 hides that setting from apps, so while access is on ListenService
 *    listens to every Wi-Fi change (and the screen coming on) and writes "on" again.
 *  - USB debugging switched on while locked down: a content-trigger job turns it off.
 *
 * The app only ever WRITES the debugging switches, it never relies on reading them:
 * since Android 17 apps are shown 0 for developer settings (development, USB and
 * wireless debugging) whatever their real value. Writing a value that is already
 * set changes nothing. A short quiet period after each write stops the app from
 * reacting to the change it just caused; a change seen during that period is
 * re-checked once when it ends. Later switch-offs are caught by the next event (a
 * Wi-Fi change, the screen coming on, Quick Settings opened).
 *
 * Jobs are deferred by Doze unless the app is exempt from battery optimisation.
 * Setup exempts it (the app does no work while idle, so this costs nothing).
 */
final class Access {
    static final int JOB_WIFI_WATCH = 1;
    static final int JOB_ENSURE = 2;
    static final int JOB_USB_WATCH = 4;
    /** The look again after a quiet period, when the listener is not running. */
    static final int JOB_RECHECK = 6;
    /** Removed jobs, cancelled on restore: 3 = old probe, 5 = 15-minute safety check. */
    private static final int JOB_PROBE_OLD = 3;
    private static final int JOB_SAFETY_OLD = 5;
    /**
     * Quiet re-writes of "on" (no change when already on): the first at most one per this gap
     * within a connection, then twice as far apart each time, up to NUDGE_MAX_MS. On Android 17
     * the app cannot tell whether Android refused a write (and showed its "Allow wireless
     * debugging on this network?" prompt), so the back-off is what keeps a refused network
     * from prompting every few seconds. A new connection, switching on, opening Quick
     * Settings, and a long enough pause since the last write (twice the gap) start again
     * from this gap: only re-writes in quick succession are spaced out.
     */
    static final long NUDGE_GAP_MS = 30_000;
    private static final long NUDGE_MAX_MS = 15 * 60_000;

    /** Ignore setting changes this soon after our own write (ours, or Android refusing it). */
    private static final long QUIET_MS = 10_000;
    /** The same with the listener running: it hears changes at once, Android's echo of
     *  our write comes within about a second. */
    private static final long QUIET_LISTENING_MS = 5_000;
    /** Safety cap: at most this many answers to a switch-off per network connection per window. */
    private static final int MAX_ATTEMPTS = 6;
    private static final long ATTEMPT_WINDOW_MS = 3 * 60_000;
    /** How long to stay quiet after giving up. */
    private static final long GIVE_UP_MS = 30 * 60_000;

    private static boolean gaveUp(Prefs p, long handle, long now) {
        long age = now - p.attemptAt();
        return p.attemptHandle() == handle && p.attemptCount() > MAX_ATTEMPTS
                && age >= 0 && age < GIVE_UP_MS;
    }
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
        p.nudge(0, 0);
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
        enforceUsb(c, true);
        // Registering makes Android report the current network at once (a second check 3 s
        // later), so only when this process has not registered yet.
        if (!netHooked) registerNetCallback(c);
        scheduleWifiWatch(c);
        ListenService.start(c);
        if (n != null) ensureWifi(c, n, false, "switch on");
        refreshTile(c);
    }

    /** Works even without the permission, so the tile can always be switched off. */
    static void disarm(Context c) {
        Prefs p = new Prefs(c);
        p.armed(false);
        ListenService.stop(c);
        unregisterNetCallback(c);
        JobScheduler js = c.getSystemService(JobScheduler.class);
        js.cancel(JOB_WIFI_WATCH);
        js.cancel(JOB_ENSURE);
        js.cancel(JOB_RECHECK);
        cancelRecheck();
        // Set before the write and cleared after it: restore() finishes a switch-off cut short
        // by a crash, and otherwise leaves Wireless debugging alone (setup, Android Studio).
        p.pendingOff(true);
        boolean ok = Adb.put(c, Adb.WIFI, 0);
        if (ok) p.pendingOff(false);
        p.log("switched off" + (ok ? "" : ": could not switch Wireless debugging off (permission missing), switch it off in Developer options"));
        refreshTile(c);
    }

    /** After boot, app update, or opening the app: put the event hooks back. */
    static void restore(Context c) {
        Prefs p = new Prefs(c);
        JobScheduler js = c.getSystemService(JobScheduler.class);
        js.cancel(JOB_PROBE_OLD);
        js.cancel(JOB_SAFETY_OLD);
        if (p.armed()) {
            rehook(c);
            scheduleEnsure(c, SETTLE_MS);
        } else if (p.pendingOff() && Adb.canWrite(c)) {
            // Off means off, also after a crash between "switched off" and the write. Only
            // then: a setup from the PC, or someone using Wireless debugging by hand while
            // access is off, must not be cut off each time the app opens or is updated.
            if (Adb.put(c, Adb.WIFI, 0)) p.pendingOff(false);
        }
        if (p.lockdownActive()) enforceUsb(c, true);
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
        ListenService.start(c);
    }

    /** Quick Settings opened: make sure the hooks are there and wireless debugging is on. */
    static void tileShown(Context c) {
        Prefs p = new Prefs(c);
        if (!p.armed() || !Adb.canWrite(c)) return;
        rehook(c);
        if (p.lockdownActive()) enforceUsb(c, true);
        nudge(c, "tile shown", true);
    }

    /** The gap the next quiet re-write must wait for after the last write (see NUDGE_GAP_MS). */
    static long nudgeGap(Context c) {
        Prefs p = new Prefs(c);
        Network n = Net.currentWifi(c);
        return n != null ? currentGap(p, n.getNetworkHandle(), System.currentTimeMillis()) : NUDGE_GAP_MS;
    }

    private static long currentGap(Prefs p, long handle, long now) {
        if (p.nudgeHandle() != handle) return NUDGE_GAP_MS;
        long gap = Math.max(NUDGE_GAP_MS, p.nudgeGap());
        long since = now - p.lastWriteAt();
        return since < 0 || since > 2 * gap ? NUDGE_GAP_MS : gap; // decays after a quiet spell
    }

    /**
     * Writes "Wireless debugging on" again on an approved network (since Android 17 apps are
     * not told when Android switches it off). Writing a value that is already set changes
     * nothing, so this is free when all is well. Quiet, spaced out (see NUDGE_GAP_MS), not
     * counted as a retry, and skipped where Android keeps refusing.
     * @param fresh the user is looking (Quick Settings opened): start the back-off again
     */
    static void nudge(Context c, String why, boolean fresh) {
        Prefs p = new Prefs(c);
        if (!p.armed() || !Adb.canWrite(c)) return;
        Network n = Net.currentWifi(c);
        if (n == null || !p.isApproved(Net.fingerprints(c, n))) return;
        long h = n.getNetworkHandle();
        long now = System.currentTimeMillis();
        long gap = fresh ? NUDGE_GAP_MS : currentGap(p, h, now);
        long since = now - p.lastWriteAt();
        if (since >= 0 && since < gap) return;
        if (gaveUp(p, h, now)) return; // Android refuses here
        if (Adb.readable() && Adb.get(c, Adb.WIFI) == 1) return; // already on
        p.lastWrite(now, true);
        p.nudge(h, Math.min(NUDGE_MAX_MS, gap * 2));
        if (!Adb.put(c, Adb.WIFI, 1)) p.log(why + ": wireless debugging on FAILED");
    }

    static void setAllowUsb(Context c, boolean allow) {
        Prefs p = new Prefs(c);
        p.allowUsb(allow);
        p.log(allow ? "USB debugging allowed" : "USB debugging locked off");
        if (allow) c.getSystemService(JobScheduler.class).cancel(JOB_USB_WATCH);
        else enforceUsb(c, true);
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

    /**
     * @param recheck the look again after a quiet period. It is final: changes right after it
     *     are not looked at again, since Android itself writes the setting again in answer
     *     to ours on some versions, which would otherwise go back and forth forever. Only
     *     writes answering a change of the setting after the quiet period count against the
     *     retry cap; writes on connect, restore or roaming are not refusals.
     */
    static void ensureWifi(Context c, Network n, boolean fromSettingChange, String why, boolean recheck) {
        Prefs p = new Prefs(c);
        if (!p.armed()) return;
        if (!Adb.canWrite(c)) { p.log(why + ": skipped, permission missing"); return; }
        if (n == null) n = Net.currentWifi(c);
        if (n == null) { p.log(why + ": skipped, no Wi-Fi"); return; }
        if (Net.fingerprint(c, n) == null) {
            // Mid-reconnect: the address is not known yet. Look again in a moment, once (the
            // listener also hears the address arrive).
            if (!recheck) recheckLater(c, 3_000);
            else p.log(why + ": skipped, no IPv4 address on this Wi-Fi yet");
            return;
        }
        if (!p.isApproved(Net.fingerprints(c, n))) {
            p.log(why + ": skipped, network not approved (" + Net.label(c, n) + ")");
            return; // unknown network: stay silent
        }

        long h = n.getNetworkHandle();
        long now = System.currentTimeMillis(); // wall clock: survives reboots
        if (Adb.readable() && Adb.get(c, Adb.WIFI) == 1) {
            // Already on (up to Android 16 apps can read it): nothing to write, so no echo.
            if (!fromSettingChange && !recheck) p.log(why + ": wireless debugging already on");
            return;
        }
        long since = now - p.lastWriteAt();
        long quiet = ListenService.running ? QUIET_LISTENING_MS : QUIET_MS;
        if (!Adb.readable() && fromSettingChange && since >= 0 && since < quiet) {
            // Probably the echo of our own write. Look again once the quiet period is over,
            // in case Android switched it off again meanwhile (once per write).
            if (!p.lastWriteFinal()) recheckLater(c, quiet - since + 1_000);
            return;
        }

        // Within one connection the count resets after the window. Once we gave up (Android
        // keeps refusing on this network, for example its own "Allow on this network?" was
        // declined), stay quiet for a while: until the next connection, until access is
        // switched on again, or GIVE_UP_MS later.
        boolean same = p.attemptHandle() == h;
        if (gaveUp(p, h, now)) return;
        if (fromSettingChange) {
            long age = now - p.attemptAt();
            int count = (same && age >= 0 && age < ATTEMPT_WINDOW_MS) ? p.attemptCount() : 0;
            if (count >= MAX_ATTEMPTS) {
                p.log(why + ": paused for 30 minutes (Android keeps switching it off)");
                p.attempt(h, now, MAX_ATTEMPTS + 1);
                return;
            }
            p.attempt(h, count == 0 ? now : p.attemptAt(), count + 1);
        }
        p.lastWrite(now, recheck);
        boolean ok = Adb.put(c, Adb.WIFI, 1);
        p.log(why + ": wireless debugging on" + (ok ? "" : " FAILED"));
    }

    /** One handler for the in-process re-check, so a newer one really replaces an older one. */
    private static final Handler MAIN = new Handler(Looper.getMainLooper());
    private static Runnable pendingRecheck;
    private static PowerManager.WakeLock recheckLock;

    /**
     * The look again after a quiet period: in the running listener when there is one (Android
     * can hold jobs back for many seconds), else as a job. In the listener, a short wake lock
     * keeps the phone from sleeping through the wait (a delay on the main thread does not run
     * while the phone sleeps); it is released as soon as the re-check ran.
     */
    private static void recheckLater(Context c, long delayMs) {
        if (ListenService.running) {
            Context app = c.getApplicationContext();
            synchronized (Access.class) {
                if (pendingRecheck != null) MAIN.removeCallbacks(pendingRecheck); // one at a time
                if (recheckLock == null) {
                    recheckLock = app.getSystemService(PowerManager.class)
                            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "phonestick:recheck");
                    recheckLock.setReferenceCounted(false);
                }
                recheckLock.acquire(delayMs + 5_000); // released below, or by this timeout at worst
                Runnable r = new Runnable() {
                    @Override
                    public void run() {
                        synchronized (Access.class) {
                            if (pendingRecheck == this) pendingRecheck = null;
                        }
                        try {
                            ensureWifi(app, null, false, "re-check", true);
                        } catch (RuntimeException e) {
                            new Prefs(app).log("re-check failed: " + e);
                        } finally {
                            synchronized (Access.class) {
                                if (pendingRecheck == null && recheckLock.isHeld()) recheckLock.release();
                            }
                        }
                    }
                };
                pendingRecheck = r;
                MAIN.postDelayed(r, delayMs);
            }
            return;
        }
        android.os.PersistableBundle x = new android.os.PersistableBundle();
        x.putBoolean("recheck", true);
        JobInfo ji = new JobInfo.Builder(JOB_RECHECK, job(c))
                .setMinimumLatency(delayMs)
                .setRequiredNetwork(Net.wifiRequest())
                .setExtras(x)
                .build();
        c.getSystemService(JobScheduler.class).schedule(ji);
    }

    /** Drops a pending in-process re-check (access switched off). */
    private static void cancelRecheck() {
        synchronized (Access.class) {
            if (pendingRecheck != null) MAIN.removeCallbacks(pendingRecheck);
            pendingRecheck = null;
            if (recheckLock != null && recheckLock.isHeld()) recheckLock.release();
        }
    }

    /**
     * @param schedule false from the USB watch job itself, which queues its next run once it
     *     has ended (scheduling a running job's id would stop it)
     */
    static void enforceUsb(Context c, boolean schedule) {
        Prefs p = new Prefs(c);
        if (!p.lockdownActive() || p.allowUsb()) return;
        if (Adb.canWrite(c)) Adb.put(c, Adb.USB, 0); // no-op when already off
        if (schedule) scheduleUsbWatch(c);
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

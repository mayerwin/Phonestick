package io.github.mayerwin.phonestick;

import android.app.job.JobParameters;
import android.app.job.JobService;
import android.os.Handler;

/** Runs the short checks queued by Access, then goes back to sleep. */
public class WatchJob extends JobService {
    @Override
    public boolean onStartJob(JobParameters params) {
        final int id = params.getJobId();
        try {
            switch (id) {
                case Access.JOB_WIFI_WATCH:
                    // The listener reacts faster and would count the same change twice.
                    if (ListenService.running) break;
                    new Prefs(this).log("woken: wireless debugging setting changed");
                    Access.ensureWifi(this, params.getNetwork(), true, "setting changed");
                    break;
                case Access.JOB_ENSURE:
                    boolean recheck = params.getExtras().getBoolean("recheck", false);
                    Access.ensureWifi(this, params.getNetwork(), false, recheck ? "re-check" : "check", recheck);
                    break;
                case Access.JOB_USB_WATCH:
                    Access.enforceUsb(this);
                    break;
            }
        } catch (RuntimeException e) {
            new Prefs(this).log("job " + id + " failed: " + e);
        } finally {
            // Content-trigger jobs fire once. Queue the next one after this job has ended,
            // since scheduling a running job id would stop it. Always, even after an error.
            new Handler(getMainLooper()).post(() -> {
                if (id == Access.JOB_WIFI_WATCH) Access.scheduleWifiWatch(this);
                if (id == Access.JOB_USB_WATCH) {
                    Prefs p = new Prefs(this);
                    if (p.lockdownActive() && !p.allowUsb()) Access.scheduleUsbWatch(this);
                }
            });
        }
        return false;
    }

    @Override
    public boolean onStopJob(JobParameters params) {
        return false;
    }
}

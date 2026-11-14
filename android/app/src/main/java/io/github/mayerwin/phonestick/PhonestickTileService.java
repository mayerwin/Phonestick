package io.github.mayerwin.phonestick;

import android.app.PendingIntent;
import android.content.Intent;
import android.graphics.drawable.Icon;
import android.os.Build;
import android.service.quicksettings.Tile;
import android.service.quicksettings.TileService;

/** The on/off button in Quick Settings. */
public class PhonestickTileService extends TileService {
    @Override
    public void onTileAdded() {
        new Prefs(this).tileAdded(true);
    }

    @Override
    public void onTileRemoved() {
        new Prefs(this).tileAdded(false);
    }

    @Override
    public void onStartListening() {
        new Prefs(this).tileAdded(true); // also true for tiles added before this callback existed
        Access.tileShown(this);
        update();
    }

    @Override
    @SuppressWarnings("deprecation")
    @android.annotation.SuppressLint("StartActivityAndCollapseDeprecated") // only used below API 34
    public void onClick() {
        // Switching off always works, even locked or without the permission.
        if (new Prefs(this).armed()) {
            Access.disarm(this);
            update();
            return;
        }
        if (!Adb.canWrite(this)) {
            // Not set up yet: open the app, which explains the one-time setup.
            Intent i = new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            if (Build.VERSION.SDK_INT >= 34) {
                startActivityAndCollapse(PendingIntent.getActivity(this, 0, i, PendingIntent.FLAG_IMMUTABLE));
            } else {
                startActivityAndCollapse(i);
            }
            return;
        }
        // Switching on approves the current network: ask for the unlock first.
        if (isLocked()) {
            unlockAndRun(() -> { Access.arm(this); update(); });
            return;
        }
        Access.arm(this);
        update();
    }

    private void update() {
        Tile t = getQsTile();
        if (t == null) return;
        boolean armed = new Prefs(this).armed();
        t.setState(armed ? Tile.STATE_ACTIVE : Tile.STATE_INACTIVE);
        t.setIcon(Icon.createWithResource(this, armed ? R.drawable.ic_tile_on : R.drawable.ic_tile_off));
        t.setSubtitle(getString(!Adb.canWrite(this) ? R.string.tile_setup
                : armed ? R.string.tile_on : R.string.tile_off));
        t.updateTile();
    }
}

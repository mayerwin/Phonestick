package io.github.mayerwin.phonestick;

import android.app.Activity;
import android.app.StatusBarManager;
import android.content.ComponentName;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.content.res.Configuration;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.Icon;
import android.graphics.drawable.RippleDrawable;
import android.net.Network;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.text.TextUtils;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.View;
import android.view.WindowInsets;
import android.view.animation.DecelerateInterpolator;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * The app screen. Calm and clear: a porcelain canvas, white cards on a soft edge, one
 * violet accent, a tinted band for the state, Lucide-style icons. Built in code with
 * framework views only (no support libraries), so the app stays tiny.
 */
public class MainActivity extends Activity {
    private LinearLayout root;
    private boolean dark;
    private Typeface brand;
    private boolean showAllLog;

    // tokens
    private int bg, card, ink, ink2, dim, tonal, tonalPressed, accent, accentText, accentTint,
            okBg, okFg, warnBg, warnFg, edge;

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        dark = (getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK)
                == Configuration.UI_MODE_NIGHT_YES;
        tokens();
        try { brand = getResources().getFont(R.font.manrope_bold); } catch (Exception e) { brand = Typeface.DEFAULT_BOLD; }
        getWindow().setDecorFitsSystemWindows(false);

        ScrollView sv = new ScrollView(this);
        sv.setBackgroundColor(bg);
        sv.setClipToPadding(false);
        root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        sv.addView(root);
        sv.setOnApplyWindowInsetsListener((v, insets) -> {
            android.graphics.Insets i = insets.getInsets(WindowInsets.Type.systemBars());
            root.setPadding(dp(20), i.top + dp(20), dp(20), i.bottom + dp(28));
            return insets;
        });
        setContentView(sv);
    }

    @Override
    protected void onResume() {
        super.onResume();
        Access.restore(this);
        maybeAskForTile();
        render();
    }

    private void tokens() {
        if (dark) {
            bg = 0xFF121316; card = 0xFF1A1B1F; ink = 0xFFEDEEF0; ink2 = 0xFFB4B8C0; dim = 0xFF8D929C;
            tonal = 0xFF26272D; tonalPressed = 0xFF31323A;
            accent = 0xFF8B5CF6; accentText = 0xFFC4B5FD; accentTint = 0x308B5CF6;
            okBg = 0xFF14301F; okFg = 0xFF6EE7A0; warnBg = 0xFF33240F; warnFg = 0xFFFBBF24; edge = 0x1CFFFFFF;
        } else {
            bg = 0xFFF4F5F7; card = 0xFFFFFFFF; ink = 0xFF16181D; ink2 = 0xFF454A54; dim = 0xFF646A75;
            tonal = 0xFFEEF0F3; tonalPressed = 0xFFE4E7EB;
            accent = 0xFF6D28D9; accentText = 0xFF6D28D9; accentTint = 0x1A6D28D9;
            okBg = 0xFFE6F4EA; okFg = 0xFF15803D; warnBg = 0xFFFDF0E1; warnFg = 0xFFB45309; edge = 0x10000000;
        }
    }

    // ------------------------------------------------------------------ screen

    private void render() {
        root.removeAllViews();
        Prefs p = new Prefs(this);
        boolean granted = Adb.canWrite(this);

        root.addView(header());
        root.addView(hero(p, granted));

        if (!granted) {
            root.addView(label(getString(R.string.setup_title)));
            LinearLayout c = card();
            TextView t = text(getString(R.string.setup_body, getPackageName()), 14, ink2, false);
            t.setLineSpacing(dp(3), 1f);
            t.setTextIsSelectable(true);
            c.addView(t);
            root.addView(c);
            fadeIn();
            return;
        }

        boolean tileOk = p.tileAdded();
        boolean dozeOk = Access.dozeExempt(this);
        if (!tileOk || !dozeOk) {
            root.addView(label(getString(R.string.checklist_title)));
            LinearLayout c = card();
            if (!tileOk) c.addView(row(R.drawable.ic_l_plus, getString(R.string.check_tile), getString(R.string.check_tile_help),
                    button(getString(R.string.add), true, v -> {
                        if (Build.VERSION.SDK_INT < 33) {
                            // No "add tile" prompt before Android 13: say how instead.
                            android.widget.Toast.makeText(this, R.string.add_tile_manual, android.widget.Toast.LENGTH_LONG).show();
                            return;
                        }
                        new Prefs(this).askedTile(false);
                        maybeAskForTile();
                    })));
            if (!tileOk && !dozeOk) c.addView(divider());
            if (!dozeOk) c.addView(row(R.drawable.ic_l_battery, getString(R.string.check_doze), getString(R.string.check_doze_help),
                    button(getString(R.string.allow), true, v -> askDozeExemption())));
            root.addView(c);
        }

        root.addView(label(getString(R.string.approved_title)));
        LinearLayout nets = card();
        List<String> list = new ArrayList<>(p.approved());
        Collections.sort(list);
        if (list.isEmpty()) nets.addView(text(getString(R.string.approved_none), 14, ink2, false));
        for (int i = 0; i < list.size(); i++) {
            String e = list.get(i);
            if (i > 0) nets.addView(divider());
            nets.addView(row(R.drawable.ic_l_wifi, e.substring(e.indexOf('|') + 1), null,
                    button(getString(R.string.forget), false, v -> { new Prefs(this).forget(e); render(); })));
        }
        root.addView(nets);

        root.addView(label(getString(R.string.security_title)));
        LinearLayout sec = card();
        Switch usb = new Switch(this);
        usb.setChecked(!p.allowUsb());
        int[][] st = { { android.R.attr.state_checked }, {} };
        usb.setThumbTintList(new ColorStateList(st, new int[] { Color.WHITE, dark ? 0xFF8D929C : Color.WHITE }));
        usb.setTrackTintList(new ColorStateList(st, new int[] { accent, dark ? 0xFF3A3B42 : 0xFFD5D8DE }));
        usb.setOnCheckedChangeListener((v, locked) -> {
            Access.setAllowUsb(this, !locked);
            v.postDelayed(this::render, 200);
        });
        sec.addView(row(R.drawable.ic_l_shield, getString(R.string.usb_lock), getString(R.string.usb_lock_help), usb));
        sec.addView(divider());
        Switch safety = new Switch(this);
        safety.setChecked(p.safetyCheck());
        safety.setThumbTintList(usb.getThumbTintList());
        safety.setTrackTintList(usb.getTrackTintList());
        safety.setOnCheckedChangeListener((v, on) -> {
            Access.setSafetyCheck(this, on);
            v.postDelayed(this::render, 200);
        });
        sec.addView(row(R.drawable.ic_l_activity, getString(R.string.safety), getString(R.string.safety_help), safety));
        root.addView(sec);

        root.addView(label(getString(R.string.events_title)));
        LinearLayout ev = card();
        String log = p.log();
        if (log.isEmpty()) {
            ev.addView(text(getString(R.string.events_none), 14, dim, false));
        } else {
            String[] lines = log.split("\n");
            int from = showAllLog ? 0 : Math.max(0, lines.length - 5);
            for (int i = lines.length - 1; i >= from; i--) {
                String l = lines[i];
                int cut = l.indexOf("  ");
                LinearLayout r = new LinearLayout(this);
                r.setPadding(0, dp(5), 0, dp(5));
                TextView when = text(cut > 0 ? l.substring(0, cut) : "", 12, dim, false);
                when.setTypeface(Typeface.MONOSPACE);
                r.addView(when, new LinearLayout.LayoutParams(dp(112), -2));
                TextView what = text(cut > 0 ? l.substring(cut + 2) : l, 13, ink2, false);
                r.addView(what, new LinearLayout.LayoutParams(0, -2, 1));
                ev.addView(r);
            }
            if (lines.length > 5) {
                TextView more = text(getString(showAllLog ? R.string.show_less : R.string.show_all), 14, accentText, true);
                more.setPadding(0, dp(10), 0, dp(2));
                more.setOnClickListener(v -> { showAllLog = !showAllLog; render(); });
                ev.addView(more);
            }
        }
        root.addView(ev);

        root.addView(label(getString(R.string.how_title)));
        LinearLayout how = card();
        TextView h = text(getString(R.string.how_body), 13.5f, ink2, false);
        h.setLineSpacing(dp(3), 1f);
        how.addView(h);
        root.addView(how);
        fadeIn();
    }

    /** Calm entrance: a short fade and a 4dp rise. */
    private void fadeIn() {
        root.setAlpha(0.4f);
        root.setTranslationY(dp(4));
        root.animate().alpha(1f).translationY(0).setDuration(180).setInterpolator(new DecelerateInterpolator()).start();
    }

    private View header() {
        LinearLayout h = new LinearLayout(this);
        h.setOrientation(LinearLayout.VERTICAL);
        h.setPadding(dp(2), 0, 0, dp(18));
        LinearLayout top = new LinearLayout(this);
        top.setGravity(Gravity.CENTER_VERTICAL);
        ImageView logo = new ImageView(this);
        logo.setImageResource(R.mipmap.ic_launcher);
        top.addView(logo, new LinearLayout.LayoutParams(dp(36), dp(36)));
        TextView name = text(getString(R.string.app_name), 24, ink, false);
        name.setTypeface(brand);
        name.setPadding(dp(10), 0, 0, 0);
        top.addView(name);
        h.addView(top);
        TextView sub = text(getString(R.string.tagline_short), 13.5f, dim, false);
        sub.setPadding(dp(2), dp(6), 0, 0);
        h.addView(sub);
        return h;
    }

    /** The main card: state, a tinted band saying what it means, and the one button. */
    private View hero(Prefs p, boolean granted) {
        boolean on = p.armed();
        LinearLayout c = card();
        c.setPadding(dp(18), dp(18), dp(18), dp(18));

        Network n = Net.currentWifi(this);
        boolean approved = n != null && p.isApproved(Net.fingerprints(this, n));

        View top = row(on ? R.drawable.ic_l_smartphone : R.drawable.ic_l_lock, null, null, null);
        LinearLayout texts = (LinearLayout) ((LinearLayout) top).getChildAt(1);
        TextView title = text(getString(on ? R.string.hero_on : R.string.hero_off), 26, ink, false);
        title.setTypeface(brand);
        texts.addView(title);
        String sub = !granted ? getString(R.string.hero_needs_setup)
                : on ? getString(R.string.hero_on_short) : getString(R.string.hero_off_sub);
        texts.addView(text(sub, 14, dim, false));
        c.addView(top);

        // Band
        int bandBg, bandFg, icon;
        String head, detail;
        if (!granted) { bandBg = warnBg; bandFg = warnFg; icon = R.drawable.ic_l_lock; head = getString(R.string.band_setup); detail = ""; }
        else if (!on) { bandBg = tonal; bandFg = ink2; icon = R.drawable.ic_l_lock; head = getString(R.string.band_locked); detail = getString(R.string.band_locked_detail); }
        else if (n == null) { bandBg = tonal; bandFg = ink2; icon = R.drawable.ic_l_wifi; head = getString(R.string.band_no_wifi); detail = ""; }
        else if (approved) { bandBg = okBg; bandFg = okFg; icon = R.drawable.ic_l_check; head = getString(R.string.band_ready); detail = Net.label(this, n); }
        else { bandBg = warnBg; bandFg = warnFg; icon = R.drawable.ic_l_wifi; head = getString(R.string.band_unknown); detail = getString(R.string.band_unknown_detail); }
        c.addView(band(icon, head, detail, bandBg, bandFg));

        if (granted) {
            View gap = new View(this);
            c.addView(gap, new LinearLayout.LayoutParams(1, dp(14)));
            TextView btn = button(getString(on ? R.string.btn_disarm : R.string.btn_arm), !on, v -> {
                v.performHapticFeedback(HapticFeedbackConstants.CONFIRM);
                if (new Prefs(this).armed()) Access.disarm(this);
                else Access.arm(this);
                render();
            });
            btn.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
            btn.setGravity(Gravity.CENTER);
            btn.setCompoundDrawablesRelativeWithIntrinsicBounds(tinted(R.drawable.ic_l_power, on ? ink : Color.WHITE, 20), null, null, null);
            btn.setCompoundDrawablePadding(dp(10));
            FrameLayout holder = new FrameLayout(this);
            FrameLayout.LayoutParams bl = new FrameLayout.LayoutParams(-2, dp(52));
            bl.gravity = Gravity.CENTER;
            btn.setPadding(dp(22), 0, dp(22), 0);
            holder.addView(btn, bl);
            GradientDrawable hb = new GradientDrawable();
            hb.setCornerRadius(dp(12));
            hb.setColor(on ? tonal : accent);
            holder.setBackground(new RippleDrawable(ColorStateList.valueOf(on ? tonalPressed : 0x33FFFFFF), hb, null));
            btn.setBackground(null);
            holder.setOnClickListener(v -> btn.performClick());
            c.addView(holder, new LinearLayout.LayoutParams(-1, dp(52)));
        }
        return c;
    }

    private View band(int icon, String head, String detail, int bgc, int fg) {
        LinearLayout b = new LinearLayout(this);
        b.setGravity(Gravity.CENTER_VERTICAL);
        b.setPadding(dp(14), dp(12), dp(14), dp(12));
        GradientDrawable g = new GradientDrawable();
        g.setCornerRadius(dp(12));
        g.setColor(bgc);
        b.setBackground(g);
        ImageView iv = new ImageView(this);
        iv.setImageResource(icon);
        iv.setImageTintList(ColorStateList.valueOf(fg));
        b.addView(iv, new LinearLayout.LayoutParams(dp(18), dp(18)));
        LinearLayout t = new LinearLayout(this);
        t.setOrientation(LinearLayout.VERTICAL);
        t.setPadding(dp(12), 0, 0, 0);
        t.addView(text(head, 14, fg, true));
        if (!detail.isEmpty()) {
            TextView d = text(detail, 12.5f, fg, false);
            d.setAlpha(0.85f);
            d.setSingleLine(true);
            d.setEllipsize(TextUtils.TruncateAt.END);
            t.addView(d);
        }
        b.addView(t, new LinearLayout.LayoutParams(0, -2, 1));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2);
        lp.topMargin = dp(16);
        b.setLayoutParams(lp);
        return b;
    }

    // ------------------------------------------------------------------ tile and Doze

    private void maybeAskForTile() {
        Prefs p = new Prefs(this);
        if (Build.VERSION.SDK_INT < 33 || p.tileAdded() || p.askedTile() || !Adb.canWrite(this)) return;
        p.askedTile(true);
        StatusBarManager sbm = getSystemService(StatusBarManager.class);
        sbm.requestAddTileService(new ComponentName(this, PhonestickTileService.class),
                getString(R.string.app_name), Icon.createWithResource(this, R.drawable.ic_tile_on),
                getMainExecutor(), result -> {
                    if (result == StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ADDED
                            || result == StatusBarManager.TILE_ADD_REQUEST_RESULT_TILE_ALREADY_ADDED) {
                        new Prefs(this).tileAdded(true);
                    }
                    render();
                });
    }

    @android.annotation.SuppressLint("BatteryLife") // sideloaded tool; the exemption is the point
    private void askDozeExemption() {
        try {
            startActivity(new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                    Uri.parse("package:" + getPackageName())));
        } catch (Exception e) {
            try {
                startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS));
            } catch (Exception e2) {
                startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                        Uri.parse("package:" + getPackageName())));
            }
        }
    }

    // ------------------------------------------------------------------ building blocks

    private LinearLayout card() {
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.VERTICAL);
        c.setPadding(dp(16), dp(12), dp(16), dp(12));
        GradientDrawable g = new GradientDrawable();
        g.setCornerRadius(dp(16));
        g.setColor(card);
        g.setStroke(Math.max(1, dp(1) / 2), edge);
        c.setBackground(g);
        if (!dark) c.setElevation(dp(1));
        return c;
    }

    /** Section label: small, semibold, lightly tracked. */
    private TextView label(String s) {
        TextView t = text(s.toUpperCase(), 11.5f, dim, true);
        t.setLetterSpacing(0.07f);
        t.setPadding(dp(4), dp(26), 0, dp(9));
        return t;
    }

    /** Icon tile, title and help on the left, an optional control on the right. */
    private View row(int icon, String titleText, String help, View control) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(6), 0, dp(6));
        FrameLayout tile = new FrameLayout(this);
        GradientDrawable g = new GradientDrawable();
        g.setCornerRadius(dp(10));
        g.setColor(accentTint);
        tile.setBackground(g);
        ImageView iv = new ImageView(this);
        iv.setImageResource(icon);
        iv.setImageTintList(ColorStateList.valueOf(accentText));
        FrameLayout.LayoutParams il = new FrameLayout.LayoutParams(dp(20), dp(20));
        il.gravity = Gravity.CENTER;
        tile.addView(iv, il);
        row.addView(tile, new LinearLayout.LayoutParams(dp(40), dp(40)));
        LinearLayout texts = new LinearLayout(this);
        texts.setOrientation(LinearLayout.VERTICAL);
        texts.setPadding(dp(14), 0, dp(8), 0);
        if (titleText != null) texts.addView(text(titleText, 15.5f, ink, true));
        if (help != null) {
            TextView h = text(help, 13, dim, false);
            h.setLineSpacing(dp(2), 1f);
            h.setPadding(0, dp(2), 0, 0);
            texts.addView(h);
        }
        row.addView(texts, new LinearLayout.LayoutParams(0, -2, 1));
        if (control != null) row.addView(control);
        return row;
    }

    /** Primary buttons are filled with the accent; secondary ones are a neutral tone, never outlined. */
    private TextView button(String s, boolean primary, View.OnClickListener l) {
        TextView b = text(s, 14, primary ? Color.WHITE : ink, true);
        b.setPadding(dp(16), dp(9), dp(16), dp(9));
        GradientDrawable g = new GradientDrawable();
        g.setCornerRadius(dp(10));
        g.setColor(primary ? accent : tonal);
        b.setBackground(new RippleDrawable(ColorStateList.valueOf(primary ? 0x33FFFFFF : tonalPressed), g, null));
        b.setOnClickListener(l);
        return b;
    }

    private View divider() {
        View d = new View(this);
        d.setBackgroundColor(edge);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, Math.max(1, dp(1)));
        lp.leftMargin = dp(54);
        lp.topMargin = dp(4);
        lp.bottomMargin = dp(4);
        d.setLayoutParams(lp);
        return d;
    }

    private android.graphics.drawable.Drawable tinted(int res, int color, int sizeDp) {
        android.graphics.drawable.Drawable d = getDrawable(res).mutate();
        d.setTint(color);
        d.setBounds(0, 0, dp(sizeDp), dp(sizeDp));
        return d;
    }

    private TextView text(String s, float sp, int color, boolean medium) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        t.setTypeface(Typeface.create(medium ? "sans-serif-medium" : "sans-serif", Typeface.NORMAL));
        return t;
    }

    private int dp(float v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }
}

package org.screencast.app;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.view.Gravity;
import android.view.View;
import android.widget.TextView;

/** Shared colours, shapes and small widgets for the dark interface. */
final class UiKit {

    static final int BG = 0xFF0E1014;
    static final int SURFACE = 0xFF161A20;
    static final int SURFACE_ALT = 0xFF1F242D;
    static final int SURFACE_HIGH = 0xFF262C36;
    static final int DIVIDER = 0xFF262B34;
    static final int TEXT = 0xFFF2F4F8;
    static final int TEXT_DIM = 0xFF8D94A3;
    static final int TEXT_FAINT = 0xFF5F6673;
    static final int ACCENT = 0xFF00D26A;
    static final int ACCENT_SOFT = 0xFF123626;
    static final int DANGER = 0xFFFF5C5C;
    static final int WARNING = 0xFFFFB020;

    private UiKit() {
    }

    static int dp(Context context, float value) {
        return Math.round(value * context.getResources().getDisplayMetrics().density);
    }

    static GradientDrawable rounded(Context context, int color, float radiusDp) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(dp(context, radiusDp));
        return drawable;
    }

    static GradientDrawable stroked(Context context, int color, int strokeColor, float radiusDp,
            float strokeDp) {
        GradientDrawable drawable = rounded(context, color, radiusDp);
        drawable.setStroke(dp(context, strokeDp), strokeColor);
        return drawable;
    }

    static RippleDrawable ripple(Context context, GradientDrawable background) {
        return new RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), background, null);
    }

    static TextView text(Context context, CharSequence content, float sizeSp, int color,
            boolean medium) {
        TextView view = new TextView(context);
        view.setText(content);
        view.setTextSize(sizeSp);
        view.setTextColor(color);
        view.setTypeface(medium
                ? Typeface.create("sans-serif-medium", Typeface.NORMAL)
                : Typeface.create("sans-serif", Typeface.NORMAL));
        view.setIncludeFontPadding(false);
        return view;
    }

    /** A flat filled button; the primary variant is the green call to action. */
    static TextView button(Context context, CharSequence label, boolean primary) {
        TextView view = text(context, label, 15, primary ? 0xFF062015 : TEXT, true);
        view.setGravity(Gravity.CENTER);
        view.setPadding(dp(context, 18), dp(context, 14), dp(context, 18), dp(context, 14));
        GradientDrawable background = rounded(context, primary ? ACCENT : SURFACE_HIGH, 14);
        view.setBackground(ripple(context, background));
        view.setClickable(true);
        view.setFocusable(true);
        return view;
    }

    /** A small pill, used for status badges and latency. */
    static TextView pill(Context context, CharSequence label, int textColor, int fillColor) {
        TextView view = text(context, label, 11, textColor, true);
        view.setGravity(Gravity.CENTER);
        view.setPadding(dp(context, 9), dp(context, 5), dp(context, 9), dp(context, 5));
        view.setBackground(rounded(context, fillColor, 999));
        return view;
    }

    static void setMargins(View view, int left, int top, int right, int bottom) {
        if (view.getLayoutParams() instanceof android.view.ViewGroup.MarginLayoutParams) {
            android.view.ViewGroup.MarginLayoutParams params =
                    (android.view.ViewGroup.MarginLayoutParams) view.getLayoutParams();
            params.setMargins(left, top, right, bottom);
            view.setLayoutParams(params);
        }
    }
}

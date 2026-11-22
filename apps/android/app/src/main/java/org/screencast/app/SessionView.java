package org.screencast.app;

import android.annotation.SuppressLint;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.View.OnClickListener;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

/**
 * The live session screen: video on a Surface, a floating top bar, a floating tool row and a
 * dismissible statistics card. Gestures stay on the video, so the overlays never swallow a touch
 * meant for the remote desktop.
 */
@SuppressLint("ViewConstructor")
final class SessionView extends FrameLayout {

    interface Listener {
        void onDisconnect();

        void onKeyboard();

        void onMouseMove(int normalizedX, int normalizedY);

        void onMouseButton(int button, boolean down);

        void onWheel(int deltaX, int deltaY);
    }

    private final Listener listener;
    private final Handler handler = new Handler(Looper.getMainLooper());

    private final VideoSurfaceView videoView;
    private final LinearLayout topBar;
    private final LinearLayout toolbar;
    private final LinearLayout statsCard;
    private TextView nameView;
    private TextView latencyView;
    private final TextView statusView;
    private final TextView noticeView;
    private final TextView centerHint;
    private final TextView statsText;

    private boolean chromeVisible = true;
    private boolean statsVisible;
    private int videoWidth;
    private int videoHeight;

    private final Runnable hideNotice;

    SessionView(Context context, Listener listener) {
        super(context);
        this.listener = listener;
        setBackgroundColor(0xFF000000);

        TouchRouter router = new TouchRouter(context, new TouchRouter.Sender() {
            @Override
            public void mouseMove(int normalizedX, int normalizedY) {
                SessionView.this.listener.onMouseMove(normalizedX, normalizedY);
            }

            @Override
            public void mouseButton(int button, boolean down) {
                SessionView.this.listener.onMouseButton(button, down);
            }

            @Override
            public void wheel(int deltaX, int deltaY) {
                SessionView.this.listener.onWheel(deltaX, deltaY);
            }
        }, this::toggleChrome);

        videoView = new VideoSurfaceView(context, router);
        addView(videoView, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        topBar = buildTopBar(context);
        toolbar = buildToolbar(context);
        statsText = UiKit.text(context, "", 11, UiKit.TEXT_DIM, false);
        statsCard = buildStatsCard(context);
        statusView = UiKit.text(context, "", 11, UiKit.TEXT_DIM, false);
        statusView.setGravity(Gravity.CENTER);
        centerHint = UiKit.text(context, context.getString(R.string.stats_waiting), 13,
                UiKit.TEXT_FAINT, false);
        centerHint.setGravity(Gravity.CENTER);
        noticeView = UiKit.text(context, "", 12, UiKit.TEXT, false);
        noticeView.setGravity(Gravity.CENTER);
        noticeView.setBackground(UiKit.rounded(context, 0xE61F242D, 12));
        noticeView.setPadding(UiKit.dp(context, 14), UiKit.dp(context, 9), UiKit.dp(context, 14),
                UiKit.dp(context, 9));
        noticeView.setVisibility(View.GONE);

        addView(topBar, topBarParams(context));
        addView(centerHint, centeredParams());
        addView(statusView, statusParams(context));
        addView(toolbar, toolbarParams(context));
        addView(statsCard, statsParams(context));
        addView(noticeView, noticeParams(context));
        hideNotice = () -> noticeView.setVisibility(View.GONE);
    }

    VideoSurfaceView videoView() {
        return videoView;
    }

    /* ------------------------------------------------------------------ */
    /* Chrome                                                              */
    /* ------------------------------------------------------------------ */

    private LinearLayout buildTopBar(Context context) {
        LinearLayout bar = new LinearLayout(context);
        bar.setOrientation(LinearLayout.HORIZONTAL);
        bar.setGravity(Gravity.CENTER_VERTICAL);
        bar.setPadding(UiKit.dp(context, 6), UiKit.dp(context, 6), UiKit.dp(context, 12),
                UiKit.dp(context, 6));
        bar.setBackground(UiKit.rounded(context, 0xE611141A, 26));

        ImageView back = new ImageView(context);
        back.setImageResource(R.drawable.ic_back);
        back.setColorFilter(UiKit.TEXT);
        back.setPadding(UiKit.dp(context, 8), UiKit.dp(context, 8), UiKit.dp(context, 8),
                UiKit.dp(context, 8));
        back.setContentDescription(context.getString(R.string.session_back));
        back.setOnClickListener(view -> listener.onDisconnect());
        bar.addView(back, new LinearLayout.LayoutParams(UiKit.dp(context, 36),
                UiKit.dp(context, 36)));

        nameView = UiKit.text(context, "", 13, UiKit.TEXT, true);
        nameView.setSingleLine(true);
        LinearLayout.LayoutParams nameParams = new LinearLayout.LayoutParams(0,
                ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
        nameParams.leftMargin = UiKit.dp(context, 4);
        bar.addView(nameView, nameParams);

        latencyView = UiKit.pill(context, context.getString(R.string.session_latency_none),
                UiKit.ACCENT, UiKit.ACCENT_SOFT);
        bar.addView(latencyView);
        return bar;
    }

    private LinearLayout buildToolbar(Context context) {
        LinearLayout bar = new LinearLayout(context);
        bar.setOrientation(LinearLayout.HORIZONTAL);
        bar.setPadding(UiKit.dp(context, 4), UiKit.dp(context, 6), UiKit.dp(context, 4),
                UiKit.dp(context, 8));
        bar.setBackground(UiKit.rounded(context, 0xE611141A, 24));

        bar.addView(toolItem(context, R.drawable.ic_keyboard, R.string.session_keyboard,
                UiKit.TEXT, view -> listener.onKeyboard()), itemParams(context));
        bar.addView(toolItem(context, R.drawable.ic_stats, R.string.session_stats, UiKit.TEXT,
                view -> {
                    statsVisible = !statsVisible;
                    updateChrome();
                }), itemParams(context));
        bar.addView(toolItem(context, R.drawable.ic_monitor, R.string.session_chrome, UiKit.TEXT,
                view -> setChromeVisible(!chromeVisible)), itemParams(context));
        bar.addView(toolItem(context, R.drawable.ic_power, R.string.disconnect, UiKit.DANGER,
                view -> listener.onDisconnect()), itemParams(context));
        return bar;
    }

    private LinearLayout toolItem(Context context, int iconRes, int labelRes, int tint,
            OnClickListener action) {
        LinearLayout item = new LinearLayout(context);
        item.setOrientation(LinearLayout.VERTICAL);
        item.setGravity(Gravity.CENTER);
        item.setClickable(true);
        item.setFocusable(true);

        ImageView icon = new ImageView(context);
        icon.setImageResource(iconRes);
        icon.setColorFilter(tint);
        icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        item.addView(icon, new LinearLayout.LayoutParams(UiKit.dp(context, 20),
                UiKit.dp(context, 20)));

        TextView label = UiKit.text(context, context.getString(labelRes), 10,
                tint == UiKit.DANGER ? UiKit.DANGER : UiKit.TEXT_DIM, false);
        LinearLayout.LayoutParams labelParams = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        labelParams.topMargin = UiKit.dp(context, 4);
        item.addView(label, labelParams);
        item.setOnClickListener(action);
        return item;
    }

    private LinearLayout buildStatsCard(Context context) {
        LinearLayout card = new LinearLayout(context);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(UiKit.dp(context, 12), UiKit.dp(context, 10), UiKit.dp(context, 12),
                UiKit.dp(context, 10));
        card.setBackground(UiKit.rounded(context, 0xE611141A, 14));
        card.addView(statsText);
        card.setVisibility(View.GONE);
        return card;
    }

    /* ------------------------------------------------------------------ */
    /* Layout parameters                                                   */
    /* ------------------------------------------------------------------ */

    private FrameLayout.LayoutParams topBarParams(Context context) {
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, UiKit.dp(context, 50));
        params.gravity = Gravity.TOP;
        params.setMargins(UiKit.dp(context, 12), UiKit.dp(context, 12), UiKit.dp(context, 12), 0);
        return params;
    }

    private FrameLayout.LayoutParams toolbarParams(Context context) {
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, UiKit.dp(context, 66));
        params.gravity = Gravity.BOTTOM;
        params.setMargins(UiKit.dp(context, 12), 0, UiKit.dp(context, 12),
                UiKit.dp(context, 18));
        return params;
    }

    private FrameLayout.LayoutParams statsParams(Context context) {
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        params.gravity = Gravity.TOP | Gravity.END;
        params.setMargins(0, UiKit.dp(context, 74), UiKit.dp(context, 12), 0);
        return params;
    }

    private FrameLayout.LayoutParams noticeParams(Context context) {
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        params.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        params.bottomMargin = UiKit.dp(context, 100);
        return params;
    }

    private FrameLayout.LayoutParams statusParams(Context context) {
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        params.gravity = Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        params.bottomMargin = UiKit.dp(context, 92);
        return params;
    }

    private FrameLayout.LayoutParams centeredParams() {
        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        params.gravity = Gravity.CENTER;
        return params;
    }

    private LinearLayout.LayoutParams itemParams(Context context) {
        return new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
    }

    /* ------------------------------------------------------------------ */
    /* State                                                               */
    /* ------------------------------------------------------------------ */

    void setDeviceName(String name) {
        nameView.setText(name);
    }

    void setVideoSize(int width, int height) {
        videoWidth = width;
        videoHeight = height;
        layoutVideo();
    }

    void setStatus(String status) {
        statusView.setText(status);
    }

    void setStats(SessionStats stats) {
        Context context = getContext();
        if (stats.width <= 0 || stats.height <= 0) {
            statsText.setText(R.string.stats_waiting);
            latencyView.setText(R.string.session_latency_none);
            return;
        }

        centerHint.setVisibility(View.GONE);
        videoWidth = stats.width;
        videoHeight = stats.height;
        layoutVideo();

        StringBuilder text = new StringBuilder();
        text.append(context.getString(R.string.stats_line_resolution, stats.width, stats.height,
                stats.fps)).append('\n');
        String rtt = stats.hasRtt()
                ? context.getString(R.string.rtt_format, stats.videoRttMs)
                : context.getString(R.string.rtt_unknown);
        text.append(context.getString(R.string.stats_line_rtt, rtt)).append('\n');
        text.append(context.getString(R.string.stats_line_drops, stats.droppedFrames,
                stats.videoDatagramsRejected)).append('\n');
        text.append(pathText(stats.videoPathStatus));
        statsText.setText(text);
        statsCard.setVisibility(chromeVisible && statsVisible ? View.VISIBLE : View.GONE);

        latencyView.setText(stats.hasRtt()
                ? context.getString(R.string.session_latency, stats.videoRttMs)
                : context.getString(R.string.session_latency_none));
        latencyView.setTextColor(stats.videoPathStatus == NativeCore.SC_PATH_DOWN
                ? UiKit.WARNING
                : UiKit.ACCENT);
    }

    void reset() {
        statsVisible = false;
        statsCard.setVisibility(View.GONE);
        centerHint.setVisibility(View.VISIBLE);
        statsText.setText(R.string.stats_waiting);
        latencyView.setText(R.string.session_latency_none);
        latencyView.setTextColor(UiKit.ACCENT);
        statusView.setText("");
    }

    void showNotice(String text) {
        handler.removeCallbacks(hideNotice);
        noticeView.setText(text);
        noticeView.setVisibility(View.VISIBLE);
        handler.postDelayed(hideNotice, 4000);
    }

    void setChromeVisible(boolean visible) {
        chromeVisible = visible;
        updateChrome();
    }

    void toggleChrome() {
        setChromeVisible(!chromeVisible);
    }

    private void updateChrome() {
        topBar.setVisibility(chromeVisible ? View.VISIBLE : View.GONE);
        toolbar.setVisibility(chromeVisible ? View.VISIBLE : View.GONE);
        statusView.setVisibility(chromeVisible ? View.VISIBLE : View.GONE);
        statsCard.setVisibility(chromeVisible && statsVisible ? View.VISIBLE : View.GONE);
    }

    private String pathText(int status) {
        switch (status) {
            case NativeCore.SC_PATH_ALIVE:
                return getContext().getString(R.string.path_alive);
            case NativeCore.SC_PATH_DOWN:
                return getContext().getString(R.string.path_down);
            default:
                return getContext().getString(R.string.path_probing);
        }
    }

    /* ------------------------------------------------------------------ */
    /* Video geometry                                                      */
    /* ------------------------------------------------------------------ */

    @Override
    protected void onSizeChanged(int width, int height, int oldWidth, int oldHeight) {
        super.onSizeChanged(width, height, oldWidth, oldHeight);
        layoutVideo();
    }

    private void layoutVideo() {
        if (getWidth() == 0 || getHeight() == 0) {
            return;
        }

        int sourceWidth = videoWidth > 0 ? videoWidth : 16;
        int sourceHeight = videoHeight > 0 ? videoHeight : 9;
        double scale = Math.min((double) getWidth() / sourceWidth,
                (double) getHeight() / sourceHeight);
        int viewWidth = Math.max(1, (int) Math.round(sourceWidth * scale));
        int viewHeight = Math.max(1, (int) Math.round(sourceHeight * scale));

        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(viewWidth, viewHeight);
        params.gravity = Gravity.CENTER;
        videoView.setLayoutParams(params);
    }
}

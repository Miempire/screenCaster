package org.screencast.app;

import android.app.Activity;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.SystemClock;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.FrameLayout;

import java.util.Locale;

/**
 * The Android control end. The launcher ({@link HomeView}) owns the device book and the
 * nine-digit code; once a device is chosen the activity swaps to {@link SessionView}, which keeps
 * the decoded picture on a Surface and exposes the floating session controls.
 */
public final class MainActivity extends Activity {

    private static final long KEY_NOTICE_MIN_INTERVAL_MS = 1500;

    private FrameLayout root;
    private HomeView homeView;
    private SessionView sessionView;
    private EditText imeAnchor;

    private ViewerSession session;
    private DeviceBook.Device currentDevice;
    private int sessionGeneration;
    private long lastKeyNoticeMs;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // The video surface keeps its size while the soft keyboard is open; resizing it would
        // destroy the Surface and force the decoder to rebuild.
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        buildViews();
        setContentView(root);
        enterImmersive();
    }

    /* ------------------------------------------------------------------ */
    /* UI construction                                                     */
    /* ------------------------------------------------------------------ */

    private void buildViews() {
        root = new FrameLayout(this);
        root.setBackgroundColor(UiKit.BG);

        homeView = new HomeView(this, new HomeView.Listener() {
            @Override
            public void onConnect(DeviceBook.Device device) {
                connect(device);
            }

            @Override
            public void onDelete(DeviceBook.Device device) {
                homeView.removeDevice(device);
            }
        }, getString(R.string.about_version, versionName(), coreVersion()));

        sessionView = new SessionView(this, new SessionView.Listener() {
            @Override
            public void onDisconnect() {
                disconnect(getString(R.string.status_stopped));
            }

            @Override
            public void onKeyboard() {
                showKeyboard();
            }

            @Override
            public void onMouseMove(int normalizedX, int normalizedY) {
                ViewerSession active = session;
                if (active != null) {
                    active.sendMouseMove(normalizedX, normalizedY);
                }
            }

            @Override
            public void onMouseButton(int button, boolean down) {
                ViewerSession active = session;
                if (active != null) {
                    active.sendMouseButton(button, down);
                }
            }

            @Override
            public void onWheel(int deltaX, int deltaY) {
                ViewerSession active = session;
                if (active != null) {
                    active.sendWheel(deltaX, deltaY);
                }
            }
        });
        sessionView.setVisibility(View.GONE);

        homeView.setVisibility(View.VISIBLE);

        root.addView(homeView, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        root.addView(sessionView, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        buildImeAnchor();

        sessionView.videoView().getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                attachSurface(holder.getSurface());
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
                attachSurface(holder.getSurface());
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                attachSurface(null);
            }
        });

        if (!NativeCore.isLoaded()) {
            homeView.showNotice(getString(R.string.native_error));
        }
    }

    private void buildImeAnchor() {
        imeAnchor = new EditText(this);
        imeAnchor.setInputType(InputType.TYPE_CLASS_TEXT);
        imeAnchor.setSingleLine(true);
        imeAnchor.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        imeAnchor.setBackground(null);
        imeAnchor.setAlpha(0f);
        imeAnchor.addTextChangedListener(new TextWatcher() {
            @Override
            public void beforeTextChanged(CharSequence text, int start, int count, int after) {
                // Only the committed text matters.
            }

            @Override
            public void onTextChanged(CharSequence text, int start, int before, int count) {
                // Only the committed text matters.
            }

            @Override
            public void afterTextChanged(Editable editable) {
                if (editable.length() == 0) {
                    return;
                }

                // Committed text has no scan code, and inventing one would inject the wrong key.
                String committed = editable.toString();
                editable.clear();
                if (session != null) {
                    sessionView.showNotice(getString(R.string.text_unsupported, committed));
                }
            }
        });

        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(UiKit.dp(this, 1),
                UiKit.dp(this, 1));
        params.gravity = Gravity.TOP | Gravity.START;
        root.addView(imeAnchor, params);
    }

    /* ------------------------------------------------------------------ */
    /* Connection                                                          */
    /* ------------------------------------------------------------------ */

    private void connect(DeviceBook.Device device) {
        if (session != null) {
            disconnect(null);
        }

        if (!NativeCore.isLoaded()) {
            homeView.showNotice(getString(R.string.native_error));
            return;
        }

        final DeviceBook.Device stored = homeView.remember(device);
        currentDevice = stored;

        final int generation = ++sessionGeneration;
        session = new ViewerSession(stored.host, stored.videoPort, stored.controlPort,
                new ViewerSession.Listener() {
                    @Override
                    public void onSessionReady(int localVideoPort, int localControlPort) {
                        runOnUiThread(() -> {
                            if (generation != sessionGeneration) {
                                return;
                            }
                            sessionView.setStatus(getString(R.string.status_connected, stored.name,
                                    localVideoPort, localControlPort));
                        });
                    }

                    @Override
                    public void onSessionEnded(String reason, String detail) {
                        runOnUiThread(() -> {
                            if (generation != sessionGeneration) {
                                return;
                            }
                            session = null;
                            returnHome(reasonText(reason, detail));
                        });
                    }

                    @Override
                    public void onStats(SessionStats stats) {
                        runOnUiThread(() -> {
                            if (generation != sessionGeneration) {
                                return;
                            }
                            sessionView.setStats(stats);
                        });
                    }

                    @Override
                    public void onVideoSize(int width, int height) {
                        runOnUiThread(() -> {
                            if (generation != sessionGeneration) {
                                return;
                            }
                            sessionView.setVideoSize(width, height);
                        });
                    }
                });

        homeView.setVisibility(View.GONE);
        sessionView.setVisibility(View.VISIBLE);
        sessionView.reset();
        sessionView.setDeviceName(stored.name);
        sessionView.setStatus(getString(R.string.status_connecting, stored.host));

        session.setSurface(sessionView.videoView().getHolder().getSurface());
        session.start();

        if (device.code.isEmpty()) {
            sessionView.showNotice(getString(R.string.device_saved, spaced(stored.code)));
        }
    }

    private void disconnect(String notice) {
        ViewerSession active = session;
        session = null;
        sessionGeneration++;
        if (active != null) {
            active.stop();
        }
        returnHome(notice);
    }

    private void returnHome(String notice) {
        if (sessionView.getVisibility() != View.VISIBLE) {
            if (notice != null) {
                homeView.showNotice(notice);
            }
            return;
        }

        sessionView.setVisibility(View.GONE);
        homeView.setVisibility(View.VISIBLE);
        homeView.refreshDevices();
        if (notice != null) {
            homeView.showNotice(notice);
        }
    }

    private void attachSurface(Surface surface) {
        ViewerSession active = session;
        if (active != null) {
            active.setSurface(surface);
        }
    }

    /* ------------------------------------------------------------------ */
    /* Keyboard                                                            */
    /* ------------------------------------------------------------------ */

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        ViewerSession active = session;
        if (active == null || sessionView.getVisibility() != View.VISIBLE) {
            return super.dispatchKeyEvent(event);
        }

        View focused = getCurrentFocus();
        if (focused instanceof EditText && focused != imeAnchor) {
            return super.dispatchKeyEvent(event);
        }

        int keyCode = event.getKeyCode();
        if (keyCode == KeyEvent.KEYCODE_BACK) {
            if (event.getAction() == KeyEvent.ACTION_UP) {
                disconnect(getString(R.string.status_stopped));
            }
            return true;
        }

        int packed = KeyMapper.map(keyCode);
        if (packed == KeyMapper.UNSUPPORTED) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                noticeUnsupportedKey(keyCode);
            }
            return super.dispatchKeyEvent(event);
        }

        boolean isDown = event.getAction() == KeyEvent.ACTION_DOWN;
        if (isDown && event.getRepeatCount() > 0) {
            // The host repeats a held key itself; forwarding Android's repeats would double it.
            return true;
        }

        active.sendKey(KeyMapper.scanCode(packed), KeyMapper.extended(packed), isDown);
        return true;
    }

    private void showKeyboard() {
        imeAnchor.requestFocus();
        InputMethodManager manager = getSystemService(InputMethodManager.class);
        if (manager != null) {
            manager.showSoftInput(imeAnchor, InputMethodManager.SHOW_IMPLICIT);
        }
    }

    private void noticeUnsupportedKey(int keyCode) {
        long now = SystemClock.elapsedRealtime();
        if (now - lastKeyNoticeMs < KEY_NOTICE_MIN_INTERVAL_MS) {
            return;
        }

        lastKeyNoticeMs = now;
        sessionView.showNotice(getString(R.string.key_unsupported,
                KeyEvent.keyCodeToString(keyCode)));
    }

    /* ------------------------------------------------------------------ */
    /* Back, lifecycle and immersive mode                                  */
    /* ------------------------------------------------------------------ */

    @SuppressWarnings("deprecation")
    @Override
    public void onBackPressed() {
        if (session != null) {
            disconnect(getString(R.string.status_stopped));
            return;
        }
        if (homeView.currentTab() != 0) {
            homeView.showDevicesTab();
            return;
        }
        super.onBackPressed();
    }

    @Override
    protected void onResume() {
        super.onResume();
        enterImmersive();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) {
            enterImmersive();
        }
    }

    @Override
    protected void onPause() {
        // Losing the foreground must not leave Ctrl or a mouse button held on the host.
        ViewerSession active = session;
        if (active != null) {
            active.sendReleaseAll();
        }
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        ViewerSession active = session;
        session = null;
        sessionGeneration++;
        if (active != null) {
            active.stop();
        }
        super.onDestroy();
    }

    private void enterImmersive() {
        View decor = getWindow().getDecorView();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            WindowInsetsController controller = decor.getWindowInsetsController();
            if (controller != null) {
                controller.hide(WindowInsets.Type.systemBars());
                controller.setSystemBarsBehavior(
                        WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
            return;
        }

        enterImmersiveLegacy(decor);
    }

    @SuppressWarnings("deprecation")
    private void enterImmersiveLegacy(View decor) {
        decor.setSystemUiVisibility(View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                | View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
    }

    /* ------------------------------------------------------------------ */
    /* Text helpers                                                        */
    /* ------------------------------------------------------------------ */

    private String reasonText(String reason, String detail) {
        switch (reason) {
            case ViewerSession.REASON_LIBRARY_MISSING:
                return getString(R.string.reason_library_missing);
            case ViewerSession.REASON_BRIDGE_MISSING:
                return getString(R.string.reason_bridge_missing);
            case ViewerSession.REASON_CREATE_FAILED:
                return getString(R.string.reason_create_failed);
            case ViewerSession.REASON_BIND_FAILED:
            case ViewerSession.REASON_PEER_FAILED:
            case ViewerSession.REASON_START_FAILED:
            case ViewerSession.REASON_CRASHED:
                return getString(R.string.reason_failed, detail.isEmpty() ? reason : detail);
            default:
                return getString(R.string.status_stopped);
        }
    }

    private String versionName() {
        try {
            return getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
        } catch (PackageManager.NameNotFoundException error) {
            return "?";
        }
    }

    private String coreVersion() {
        if (!NativeCore.isLoaded()) {
            return getString(R.string.core_missing);
        }
        int version = NativeCore.version();
        return String.format(Locale.US, "%d.%d.%d", (version >> 16) & 0xFF, (version >> 8) & 0xFF,
                version & 0xFF);
    }

    private static String spaced(String code) {
        if (code.length() != 9) {
            return code;
        }
        return code.substring(0, 3) + " " + code.substring(3, 6) + " " + code.substring(6);
    }
}

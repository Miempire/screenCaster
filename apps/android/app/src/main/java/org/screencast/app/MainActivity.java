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

import java.io.IOException;
import java.net.InetAddress;
import java.util.Locale;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * The Android control end. The launcher ({@link HomeView}) owns the device book and the
 * nine-digit code; once a device is chosen the activity swaps to {@link SessionView}, which keeps
 * the decoded picture on a Surface and exposes the floating session controls.
 */
public final class MainActivity extends Activity {

    private static final long KEY_NOTICE_MIN_INTERVAL_MS = 1500;
    private static final long PAIR_TIMEOUT_MS = 15_000;
    private static final Object EVENT_SESSION_CLOSED = new Object();

    private FrameLayout root;
    private HomeView homeView;
    private SessionView sessionView;
    private EditText imeAnchor;

    private ViewerSession session;
    private DeviceBook.Device currentDevice;
    private int sessionGeneration;
    private long lastKeyNoticeMs;

    private volatile SignalingClient signaling;
    private Thread signalingThread;

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
            public void onPair(String code) {
                pair(code);
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
        } else {
            stopSignaling();
        }

        if (!NativeCore.isLoaded()) {
            homeView.showNotice(getString(R.string.native_error));
            return;
        }

        final DeviceBook.Device stored = homeView.remember(device);
        currentDevice = stored;

        final int generation = ++sessionGeneration;
        session = new ViewerSession(ViewerSession.DEFAULT_SECRET, ViewerSession.DEFAULT_SESSION_ID,
                stored.host, stored.videoPort, stored.controlPort,
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

    private void pair(String code) {
        if (session != null) {
            disconnect(null);
        } else {
            stopSignaling();
        }
        if (!NativeCore.isLoaded() || !NativeCore.isSessionBridgePresent()) {
            homeView.showNotice(getString(R.string.native_error));
            return;
        }

        final int generation = ++sessionGeneration;
        final BlockingQueue<Object> events = new ArrayBlockingQueue<>(12);
        final AtomicBoolean mediaStarted = new AtomicBoolean();
        SignalingClient client = new SignalingClient(ServerConfig.HOST, ServerConfig.PORT,
                ServerConfig.deviceId(this), SignalingClient.ROLE_VIEWER,
                new SignalingClient.Listener() {
                    @Override
                    public void onSessionClosed(SignalingClient.SessionInfo view) {
                        if (!mediaStarted.get()) {
                            events.offer(EVENT_SESSION_CLOSED);
                        }
                    }

                    @Override
                    public void onPeerKey(SignalingClient.PeerKeyMessage message) {
                        if (message.type == SignalingWire.PEER_KEY_OFFER) {
                            events.offer(message);
                        }
                    }

                    @Override
                    public void onFaulted(Throwable error) {
                        events.offer(error);
                    }
                });

        signaling = client;
        homeView.showNotice(getString(R.string.pair_connecting));
        signalingThread = new Thread(
                () -> runPairing(client, code, events, mediaStarted, generation),
                "screencast-signaling");
        signalingThread.start();
    }

    private void runPairing(SignalingClient client, String code, BlockingQueue<Object> events,
            AtomicBoolean mediaStarted, int generation) {
        ViewerSession viewer = null;
        try {
            client.start();
            SignalingClient.SessionInfo joined = client.joinSession(code);
            runOnUiThread(() -> {
                if (generation == sessionGeneration) {
                    homeView.showNotice(getString(R.string.pair_waiting));
                }
            });

            Object event = awaitEvent(events,
                    SystemClock.elapsedRealtime() + PAIR_TIMEOUT_MS);
            if (event == EVENT_SESSION_CLOSED) {
                throw new PairingException(R.string.pair_code_invalid);
            }
            if (event instanceof Throwable) {
                throw new PairingException(R.string.pair_unreachable);
            }
            if (!(event instanceof SignalingClient.PeerKeyMessage)) {
                throw new PairingException(R.string.pair_timeout);
            }

            SignalingClient.PeerKeyMessage offer = (SignalingClient.PeerKeyMessage) event;
            PeerMediaInfo hostMedia = PeerMediaInfo.decode(offer.payload);
            if (hostMedia == null) {
                throw new PairingException(R.string.pair_unreachable);
            }
            byte[] secret = MediaKeys.deriveMediaSecret(code, joined.sessionId);
            viewer = new ViewerSession(secret, joined.sessionId, signalingListener(generation));

            String reason = viewer.bindLocal();
            if (reason != null) {
                throw new PairingException(R.string.reason_failed,
                        describeViewerFailure(reason, viewer.lastError()));
            }

            PeerMediaInfo viewerMedia = new PeerMediaInfo(PeerMediaInfo.VIEWER_ROLE,
                    viewer.localVideoPort(), viewer.localControlPort(),
                    PeerMediaInfo.localIPv4Addresses());
            client.sendPeerKey(SignalingWire.PEER_KEY_ANSWER, joined.sessionId,
                    viewerMedia.encode());
            if (events.contains(EVENT_SESSION_CLOSED)) {
                throw new PairingException(R.string.pair_code_invalid);
            }

            InetAddress address = PeerMediaInfo.choosePeerAddress(hostMedia, joined.hostEndpoint,
                    joined.viewerEndpoint);
            final ViewerSession ready = viewer;
            final String hostAddress = address.getHostAddress();
            final int videoPort = hostMedia.videoPort;
            final int controlPort = hostMedia.controlPort;
            runOnUiThread(() -> {
                if (generation != sessionGeneration) {
                    ready.stop();
                    return;
                }
                installPairing(ready);
            });

            mediaStarted.set(true);
            String started = viewer.startMedia(hostAddress, videoPort, controlPort);
            if (started != null) {
                mediaStarted.set(false);
                throw new PairingException(R.string.reason_failed,
                        describeViewerFailure(started, viewer.lastError()));
            }
        } catch (PairingException error) {
            failPairing(client, viewer, error, generation);
        } catch (SignalingClient.SignalingException error) {
            int notice = error.code == SignalingWire.ERROR_NOT_FOUND
                    || error.code == SignalingWire.ERROR_INVALID_STATE
                    || error.code == SignalingWire.ERROR_INVITE_INVALID
                    || error.code == SignalingWire.ERROR_BUSY
                    ? R.string.pair_code_invalid
                    : R.string.pair_unreachable;
            failPairing(client, viewer, new PairingException(notice), generation);
        } catch (IOException | TimeoutException | InterruptedException error) {
            failPairing(client, viewer, new PairingException(R.string.pair_unreachable),
                    generation);
        } catch (RuntimeException error) {
            failPairing(client, viewer, new PairingException(R.string.reason_failed,
                    error.getClass().getSimpleName() + ": " + error.getMessage()), generation);
        }
    }

    private static Object awaitEvent(BlockingQueue<Object> events, long deadline)
            throws InterruptedException {
        long remaining = deadline - SystemClock.elapsedRealtime();
        if (remaining <= 0) {
            return null;
        }
        return events.poll(remaining, TimeUnit.MILLISECONDS);
    }

    private void installPairing(ViewerSession viewer) {
        session = viewer;
        signalingThread = null;
        homeView.setVisibility(View.GONE);
        sessionView.setVisibility(View.VISIBLE);
        sessionView.reset();
        sessionView.setDeviceName(getString(R.string.server_display));
        sessionView.setStatus(getString(R.string.status_connected,
                getString(R.string.server_display), viewer.localVideoPort(),
                viewer.localControlPort()));
        viewer.setSurface(sessionView.videoView().getHolder().getSurface());
        homeView.clearCode();
    }

    private void failPairing(SignalingClient client, ViewerSession viewer, PairingException error,
            int generation) {
        if (viewer != null) {
            viewer.stop();
        }
        client.close();
        String text = error.detail == null
                ? getString(error.noticeRes)
                : getString(error.noticeRes, error.detail);
        runOnUiThread(() -> {
            if (generation != sessionGeneration) {
                return;
            }
            if (signaling == client) {
                signaling = null;
            }
            signalingThread = null;
            if (session == viewer) {
                session = null;
                sessionGeneration++;
                sessionView.setVisibility(View.GONE);
                homeView.setVisibility(View.VISIBLE);
                homeView.refreshDevices();
            }
            homeView.showNotice(text);
            homeView.focusCode();
        });
    }

    private ViewerSession.Listener signalingListener(int generation) {
        return new ViewerSession.Listener() {
            @Override
            public void onSessionReady(int localVideoPort, int localControlPort) {
            }

            @Override
            public void onSessionEnded(String reason, String detail) {
                runOnUiThread(() -> {
                    if (generation != sessionGeneration) {
                        return;
                    }
                    session = null;
                    sessionGeneration++;
                    stopSignaling();
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
        };
    }

    private static String describeViewerFailure(String reason, String detail) {
        if (detail != null && !detail.isEmpty()) {
            return detail;
        }
        return reason;
    }

    private static final class PairingException extends Exception {
        final int noticeRes;
        final String detail;

        PairingException(int noticeRes) {
            this(noticeRes, null);
        }

        PairingException(int noticeRes, String detail) {
            this.noticeRes = noticeRes;
            this.detail = detail;
        }
    }

    private void disconnect(String notice) {
        ViewerSession active = session;
        session = null;
        sessionGeneration++;
        if (active != null) {
            active.stop();
        }
        stopSignaling();
        returnHome(notice);
    }

    private void stopSignaling() {
        SignalingClient client = signaling;
        signaling = null;
        Thread thread = signalingThread;
        signalingThread = null;
        if (thread != null) {
            thread.interrupt();
        }
        if (client != null) {
            new Thread(client::close, "screencast-signaling-close").start();
        }
    }

    private boolean pairingInProgress() {
        Thread thread = signalingThread;
        return thread != null && thread.isAlive();
    }

    private void cancelPairing() {
        stopSignaling();
        sessionGeneration++;
        homeView.showNotice(getString(R.string.status_stopped));
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
        if (pairingInProgress()) {
            cancelPairing();
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
        stopSignaling();
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

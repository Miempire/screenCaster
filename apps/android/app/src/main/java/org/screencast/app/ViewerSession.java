package org.screencast.app;

import android.os.SystemClock;
import android.view.Surface;

/**
 * The controlling side of a live session: owns the Rust session handle, drains its event queue,
 * feeds the H.264 decoder and forwards input events.
 *
 * <p>This is the Java equivalent of {@code src/ScreenCast.Windows/Session/ViewerSession.cs};
 * the ordering and the recovery rules are deliberately the same:</p>
 * <ul>
 *   <li>create → bind(0,0) → set peer → start → lease acquire → request key frame;</li>
 *   <li>a dropped frame or a failed decode breaks the reference chain, so a new key frame is
 *       requested, rate limited to one request per 500 ms so a bad link cannot flood the host;</li>
 *   <li>on shutdown the held input state is released ({@code ReleaseAll}) and the input lease is
 *       given back before the session handle is destroyed.</li>
 * </ul>
 *
 * <p>Everything happens on one worker thread. The Rust core owns its own receive threads and
 * ticker; {@code sc_session_poll} is non-blocking, so the loop sleeps 2 ms only when a full
 * sweep found no event.</p>
 */
final class ViewerSession implements Runnable {

    /** Delivered on the session thread; the UI is responsible for hopping to the main thread. */
    interface Listener {
        void onSessionReady(int localVideoPort, int localControlPort);

        /** @param detail technical detail such as {@code SC_ERR_IO}, empty when there is none */
        void onSessionEnded(String reason, String detail);

        void onStats(SessionStats stats);

        void onVideoSize(int width, int height);
    }

    /** Reason keys, resolved to text by the Activity. */
    static final String REASON_STOPPED = "stopped";
    static final String REASON_LIBRARY_MISSING = "library-missing";
    static final String REASON_BRIDGE_MISSING = "bridge-missing";
    static final String REASON_CREATE_FAILED = "create-failed";
    static final String REASON_BIND_FAILED = "bind-failed";
    static final String REASON_PEER_FAILED = "peer-failed";
    static final String REASON_START_FAILED = "start-failed";
    static final String REASON_CRASHED = "crashed";

    /**
     * Default development credentials, identical to {@code MainWindow.cs} and
     * {@code docs/deployment.md}: the shared secret is 00..1F and the session id A1..B0.
     */
    private static final byte[] DEFAULT_SECRET = decodeHex(
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");

    private static final byte[] DEFAULT_SESSION_ID = decodeHex("A1A2A3A4A5A6A7A8A9AAABACADAEAFB0");

    /** Key epoch; the host uses 1 and both sides must agree. */
    private static final int KEY_EPOCH = 1;

    private static final long KEY_FRAME_MIN_INTERVAL_MS = 500;
    private static final long STATS_INTERVAL_MS = 500;
    private static final long FPS_INTERVAL_MS = 1000;
    private static final long IDLE_SLEEP_MS = 2;
    private static final int MAX_EVENTS_PER_TURN = 64;

    private final String host;
    private final int peerVideoPort;
    private final int peerControlPort;
    private final Listener listener;

    private final long[] pollSlots = new long[NativeCore.POLL_SLOTS];
    private final long[] statsSlots = new long[NativeCore.STATS_SLOTS];
    private final int[] localPorts = new int[2];
    private final Object handleLock = new Object();

    private final H264Decoder decoder;

    private volatile boolean running;
    private volatile long handle;
    private Thread thread;

    private int decodedFrames;
    private int renderedFrames;
    private long assembledFrames;
    private long lastDroppedFrames;
    private long lastKeyFrameRequestMs;
    private double fps;
    private String lastError = "";

    ViewerSession(String host, int peerVideoPort, int peerControlPort, Listener listener) {
        this.host = host;
        this.peerVideoPort = peerVideoPort;
        this.peerControlPort = peerControlPort;
        this.listener = listener;
        this.decoder = new H264Decoder(new H264Decoder.Callback() {
            @Override
            public void onVideoSizeChanged(int width, int height) {
                listener.onVideoSize(width, height);
            }

            @Override
            public void onPictureRendered() {
                renderedFrames++;
            }

            @Override
            public void onDecodeError(String message) {
                lastError = message;
            }

            @Override
            public void onKeyFrameRequired() {
                requestKeyFrame();
            }
        });
    }

    /** Attach the SurfaceView's Surface; may be called before or after {@link #start()}. */
    void setSurface(Surface surface) {
        decoder.setSurface(surface);
    }

    synchronized void start() {
        if (thread != null) {
            return;
        }

        running = true;
        thread = new Thread(this, "screencast-viewer");
        thread.start();
    }

    /** Ask the worker to stop and wait for it; the handle is destroyed before this returns. */
    void stop() {
        Thread worker;
        synchronized (this) {
            running = false;
            worker = thread;
            thread = null;
        }

        if (worker == null || worker == Thread.currentThread()) {
            return;
        }

        worker.interrupt();
        try {
            worker.join(3000);
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
        }
    }

    /* ------------------------------------------------------------------ */
    /* Input forwarding                                                    */
    /* ------------------------------------------------------------------ */

    void sendKey(int scanCode, boolean extended, boolean isDown) {
        sendControl(ControlEventType.KEY, InputCodec.encodeKey(scanCode, extended, isDown));
    }

    void sendMouseMove(int normalizedX, int normalizedY) {
        sendControl(ControlEventType.MOUSE_MOVE,
                InputCodec.encodeMouseMove(normalizedX, normalizedY, 0));
    }

    void sendMouseButton(int button, boolean isDown) {
        sendControl(ControlEventType.MOUSE_BUTTON, InputCodec.encodeMouseButton(button, isDown));
    }

    void sendWheel(int deltaX, int deltaY) {
        sendControl(ControlEventType.WHEEL, InputCodec.encodeWheel(deltaX, deltaY));
    }

    void sendReleaseAll() {
        sendControl(ControlEventType.RELEASE_ALL, new byte[0]);
    }

    void sendLease(boolean acquire) {
        sendControl(ControlEventType.CONTROL_LEASE, new byte[] { (byte) (acquire ? 1 : 0) });
    }

    /**
     * Ask the host for a fresh key frame, at most once every 500 ms.
     *
     * <p>Without this request a lost IDR leaves the picture broken forever: every following
     * frame references data the decoder never saw.</p>
     */
    void requestKeyFrame() {
        long now = SystemClock.elapsedRealtime();
        if (lastKeyFrameRequestMs != 0 && now - lastKeyFrameRequestMs < KEY_FRAME_MIN_INTERVAL_MS) {
            return;
        }

        lastKeyFrameRequestMs = now;
        sendControl(ControlEventType.REQUEST_KEY_FRAME, new byte[0]);
    }

    private int sendControl(int eventType, byte[] body) {
        synchronized (handleLock) {
            long active = handle;
            if (active == 0) {
                return NativeCore.SC_ERR_STATE;
            }
            return NativeCore.nativeSendControl(active, eventType, body);
        }
    }

    /* ------------------------------------------------------------------ */
    /* Worker thread                                                       */
    /* ------------------------------------------------------------------ */

    @Override
    public void run() {
        String reason = REASON_STOPPED;
        try {
            reason = open();
            if (reason == null) {
                loop();
                reason = REASON_STOPPED;
            }
        } catch (UnsatisfiedLinkError error) {
            reason = REASON_BRIDGE_MISSING;
        } catch (RuntimeException error) {
            lastError = error.getClass().getSimpleName() + ": " + error.getMessage();
            reason = REASON_CRASHED;
        } finally {
            close();
            listener.onSessionEnded(reason, lastError);
        }
    }

    /** @return null when the session is up, otherwise the failure reason key */
    private String open() {
        if (!NativeCore.isLoaded()) {
            return REASON_LIBRARY_MISSING;
        }
        if (!NativeCore.isSessionBridgePresent()) {
            return REASON_BRIDGE_MISSING;
        }

        // Process-local role selection, mirroring what the Windows client does on startup.
        NativeCore.setRole(1);

        long created = NativeCore.nativeCreate(DEFAULT_SECRET, DEFAULT_SESSION_ID, KEY_EPOCH, false);
        if (created == 0) {
            return REASON_CREATE_FAILED;
        }
        synchronized (handleLock) {
            handle = created;
        }

        // Port 0 selects an ephemeral port: the viewer only needs a return path.
        int bound = NativeCore.nativeBind(created, 0, 0);
        if (bound <= 0) {
            lastError = NativeCore.describeStatus(bound);
            return REASON_BIND_FAILED;
        }

        int peer = NativeCore.nativeSetPeer(created, host, peerVideoPort, host, peerControlPort);
        if (peer != NativeCore.SC_OK) {
            lastError = NativeCore.describeStatus(peer);
            return REASON_PEER_FAILED;
        }

        int started = NativeCore.nativeStart(created);
        if (started != NativeCore.SC_OK) {
            lastError = NativeCore.describeStatus(started);
            return REASON_START_FAILED;
        }

        if (NativeCore.nativeLocalPorts(created, localPorts) == NativeCore.SC_OK) {
            listener.onSessionReady(localPorts[0], localPorts[1]);
        } else {
            listener.onSessionReady(0, 0);
        }

        // Take the input lease, then ask for the first key frame so decoding can start.
        sendLease(true);
        requestKeyFrame();
        return null;
    }

    private void loop() {
        long lastStatsMs = SystemClock.elapsedRealtime();
        long lastFpsMs = lastStatsMs;
        int framesAtLastFps = 0;

        while (running) {
            int events = 0;
            while (running && events < MAX_EVENTS_PER_TURN) {
                long active;
                synchronized (handleLock) {
                    active = handle;
                }
                if (active == 0) {
                    return;
                }

                byte[] payload = NativeCore.nativePoll(active, pollSlots);
                int result = (int) pollSlots[NativeCore.POLL_RESULT];
                if (result != 1) {
                    if (result < 0) {
                        lastError = NativeCore.describeStatus(result);
                        return;
                    }
                    break;
                }

                events++;
                dispatch(payload);
            }

            long now = SystemClock.elapsedRealtime();
            if (now - lastStatsMs >= STATS_INTERVAL_MS) {
                publishStats();
                lastStatsMs = now;
            }
            if (now - lastFpsMs >= FPS_INTERVAL_MS) {
                int rendered = renderedFrames;
                fps = (rendered - framesAtLastFps) * 1000.0 / Math.max(1, now - lastFpsMs);
                framesAtLastFps = rendered;
                lastFpsMs = now;
            }

            if (events == 0) {
                sleepQuietly(IDLE_SLEEP_MS);
            }
        }
    }

    private void dispatch(byte[] payload) {
        int kind = (int) pollSlots[NativeCore.POLL_KIND];
        if (kind == NativeCore.SC_EVENT_VIDEO_FRAME) {
            handleVideoFrame(payload);
        }
        // SC_EVENT_CONTROL / SC_EVENT_CONTROL_EXPIRED / SC_EVENT_PATH carry nothing the viewer
        // acts on today: the host is the input consumer and path changes surface through stats.
    }

    private void handleVideoFrame(byte[] payload) {
        if (payload == null || payload.length == 0) {
            return;
        }

        int codec = (int) pollSlots[NativeCore.POLL_CODEC];
        if (codec != NativeCore.CODEC_UNKNOWN && codec != NativeCore.CODEC_H264) {
            lastError = "unsupported codec " + codec;
            return;
        }

        int flags = (int) pollSlots[NativeCore.POLL_FLAGS];
        if ((flags & NativeCore.FRAME_FLAG_DISCONTINUITY) != 0) {
            decoder.requestResync();
        }

        assembledFrames++;
        long timestampUs = pollSlots[NativeCore.POLL_CAPTURE_US];
        decoder.feed(payload, flags, timestampUs);
        decoder.drain();
        decodedFrames++;
    }

    private void publishStats() {
        long active;
        synchronized (handleLock) {
            active = handle;
        }
        if (active == 0) {
            return;
        }

        if (NativeCore.nativeStats(active, statsSlots) != NativeCore.SC_OK) {
            return;
        }

        SessionStats snapshot = SessionStats.fromSlots(statsSlots, decoder.width(),
                decoder.height(), fps, decodedFrames);

        // A hole in the frame sequence means the decoder is missing a reference: recover with a
        // key frame. Same rule as the Windows viewer.
        if (snapshot.droppedFrames > lastDroppedFrames) {
            lastDroppedFrames = snapshot.droppedFrames;
            requestKeyFrame();
        }

        listener.onStats(snapshot);
    }

    private void close() {
        // Best effort: the session may already be gone.
        sendReleaseAll();
        sendLease(false);

        long active;
        synchronized (handleLock) {
            active = handle;
            handle = 0;
        }
        if (active != 0) {
            NativeCore.nativeDestroy(active);
        }

        decoder.release();
    }

    private static void sleepQuietly(long millis) {
        try {
            Thread.sleep(millis);
        } catch (InterruptedException interrupted) {
            Thread.currentThread().interrupt();
        }
    }

    private static byte[] decodeHex(String text) {
        int length = text.length() / 2;
        byte[] bytes = new byte[length];
        for (int index = 0; index < length; index++) {
            bytes[index] = (byte) Integer.parseInt(text.substring(index * 2, index * 2 + 2), 16);
        }
        return bytes;
    }
}

package org.screencast.app;

/**
 * The only Java class that touches the Rust network core.
 *
 * <p>Every method maps one-to-one onto a symbol exported by
 * {@code native/include/screencast_core.h} through the JNI shim in
 * {@code apps/android/native-jni/android_jni.rs}. The library is loaded once; a missing ABI
 * library leaves {@link #isLoaded()} false instead of crashing at class initialisation, so the
 * UI can report the problem instead of dying with {@link UnsatisfiedLinkError}.</p>
 *
 * <p>Two conventions keep the video path allocation-free:</p>
 * <ul>
 *   <li>{@link #nativePoll(long, long[])} writes the scalar fields of one {@code sc_event_t}
 *       into a caller-owned {@code long[]} and returns only the payload bytes. A Java object
 *       per event (or boxing {@code u64} fields) would put the garbage collector inside the
 *       frame loop.</li>
 *   <li>{@link #nativeStats(long, long[])} writes all {@code sc_stats_t} fields into a
 *       caller-owned {@code long[]}, with the two {@code double} RTT fields bit-encoded.</li>
 * </ul>
 *
 * <p>The {@code sc_event_t::data} pointer never reaches Java: the shim copies it into a fresh
 * {@code byte[]} before releasing the frame buffer.</p>
 */
final class NativeCore {

    /* ---- Status codes, negative on failure (screencast_core.h). ---- */
    static final int SC_OK = 0;
    static final int SC_ERR_NULL = -1;
    static final int SC_ERR_INVALID_ARGUMENT = -2;
    static final int SC_ERR_NOT_BOUND = -3;
    static final int SC_ERR_ALREADY_BOUND = -4;
    static final int SC_ERR_IO = -5;
    static final int SC_ERR_STATE = -6;
    static final int SC_ERR_TOO_LARGE = -7;
    static final int SC_ERR_PROTOCOL = -8;

    /* ---- Event kinds. ---- */
    static final int SC_EVENT_NONE = 0;
    static final int SC_EVENT_VIDEO_FRAME = 1;
    static final int SC_EVENT_CONTROL = 2;
    static final int SC_EVENT_PATH = 3;
    static final int SC_EVENT_CONTROL_EXPIRED = 4;

    /* ---- Path status values carried by SC_EVENT_PATH and sc_stats_t. ---- */
    static final int SC_PATH_PROBING = 0;
    static final int SC_PATH_ALIVE = 1;
    static final int SC_PATH_DOWN = 2;

    /* ---- Video frame flags (sc_event_t.flags). ---- */
    static final int FRAME_FLAG_KEY = 1;
    static final int FRAME_FLAG_CONFIGURATION = 2;
    static final int FRAME_FLAG_DISCONTINUITY = 4;

    /* ---- Codec identifiers. ---- */
    static final int CODEC_UNKNOWN = 0;
    static final int CODEC_H264 = 1;

    /* ---- sc_event_t layout as delivered by nativePoll. ---- */
    static final int POLL_SLOTS = 12;
    static final int POLL_RESULT = 0;
    static final int POLL_KIND = 1;
    static final int POLL_STATUS = 2;
    static final int POLL_COMPONENT = 3;
    static final int POLL_EVENT_TYPE = 4;
    static final int POLL_FLAGS = 5;
    static final int POLL_CODEC = 6;
    static final int POLL_FRAME_ID = 7;
    static final int POLL_CAPTURE_US = 8;
    static final int POLL_RTT_US = 9;
    static final int POLL_CONFIG_ID = 10;
    static final int POLL_DATA_LEN = 11;

    /* ---- sc_stats_t layout as delivered by nativeStats. ---- */
    static final int STATS_SLOTS = 22;
    static final int STATS_VIDEO_RECEIVED = 0;
    static final int STATS_VIDEO_REJECTED = 1;
    static final int STATS_CONTROL_RECEIVED = 2;
    static final int STATS_CONTROL_REJECTED = 3;
    static final int STATS_CONTROL_FRAMES_DROPPED = 4;
    static final int STATS_ASSEMBLED_FRAMES = 5;
    static final int STATS_DROPPED_FRAMES = 6;
    static final int STATS_DROPPED_FRAGMENTS = 7;
    static final int STATS_VIDEO_BYTES_SENT = 8;
    static final int STATS_CONTROL_BYTES_SENT = 9;
    static final int STATS_REPLAY_REJECTED = 10;
    static final int STATS_PROBES_SENT = 11;
    static final int STATS_PROBES_ANSWERED = 12;
    static final int STATS_EVENTS_DROPPED = 13;
    static final int STATS_PARTIAL_FRAMES = 14;
    static final int STATS_VIDEO_RTT_US = 15;
    static final int STATS_CONTROL_RTT_US = 16;
    static final int STATS_VIDEO_PATH_STATUS = 17;
    static final int STATS_CONTROL_PATH_STATUS = 18;
    static final int STATS_VIDEO_LOCAL_PORT = 19;
    static final int STATS_CONTROL_LOCAL_PORT = 20;
    static final int STATS_RESERVED = 21;

    private static final boolean LOADED;

    static {
        boolean loaded;
        try {
            System.loadLibrary("screencast_core");
            loaded = true;
        } catch (UnsatisfiedLinkError error) {
            loaded = false;
        }
        LOADED = loaded;
    }

    private NativeCore() {
    }

    /** True when {@code libscreencast_core.so} was packaged for this ABI and loaded. */
    static boolean isLoaded() {
        return LOADED;
    }

    /**
     * Probes whether the session symbols exist.
     *
     * <p>The core library can be present while the JNI session layer is not (for example when
     * {@code native/src/android_jni.rs} has not been wired in yet). Calling an absent native
     * method throws {@link UnsatisfiedLinkError}, which is catchable; probing once here turns a
     * hard crash at connect time into a readable message.</p>
     */
    static boolean isSessionBridgePresent() {
        if (!LOADED) {
            return false;
        }
        try {
            // Handle 0 is rejected by the shim with SC_ERR_NULL, so this has no side effect.
            nativeBind(0L, 0, 0);
            return true;
        } catch (UnsatisfiedLinkError error) {
            return false;
        }
    }

    /* ---- 0.1.0 foundation functions. ---- */

    static native int version();

    static native int getRole();

    static native int setRole(int role);

    /* ---- Session lifecycle. ---- */

    static native long nativeCreate(byte[] secret32, byte[] sessionId16, int epoch, boolean isHost);

    static native int nativeBind(long handle, int videoPort, int controlPort);

    static native int nativeSetPeer(long handle, String videoAddr, int videoPort, String controlAddr,
            int controlPort);

    static native int nativeStart(long handle);

    static native int nativeSendControl(long handle, int eventType, byte[] body);

    static native int nativeSendFrame(long handle, byte[] data, long frameId, long timestampUs,
            int configId, int flags, int codec);

    static native byte[] nativePoll(long handle, long[] out);

    static native int nativeStats(long handle, long[] out);

    static native int nativeLocalPorts(long handle, int[] out);

    static native void nativeDestroy(long handle);

    /** Stable technical name of a status code, for logs and the status line. */
    static String describeStatus(int status) {
        switch (status) {
            case SC_OK:
                return "SC_OK";
            case SC_ERR_NULL:
                return "SC_ERR_NULL";
            case SC_ERR_INVALID_ARGUMENT:
                return "SC_ERR_INVALID_ARGUMENT";
            case SC_ERR_NOT_BOUND:
                return "SC_ERR_NOT_BOUND";
            case SC_ERR_ALREADY_BOUND:
                return "SC_ERR_ALREADY_BOUND";
            case SC_ERR_IO:
                return "SC_ERR_IO";
            case SC_ERR_STATE:
                return "SC_ERR_STATE";
            case SC_ERR_TOO_LARGE:
                return "SC_ERR_TOO_LARGE";
            case SC_ERR_PROTOCOL:
                return "SC_ERR_PROTOCOL";
            default:
                return "status " + status;
        }
    }
}

package org.screencast.app;

/**
 * One snapshot of what the viewer can report: the counters from {@code sc_stats_t} that the UI
 * displays, plus the counters the platform layer owns (fps, decoded frames, picture size).
 *
 * <p>Immutable so it can be handed from the session thread to the UI thread without a lock.</p>
 */
final class SessionStats {

    final int width;
    final int height;
    final double fps;
    final int decodedFrames;
    final long droppedFrames;
    final long videoDatagramsRejected;
    /** Smoothed video path RTT in milliseconds; negative when no sample exists yet. */
    final double videoRttMs;
    /** One of {@link NativeCore#SC_PATH_PROBING}, {@code SC_PATH_ALIVE}, {@code SC_PATH_DOWN}. */
    final int videoPathStatus;

    private SessionStats(int width, int height, double fps, int decodedFrames, long droppedFrames,
            long videoDatagramsRejected, double videoRttMs, int videoPathStatus) {
        this.width = width;
        this.height = height;
        this.fps = fps;
        this.decodedFrames = decodedFrames;
        this.droppedFrames = droppedFrames;
        this.videoDatagramsRejected = videoDatagramsRejected;
        this.videoRttMs = videoRttMs;
        this.videoPathStatus = videoPathStatus;
    }

    /**
     * Decode the {@code long[]} produced by {@link NativeCore#nativeStats(long, long[])}.
     *
     * <p>The two RTT fields are {@code double} in C and travel as raw bits; {@code -1} means
     * "no sample yet" and stays negative here so the UI can print a dash.</p>
     */
    static SessionStats fromSlots(long[] slots, int width, int height, double fps,
            int decodedFrames) {
        return new SessionStats(
                width,
                height,
                fps,
                decodedFrames,
                slots[NativeCore.STATS_DROPPED_FRAMES],
                slots[NativeCore.STATS_VIDEO_REJECTED],
                Double.longBitsToDouble(slots[NativeCore.STATS_VIDEO_RTT_US]) / 1000.0,
                (int) slots[NativeCore.STATS_VIDEO_PATH_STATUS]);
    }

    boolean hasRtt() {
        return videoRttMs >= 0;
    }
}

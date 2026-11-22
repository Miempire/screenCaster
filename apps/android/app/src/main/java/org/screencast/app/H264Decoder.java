package org.screencast.app;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Build;
import android.view.Surface;

import java.io.IOException;
import java.nio.ByteBuffer;

/**
 * Hardware H.264 decoder that renders straight into a {@link Surface} (a SurfaceView's buffer
 * queue), so no decoded pixel ever passes through Java.
 *
 * <p>Configuration choices, and why:</p>
 * <ul>
 *   <li><b>Annex-B in, Surface out.</b> The host sends complete Annex-B access units. The
 *       decoder is fed the bytes unchanged; SPS/PPS travel in-band and MediaCodec parses
 *       them, so the app never has to split NAL units itself.</li>
 *   <li><b>No key-frame flag.</b> {@code BUFFER_FLAG_KEY_FRAME} is not set: for
 *       {@code video/avc} the decoder detects an IDR from the bitstream, and setting the flag
 *       on a frame that is not actually an IDR makes some decoders emit errors.</li>
 *   <li><b>First frame must be a key frame.</b> Feeding a P frame before any IDR puts the
 *       decoder into a state where it cannot produce output; the decoder therefore refuses
 *       those frames and asks for a key frame instead.</li>
 *   <li><b>Low latency where available.</b> {@code KEY_LOW_LATENCY} exists from API 30; on
 *       older releases the decoder runs in its default mode.</li>
 *   <li><b>Rebuild on output format change.</b> A resolution change invalidates the configured
 *       surface size; the codec is torn down and rebuilt and a new key frame is requested.</li>
 * </ul>
 *
 * <p>All methods are {@code synchronized}: {@link #setSurface(Surface)} is called from the UI
 * thread while {@link #feed(byte[], int, long)} and {@link #drain()} run on the session thread.
 * Every call is non-blocking ({@code timeoutUs == 0}), so the UI thread never waits on the
 * decoder for more than a buffer hand-off.</p>
 */
final class H264Decoder {

    /** Callbacks are delivered on the session thread except {@link #onVideoSizeChanged}. */
    interface Callback {
        void onVideoSizeChanged(int width, int height);

        void onPictureRendered();

        void onDecodeError(String message);

        void onKeyFrameRequired();
    }

    private static final String MIME_TYPE = "video/avc";

    /**
     * Nominal size handed to {@code configure}. A decoder only needs a hint for Surface output;
     * the real geometry comes from the SPS, and the surface itself is sized by the view.
     */
    private static final int NOMINAL_WIDTH = 1920;
    private static final int NOMINAL_HEIGHT = 1080;

    private final Callback callback;
    private final MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();

    private MediaCodec codec;
    private Surface surface;
    private boolean sawKeyFrame;
    private boolean needsRebuild;
    private int width;
    private int height;
    private String lastError = "";

    H264Decoder(Callback callback) {
        this.callback = callback;
    }

    int width() {
        return width;
    }

    int height() {
        return height;
    }

    /**
     * Attach or detach the output surface.
     *
     * <p>Detaching tears the codec down immediately: a codec configured with a destroyed
     * surface would otherwise fail on the next frame.</p>
     */
    synchronized void setSurface(Surface newSurface) {
        if (surface == newSurface) {
            return;
        }

        teardown();
        surface = newSurface;
        sawKeyFrame = false;
        needsRebuild = false;
    }

    /** Release everything; safe to call twice. */
    synchronized void release() {
        teardown();
        surface = null;
        sawKeyFrame = false;
        needsRebuild = false;
    }

    /** A discontinuity means the reference chain is broken: drop state and ask for a key frame. */
    synchronized void requestResync() {
        sawKeyFrame = false;
        needsRebuild = true;
        callback.onKeyFrameRequired();
    }

    /**
     * Feed one Annex-B access unit.
     *
     * @param flags {@code sc_event_t.flags}: 1 key, 2 configuration, 4 discontinuity
     */
    synchronized void feed(byte[] data, int flags, long timestampUs) {
        if (surface == null || !surface.isValid()) {
            // Nothing to render into yet; a frame fed now would be decoded into nothing.
            callback.onKeyFrameRequired();
            return;
        }

        boolean isConfig = (flags & NativeCore.FRAME_FLAG_CONFIGURATION) != 0;
        boolean isKey = (flags & NativeCore.FRAME_FLAG_KEY) != 0;

        if (!isConfig && !sawKeyFrame && !isKey) {
            // P frames before the first IDR cannot decode: ask for one and drop this frame.
            callback.onKeyFrameRequired();
            return;
        }

        if (!ensureConfigured()) {
            return;
        }

        MediaCodec active = codec;
        if (active == null) {
            return;
        }

        int inputIndex = active.dequeueInputBuffer(0);
        if (inputIndex < 0) {
            // All input buffers are with the decoder: pull output first, then retry once.
            drainLocked();
            active = codec;
            if (active == null) {
                return;
            }
            inputIndex = active.dequeueInputBuffer(0);
        }

        if (inputIndex < 0) {
            lastError = "no input buffer";
            callback.onKeyFrameRequired();
            return;
        }

        ByteBuffer input = active.getInputBuffer(inputIndex);
        if (input == null) {
            lastError = "input buffer unavailable";
            callback.onKeyFrameRequired();
            return;
        }

        if (data.length > input.capacity()) {
            lastError = "access unit " + data.length + " > input buffer " + input.capacity();
            active.queueInputBuffer(inputIndex, 0, 0, timestampUs, 0);
            callback.onKeyFrameRequired();
            return;
        }

        input.clear();
        input.put(data);

        // Deliberately no BUFFER_FLAG_KEY_FRAME: the decoder reads the IDR from the bitstream.
        int bufferFlags = isConfig ? MediaCodec.BUFFER_FLAG_CODEC_CONFIG : 0;
        active.queueInputBuffer(inputIndex, 0, data.length, timestampUs, bufferFlags);

        if (isKey) {
            sawKeyFrame = true;
        }
    }

    /** Pull every picture the decoder has ready. Called after each {@link #feed}. */
    synchronized void drain() {
        drainLocked();
    }

    private void drainLocked() {
        MediaCodec active = codec;
        if (active == null) {
            return;
        }

        while (true) {
            int outputIndex;
            try {
                outputIndex = active.dequeueOutputBuffer(bufferInfo, 0);
            } catch (IllegalStateException error) {
                fail("dequeueOutputBuffer: " + error.getMessage());
                return;
            }

            if (outputIndex >= 0) {
                // Render immediately: the SurfaceView consumes the buffer on its own thread.
                active.releaseOutputBuffer(outputIndex, true);
                callback.onPictureRendered();
                continue;
            }

            if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                applyOutputFormat(active.getOutputFormat());
                if (needsRebuild) {
                    return;
                }
                continue;
            }

            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                return;
            }

            // Any other negative value is a codec error (-1 / -2 and vendor codes).
            // INFO_OUTPUT_BUFFERS_CHANGED (-3) is not handled: it was deprecated in API 21 and is
            // never returned on the API levels this app supports.
            fail("dequeueOutputBuffer returned " + outputIndex);
            return;
        }
    }

    private void applyOutputFormat(MediaFormat format) {
        if (format == null) {
            return;
        }

        int newWidth = format.containsKey(MediaFormat.KEY_WIDTH)
                ? format.getInteger(MediaFormat.KEY_WIDTH)
                : 0;
        int newHeight = format.containsKey(MediaFormat.KEY_HEIGHT)
                ? format.getInteger(MediaFormat.KEY_HEIGHT)
                : 0;

        if (format.containsKey("crop-left") && format.containsKey("crop-right")
                && format.containsKey("crop-top") && format.containsKey("crop-bottom")) {
            newWidth = format.getInteger("crop-right") - format.getInteger("crop-left") + 1;
            newHeight = format.getInteger("crop-bottom") - format.getInteger("crop-top") + 1;
        }

        if (newWidth <= 0 || newHeight <= 0) {
            return;
        }

        if (width == newWidth && height == newHeight) {
            return;
        }

        // A change after the first report means the stream renegotiated its size: the surface
        // must be re-laid-out, which destroys and recreates it, so the codec has to be rebuilt.
        if (width != 0) {
            needsRebuild = true;
        }

        width = newWidth;
        height = newHeight;
        callback.onVideoSizeChanged(newWidth, newHeight);
    }

    private boolean ensureConfigured() {
        if (codec != null && !needsRebuild) {
            return true;
        }

        teardown();
        needsRebuild = false;

        if (surface == null || !surface.isValid()) {
            return false;
        }

        MediaCodec created = null;
        try {
            created = MediaCodec.createDecoderByType(MIME_TYPE);
            MediaFormat format = MediaFormat.createVideoFormat(MIME_TYPE, NOMINAL_WIDTH,
                    NOMINAL_HEIGHT);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
            }
            created.configure(format, surface, null, 0);
            created.start();
            codec = created;
            sawKeyFrame = false;
            return true;
        } catch (IllegalArgumentException | IllegalStateException | IOException error) {
            if (created != null) {
                try {
                    created.release();
                } catch (IllegalStateException ignored) {
                    // The codec never started; release is best effort.
                }
            }
            codec = null;
            fail("configure: " + error.getMessage());
            return false;
        }
    }

    private void teardown() {
        MediaCodec active = codec;
        codec = null;
        if (active == null) {
            return;
        }

        try {
            active.stop();
        } catch (IllegalStateException ignored) {
            // Already stopped or never started.
        }
        try {
            active.release();
        } catch (IllegalStateException ignored) {
            // Nothing left to release.
        }
    }

    private void fail(String message) {
        lastError = message;
        teardown();
        sawKeyFrame = false;
        needsRebuild = true;
        callback.onDecodeError(message);
        callback.onKeyFrameRequired();
    }
}

package org.screencast.app;

import java.nio.ByteBuffer;

/**
 * Wire encoding for control event bodies, byte for byte identical to
 * {@code src/ScreenCast.Windows/Input/InputCodec.cs} and to the decoder in
 * {@code native/src/control.rs}.
 *
 * <p>Every body is fixed length and big-endian so the host can validate it without parsing a
 * variable structure from an untrusted peer. A single wrong byte here means the host silently
 * drops the event, which is why the sizes are pinned as constants and mirrored in the report.</p>
 */
final class InputCodec {

    static final int KEY_BODY_SIZE = 3;
    static final int MOUSE_MOVE_BODY_SIZE = 6;
    static final int MOUSE_BUTTON_BODY_SIZE = 2;
    static final int WHEEL_BODY_SIZE = 4;
    static final int TOUCH_BODY_SIZE = 7;

    /** Mouse button identifiers used on the wire. */
    static final byte BUTTON_LEFT = 0;
    static final byte BUTTON_RIGHT = 1;
    static final byte BUTTON_MIDDLE = 2;

    /** Touch phases used on the wire. */
    static final byte TOUCH_DOWN = 0;
    static final byte TOUCH_MOVE = 1;
    static final byte TOUCH_UP = 2;
    static final byte TOUCH_CANCEL = 3;

    private InputCodec() {
    }

    /** Key body: scanCode(2) flags(1), where bit0 = extended and bit1 = down. */
    static byte[] encodeKey(int scanCode, boolean extended, boolean isDown) {
        ByteBuffer body = ByteBuffer.allocate(KEY_BODY_SIZE);
        body.putShort((short) (scanCode & 0xFFFF));
        body.put((byte) ((extended ? 1 : 0) | (isDown ? 2 : 0)));
        return body.array();
    }

    /** Mouse move: normalizedX(2) normalizedY(2) monitorId(2), all 0..65535. */
    static byte[] encodeMouseMove(int normalizedX, int normalizedY, int monitorId) {
        ByteBuffer body = ByteBuffer.allocate(MOUSE_MOVE_BODY_SIZE);
        body.putShort((short) (normalizedX & 0xFFFF));
        body.putShort((short) (normalizedY & 0xFFFF));
        body.putShort((short) (monitorId & 0xFFFF));
        return body.array();
    }

    /** Mouse button: button(1) down(1). */
    static byte[] encodeMouseButton(int button, boolean isDown) {
        return new byte[] { (byte) button, (byte) (isDown ? 1 : 0) };
    }

    /** Wheel: deltaX(2 signed) deltaY(2 signed), in Windows wheel units (120 per notch). */
    static byte[] encodeWheel(int deltaX, int deltaY) {
        ByteBuffer body = ByteBuffer.allocate(WHEEL_BODY_SIZE);
        body.putShort((short) deltaX);
        body.putShort((short) deltaY);
        return body.array();
    }

    /** Touch: contactId(2) phase(1) x(2) y(2). */
    static byte[] encodeTouch(int contactId, byte phase, int x, int y) {
        ByteBuffer body = ByteBuffer.allocate(TOUCH_BODY_SIZE);
        body.putShort((short) (contactId & 0xFFFF));
        body.put(phase);
        body.putShort((short) (x & 0xFFFF));
        body.putShort((short) (y & 0xFFFF));
        return body.array();
    }

    /**
     * Map a pixel position inside the rendered video to the 0..65535 normalized range.
     *
     * <p>Identical rounding to {@code InputCodec.Normalize} on Windows so a tap on the same
     * remote pixel produces the same wire value on both clients.</p>
     */
    static int normalize(int position, int extent) {
        if (extent <= 1) {
            return 0;
        }

        double ratio = (double) position / (extent - 1);
        if (ratio < 0) {
            ratio = 0;
        } else if (ratio > 1) {
            ratio = 1;
        }
        return (int) Math.round(ratio * 0xFFFF);
    }
}

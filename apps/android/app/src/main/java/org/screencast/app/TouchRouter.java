package org.screencast.app;

import android.content.Context;
import android.view.GestureDetector;
import android.view.MotionEvent;
import android.view.ViewConfiguration;

/**
 * Turns touch gestures into the mouse events the host understands.
 *
 * <table>
 *   <caption>Gesture mapping</caption>
 *   <tr><th>Gesture</th><th>Wire effect</th></tr>
 *   <tr><td>one finger down / drag</td><td>{@code MouseMove} with normalized 0..65535
 *       coordinates — a trackpad, not a dragged selection</td></tr>
 *   <tr><td>one finger tap</td><td>{@code MouseButton} left down + up</td></tr>
 *   <tr><td>two finger tap</td><td>{@code MouseButton} right down + up</td></tr>
 *   <tr><td>two finger drag</td><td>{@code Wheel}, 120 units per notch</td></tr>
 *   <tr><td>double tap</td><td>toggles the control panel (the Windows client uses a double
 *       click for fullscreen, so the same gesture is used here)</td></tr>
 * </table>
 *
 * <p>The tap/double-tap split needs the gesture detector's double-tap window, which is why taps
 * are recognised there rather than on {@code ACTION_UP}: sending a click immediately and then
 * discovering it was a double tap would inject a stray click into the remote desktop.</p>
 *
 * <p>Coordinates are normalized against the rendered video rectangle, not the window, so the
 * letterbox bars around a non-matching aspect ratio never shift the remote cursor. The view is
 * sized to the video aspect by {@code MainActivity}, so view pixels and video pixels coincide.</p>
 */
final class TouchRouter {

    /** Sink for the wire events. */
    interface Sender {
        void mouseMove(int normalizedX, int normalizedY);

        void mouseButton(int button, boolean down);

        void wheel(int deltaX, int deltaY);
    }

    /** Chrome toggle bound to the double tap. */
    interface ChromeToggle {
        void onDoubleTap();
    }

    /** Windows wheel units per notch. */
    private static final int WHEEL_UNIT = 120;

    /** Finger travel that equals one wheel notch, in dp. */
    private static final float WHEEL_NOTCH_DP = 30f;

    /** Two-finger tap window; matches the gesture detector's double-tap timeout. */
    private static final long TAP_TIMEOUT_MS = 300;

    private final Sender sender;
    private final ChromeToggle chrome;
    private final GestureDetector detector;
    private final float touchSlop;
    private final float wheelUnitsPerPixel;

    private int viewWidth = 1;
    private int viewHeight = 1;

    private boolean suppressTap;
    private boolean moved;
    private boolean twoFinger;
    private boolean twoFingerMoved;
    private float downX;
    private float downY;
    private float previousTwoFingerY;
    private float twoFingerTravel;
    private float wheelAccumulator;
    private int lastNormalizedX;
    private int lastNormalizedY;
    private long downTimeMs;

    TouchRouter(Context context, Sender sender, ChromeToggle chrome) {
        this.sender = sender;
        this.chrome = chrome;
        this.touchSlop = ViewConfiguration.get(context).getScaledTouchSlop();

        float density = context.getResources().getDisplayMetrics().density;
        this.wheelUnitsPerPixel = WHEEL_UNIT / Math.max(1f, WHEEL_NOTCH_DP * density);

        this.detector = new GestureDetector(context, new GestureDetector.SimpleOnGestureListener() {
            @Override
            public boolean onDown(MotionEvent event) {
                return true;
            }

            @Override
            public boolean onSingleTapConfirmed(MotionEvent event) {
                if (suppressTap || twoFinger) {
                    return false;
                }

                // Position first: the click must land where the finger is, not where the
                // cursor happened to be.
                sender.mouseMove(normalizeX(event.getX()), normalizeY(event.getY()));
                sender.mouseButton(InputCodec.BUTTON_LEFT, true);
                sender.mouseButton(InputCodec.BUTTON_LEFT, false);
                return true;
            }

            @Override
            public boolean onDoubleTap(MotionEvent event) {
                chrome.onDoubleTap();
                return true;
            }
        });
    }

    /** Feed one touch event; {@code width}/{@code height} are the rendered video rectangle. */
    boolean onTouchEvent(MotionEvent event, int width, int height) {
        viewWidth = Math.max(1, width);
        viewHeight = Math.max(1, height);

        detector.onTouchEvent(event);

        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                suppressTap = false;
                twoFinger = false;
                twoFingerMoved = false;
                moved = false;
                downX = event.getX();
                downY = event.getY();
                downTimeMs = event.getEventTime();
                wheelAccumulator = 0f;
                lastNormalizedX = normalizeX(downX);
                lastNormalizedY = normalizeY(downY);
                sender.mouseMove(lastNormalizedX, lastNormalizedY);
                return true;

            case MotionEvent.ACTION_POINTER_DOWN:
                if (event.getPointerCount() == 2) {
                    twoFinger = true;
                    // A second finger means this is not a tap; the detector must not click.
                    suppressTap = true;
                    twoFingerMoved = false;
                    twoFingerTravel = 0f;
                    wheelAccumulator = 0f;
                    previousTwoFingerY = averageY(event);
                }
                return true;

            case MotionEvent.ACTION_MOVE:
                if (twoFinger && event.getPointerCount() >= 2) {
                    handleTwoFingerMove(event);
                } else if (event.getPointerCount() == 1) {
                    handleSingleFingerMove(event);
                }
                return true;

            case MotionEvent.ACTION_POINTER_UP:
                if (event.getPointerCount() == 2) {
                    // One finger lifted: continue as a drag with the remaining finger, and make
                    // sure the remainder is not mistaken for a tap.
                    twoFinger = false;
                    moved = true;
                    suppressTap = true;
                    wheelAccumulator = 0f;
                }
                return true;

            case MotionEvent.ACTION_UP:
                if (twoFinger && !twoFingerMoved
                        && event.getEventTime() - downTimeMs <= TAP_TIMEOUT_MS) {
                    // Two-finger tap: right click where the fingers were.
                    sender.mouseMove(lastNormalizedX, lastNormalizedY);
                    sender.mouseButton(InputCodec.BUTTON_RIGHT, true);
                    sender.mouseButton(InputCodec.BUTTON_RIGHT, false);
                }
                twoFinger = false;
                return true;

            case MotionEvent.ACTION_CANCEL:
                twoFinger = false;
                suppressTap = true;
                return true;

            default:
                return true;
        }
    }

    private void handleSingleFingerMove(MotionEvent event) {
        float x = event.getX();
        float y = event.getY();

        if (Math.abs(x - downX) > touchSlop || Math.abs(y - downY) > touchSlop) {
            moved = true;
        }

        lastNormalizedX = normalizeX(x);
        lastNormalizedY = normalizeY(y);
        sender.mouseMove(lastNormalizedX, lastNormalizedY);
    }

    private void handleTwoFingerMove(MotionEvent event) {
        float averageY = averageY(event);
        float deltaPixels = previousTwoFingerY - averageY; // fingers up -> positive -> scroll up
        previousTwoFingerY = averageY;

        twoFingerTravel += Math.abs(deltaPixels);
        if (twoFingerTravel > touchSlop) {
            twoFingerMoved = true;
        }

        wheelAccumulator += deltaPixels * wheelUnitsPerPixel;
        flushWheel();
    }

    private void flushWheel() {
        while (wheelAccumulator >= WHEEL_UNIT) {
            sender.wheel(0, WHEEL_UNIT);
            wheelAccumulator -= WHEEL_UNIT;
        }
        while (wheelAccumulator <= -WHEEL_UNIT) {
            sender.wheel(0, -WHEEL_UNIT);
            wheelAccumulator += WHEEL_UNIT;
        }
    }

    private static float averageY(MotionEvent event) {
        int count = Math.max(1, event.getPointerCount());
        float sum = 0f;
        for (int index = 0; index < count; index++) {
            sum += event.getY(index);
        }
        return sum / count;
    }

    private int normalizeX(float x) {
        return InputCodec.normalize(Math.round(x), viewWidth);
    }

    private int normalizeY(float y) {
        return InputCodec.normalize(Math.round(y), viewHeight);
    }
}

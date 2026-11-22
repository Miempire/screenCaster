package org.screencast.app;

/**
 * Control event type identifiers, matching {@code ControlEventType} in
 * {@code native/src/control.rs} and {@code src/ScreenCast.Core/Transport/ControlEventType.cs}.
 *
 * <p>The viewer only sends the first ten; the numbers are wire values and must not change.</p>
 */
final class ControlEventType {
    static final int KEY = 1;
    static final int MOUSE_MOVE = 2;
    static final int MOUSE_BUTTON = 3;
    static final int WHEEL = 4;
    static final int TOUCH = 5;
    static final int RELEASE_ALL = 6;
    static final int REQUEST_KEY_FRAME = 7;
    static final int SET_QUALITY = 8;
    static final int PATH_SWITCH = 9;
    static final int CONTROL_LEASE = 10;

    private ControlEventType() {
    }
}

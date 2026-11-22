package org.screencast.app;

import android.view.KeyEvent;

/**
 * Maps an Android {@link KeyEvent} to a PC (set 1) hardware scan code.
 *
 * <p><b>Why this table exists at all.</b> {@link KeyEvent#getScanCode()} is <em>not</em> a PC
 * scan code: on Android it is the Linux input-layer code of the key (HID usage offset by 8),
 * so forwarding it would inject a different key on the host. The wire format carries the code
 * that {@code SendInput} injects on Windows, therefore every entry below is an explicit,
 * hand-checked mapping from an Android key code to the PC set-1 code of the same physical key.</p>
 *
 * <p><b>What is not mapped.</b> Anything absent from the tables returns
 * {@link #UNSUPPORTED}; the caller drops it and tells the user once, rather than inventing a
 * scan code. That covers the D-pad centre, media keys, volume keys, keyboard layout
 * meta-keys, and — unless {@link #ALLOW_LETTER_AND_DIGIT_KEYS} is turned off — the letter and
 * digit keys, which do have an authoritative mapping (see the alphanumeric table).</p>
 *
 * <p><b>Extended flag.</b> It cannot be derived from the scan code: arrows, Insert/Delete,
 * Home/End/PgUp/PgDn, right Ctrl/Alt, the Windows keys and numpad Enter share their scan code
 * with a numpad twin, and only the extended bit tells the host which one was pressed.</p>
 */
final class KeyMapper {

    /** Returned when the key has no PC scan code in this table. */
    static final int UNSUPPORTED = -1;

    /**
     * Letters and digits are mappable through the fixed PC set-1 table (the same table every
     * remote-desktop client uses), but they are not required for touch control. Set this to
     * {@code false} to follow the conservative rule and drop them instead.
     */
    static final boolean ALLOW_LETTER_AND_DIGIT_KEYS = true;

    private static final int EXTENDED = 0x100;

    private KeyMapper() {
    }

    /** The scan code of a packed mapping. */
    static int scanCode(int packed) {
        return packed & 0xFF;
    }

    /** Whether a packed mapping needs the extended-key flag. */
    static boolean extended(int packed) {
        return (packed & EXTENDED) != 0;
    }

    private static int extended(int scanCode, boolean isExtended) {
        return isExtended ? scanCode | EXTENDED : scanCode;
    }

    /**
     * Map one Android key code.
     *
     * @return a packed {@code scanCode | extendedBit}, or {@link #UNSUPPORTED}
     */
    static int map(int keyCode) {
        switch (keyCode) {
            /* ---- Navigation and editing, all extended. ---- */
            case KeyEvent.KEYCODE_DPAD_UP:
                return extended(0x48, true);
            case KeyEvent.KEYCODE_DPAD_DOWN:
                return extended(0x50, true);
            case KeyEvent.KEYCODE_DPAD_LEFT:
                return extended(0x4B, true);
            case KeyEvent.KEYCODE_DPAD_RIGHT:
                return extended(0x4D, true);
            case KeyEvent.KEYCODE_MOVE_HOME:
                return extended(0x47, true);
            case KeyEvent.KEYCODE_MOVE_END:
                return extended(0x4F, true);
            case KeyEvent.KEYCODE_PAGE_UP:
                return extended(0x49, true);
            case KeyEvent.KEYCODE_PAGE_DOWN:
                return extended(0x51, true);
            case KeyEvent.KEYCODE_INSERT:
                return extended(0x52, true);
            case KeyEvent.KEYCODE_FORWARD_DEL:
                return extended(0x53, true);

            /* ---- Editing basics, not extended. ---- */
            case KeyEvent.KEYCODE_ESCAPE:
                return extended(0x01, false);
            case KeyEvent.KEYCODE_ENTER:
                return extended(0x1C, false);
            case KeyEvent.KEYCODE_NUMPAD_ENTER:
                return extended(0x1C, true);
            case KeyEvent.KEYCODE_DEL:
                return extended(0x0E, false);
            case KeyEvent.KEYCODE_TAB:
                return extended(0x0F, false);
            case KeyEvent.KEYCODE_SPACE:
                return extended(0x39, false);

            /* ---- Modifiers. ---- */
            case KeyEvent.KEYCODE_CTRL_LEFT:
                return extended(0x1D, false);
            case KeyEvent.KEYCODE_CTRL_RIGHT:
                return extended(0x1D, true);
            case KeyEvent.KEYCODE_ALT_LEFT:
                return extended(0x38, false);
            case KeyEvent.KEYCODE_ALT_RIGHT:
                return extended(0x38, true);
            case KeyEvent.KEYCODE_SHIFT_LEFT:
                return extended(0x2A, false);
            case KeyEvent.KEYCODE_SHIFT_RIGHT:
                return extended(0x36, false);
            case KeyEvent.KEYCODE_META_LEFT:
                return extended(0x5B, true);
            case KeyEvent.KEYCODE_META_RIGHT:
                return extended(0x5C, true);

            /* ---- Locks, menu, print screen. ---- */
            case KeyEvent.KEYCODE_CAPS_LOCK:
                return extended(0x3A, false);
            case KeyEvent.KEYCODE_NUM_LOCK:
                return extended(0x45, true);
            case KeyEvent.KEYCODE_SCROLL_LOCK:
                return extended(0x46, false);
            case KeyEvent.KEYCODE_SYSRQ:
                return extended(0x37, true);
            case KeyEvent.KEYCODE_MENU:
                return extended(0x5D, true);

            /* ---- Function keys. ---- */
            case KeyEvent.KEYCODE_F1:
                return extended(0x3B, false);
            case KeyEvent.KEYCODE_F2:
                return extended(0x3C, false);
            case KeyEvent.KEYCODE_F3:
                return extended(0x3D, false);
            case KeyEvent.KEYCODE_F4:
                return extended(0x3E, false);
            case KeyEvent.KEYCODE_F5:
                return extended(0x3F, false);
            case KeyEvent.KEYCODE_F6:
                return extended(0x40, false);
            case KeyEvent.KEYCODE_F7:
                return extended(0x41, false);
            case KeyEvent.KEYCODE_F8:
                return extended(0x42, false);
            case KeyEvent.KEYCODE_F9:
                return extended(0x43, false);
            case KeyEvent.KEYCODE_F10:
                return extended(0x44, false);
            case KeyEvent.KEYCODE_F11:
                return extended(0x57, false);
            case KeyEvent.KEYCODE_F12:
                return extended(0x58, false);

            /* ---- Punctuation. ---- */
            case KeyEvent.KEYCODE_GRAVE:
                return extended(0x29, false);
            case KeyEvent.KEYCODE_MINUS:
                return extended(0x0C, false);
            case KeyEvent.KEYCODE_EQUALS:
                return extended(0x0D, false);
            case KeyEvent.KEYCODE_LEFT_BRACKET:
                return extended(0x1A, false);
            case KeyEvent.KEYCODE_RIGHT_BRACKET:
                return extended(0x1B, false);
            case KeyEvent.KEYCODE_BACKSLASH:
                return extended(0x2B, false);
            case KeyEvent.KEYCODE_SEMICOLON:
                return extended(0x27, false);
            case KeyEvent.KEYCODE_APOSTROPHE:
                return extended(0x28, false);
            case KeyEvent.KEYCODE_COMMA:
                return extended(0x33, false);
            case KeyEvent.KEYCODE_PERIOD:
                return extended(0x34, false);
            case KeyEvent.KEYCODE_SLASH:
                return extended(0x35, false);

            /* ---- Numpad. ---- */
            case KeyEvent.KEYCODE_NUMPAD_0:
                return extended(0x52, false);
            case KeyEvent.KEYCODE_NUMPAD_1:
                return extended(0x4F, false);
            case KeyEvent.KEYCODE_NUMPAD_2:
                return extended(0x50, false);
            case KeyEvent.KEYCODE_NUMPAD_3:
                return extended(0x51, false);
            case KeyEvent.KEYCODE_NUMPAD_4:
                return extended(0x4B, false);
            case KeyEvent.KEYCODE_NUMPAD_5:
                return extended(0x4C, false);
            case KeyEvent.KEYCODE_NUMPAD_6:
                return extended(0x4D, false);
            case KeyEvent.KEYCODE_NUMPAD_7:
                return extended(0x47, false);
            case KeyEvent.KEYCODE_NUMPAD_8:
                return extended(0x48, false);
            case KeyEvent.KEYCODE_NUMPAD_9:
                return extended(0x49, false);
            case KeyEvent.KEYCODE_NUMPAD_DOT:
                return extended(0x53, false);
            case KeyEvent.KEYCODE_NUMPAD_ADD:
                return extended(0x4E, false);
            case KeyEvent.KEYCODE_NUMPAD_SUBTRACT:
                return extended(0x4A, false);
            case KeyEvent.KEYCODE_NUMPAD_MULTIPLY:
                return extended(0x37, false);
            case KeyEvent.KEYCODE_NUMPAD_DIVIDE:
                return extended(0x35, true);

            default:
                return mapAlphanumeric(keyCode);
        }
    }

    /**
     * Letters and digits.
     *
     * <p>Android key codes {@code KEYCODE_A..KEYCODE_Z} are 29..54 in alphabetical order and
     * {@code KEYCODE_0..KEYCODE_9} are 7..16; the PC set-1 codes for the same physical keys
     * follow the QWERTY rows. Both sides are fixed tables, so this is a translation, not a
     * guess — but it is kept separate so the conservative policy can be selected in one place.</p>
     */
    private static int mapAlphanumeric(int keyCode) {
        if (!ALLOW_LETTER_AND_DIGIT_KEYS) {
            return UNSUPPORTED;
        }

        switch (keyCode) {
            case KeyEvent.KEYCODE_0:
                return extended(0x0B, false);
            case KeyEvent.KEYCODE_1:
                return extended(0x02, false);
            case KeyEvent.KEYCODE_2:
                return extended(0x03, false);
            case KeyEvent.KEYCODE_3:
                return extended(0x04, false);
            case KeyEvent.KEYCODE_4:
                return extended(0x05, false);
            case KeyEvent.KEYCODE_5:
                return extended(0x06, false);
            case KeyEvent.KEYCODE_6:
                return extended(0x07, false);
            case KeyEvent.KEYCODE_7:
                return extended(0x08, false);
            case KeyEvent.KEYCODE_8:
                return extended(0x09, false);
            case KeyEvent.KEYCODE_9:
                return extended(0x0A, false);

            case KeyEvent.KEYCODE_Q:
                return extended(0x10, false);
            case KeyEvent.KEYCODE_W:
                return extended(0x11, false);
            case KeyEvent.KEYCODE_E:
                return extended(0x12, false);
            case KeyEvent.KEYCODE_R:
                return extended(0x13, false);
            case KeyEvent.KEYCODE_T:
                return extended(0x14, false);
            case KeyEvent.KEYCODE_Y:
                return extended(0x15, false);
            case KeyEvent.KEYCODE_U:
                return extended(0x16, false);
            case KeyEvent.KEYCODE_I:
                return extended(0x17, false);
            case KeyEvent.KEYCODE_O:
                return extended(0x18, false);
            case KeyEvent.KEYCODE_P:
                return extended(0x19, false);
            case KeyEvent.KEYCODE_A:
                return extended(0x1E, false);
            case KeyEvent.KEYCODE_S:
                return extended(0x1F, false);
            case KeyEvent.KEYCODE_D:
                return extended(0x20, false);
            case KeyEvent.KEYCODE_F:
                return extended(0x21, false);
            case KeyEvent.KEYCODE_G:
                return extended(0x22, false);
            case KeyEvent.KEYCODE_H:
                return extended(0x23, false);
            case KeyEvent.KEYCODE_J:
                return extended(0x24, false);
            case KeyEvent.KEYCODE_K:
                return extended(0x25, false);
            case KeyEvent.KEYCODE_L:
                return extended(0x26, false);
            case KeyEvent.KEYCODE_Z:
                return extended(0x2C, false);
            case KeyEvent.KEYCODE_X:
                return extended(0x2D, false);
            case KeyEvent.KEYCODE_C:
                return extended(0x2E, false);
            case KeyEvent.KEYCODE_V:
                return extended(0x2F, false);
            case KeyEvent.KEYCODE_B:
                return extended(0x30, false);
            case KeyEvent.KEYCODE_N:
                return extended(0x31, false);
            case KeyEvent.KEYCODE_M:
                return extended(0x32, false);

            default:
                return UNSUPPORTED;
        }
    }
}

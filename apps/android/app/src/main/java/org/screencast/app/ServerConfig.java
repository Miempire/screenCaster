package org.screencast.app;

import android.content.Context;
import android.content.SharedPreferences;

import java.security.SecureRandom;
import java.util.Locale;

final class ServerConfig {

    static final String HOST = "jsspace.xyz";
    static final int PORT = 3479;

    private static final String STORE = "screencast.signaling";
    private static final String KEY_DEVICE_ID = "device_id";
    private static final int DEVICE_ID_BYTES = 8;

    private ServerConfig() {
    }

    static String deviceId(Context context) {
        SharedPreferences preferences = context.getSharedPreferences(STORE, Context.MODE_PRIVATE);
        String existing = preferences.getString(KEY_DEVICE_ID, "");
        if (isValid(existing)) {
            return existing;
        }

        byte[] bytes = new byte[DEVICE_ID_BYTES];
        new SecureRandom().nextBytes(bytes);
        StringBuilder text = new StringBuilder(DEVICE_ID_BYTES * 2);
        for (byte value : bytes) {
            text.append(String.format(Locale.US, "%02x", value & 0xFF));
        }
        String created = text.toString();
        preferences.edit().putString(KEY_DEVICE_ID, created).apply();
        return created;
    }

    private static boolean isValid(String value) {
        if (value == null || value.length() != DEVICE_ID_BYTES * 2) {
            return false;
        }
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            if (character < '0' || character > '9') {
                if (character < 'a' || character > 'f') {
                    return false;
                }
            }
        }
        return true;
    }
}

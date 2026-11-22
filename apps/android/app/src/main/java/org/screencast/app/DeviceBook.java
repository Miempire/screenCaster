package org.screencast.app;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * Local device book.
 *
 * <p>The 9-digit connection code is assigned on this device the first time a direct address
 * connects successfully. It is not a server-issued code: there is no signalling service in this
 * build, so the code is a local shortcut for an address the user has already used.</p>
 */
final class DeviceBook {

    static final class Device {
        final String code;
        final String name;
        final String host;
        final int videoPort;
        final int controlPort;
        final long lastUsedMs;

        Device(String code, String name, String host, int videoPort, int controlPort,
                long lastUsedMs) {
            this.code = code;
            this.name = name;
            this.host = host;
            this.videoPort = videoPort;
            this.controlPort = controlPort;
            this.lastUsedMs = lastUsedMs;
        }

        String addressText() {
            return host + ":" + videoPort + " / " + controlPort;
        }
    }

    private static final String STORE = "screencast.devices";
    private static final String KEY_DEVICES = "devices";

    private final SharedPreferences preferences;
    private final SecureRandom random = new SecureRandom();

    DeviceBook(Context context) {
        preferences = context.getSharedPreferences(STORE, Context.MODE_PRIVATE);
    }

    List<Device> all() {
        List<Device> devices = new ArrayList<>();
        String raw = preferences.getString(KEY_DEVICES, "[]");
        try {
            JSONArray array = new JSONArray(raw);
            for (int index = 0; index < array.length(); index++) {
                JSONObject object = array.getJSONObject(index);
                devices.add(new Device(
                        object.optString("code"),
                        object.optString("name"),
                        object.optString("host"),
                        object.optInt("video"),
                        object.optInt("control"),
                        object.optLong("last")));
            }
        } catch (JSONException ignored) {
            // A corrupt store is treated as empty rather than blocking the interface.
        }
        Collections.sort(devices, (left, right) -> Long.compare(right.lastUsedMs, left.lastUsedMs));
        return devices;
    }

    Device findByCode(String code) {
        for (Device device : all()) {
            if (device.code.equals(code)) {
                return device;
            }
        }
        return null;
    }

    /**
     * Saves a connected endpoint. An empty code produces a fresh one, so a direct address
     * connection automatically becomes a device card.
     *
     * @return the stored device, including the assigned code
     */
    Device save(String name, String host, int videoPort, int controlPort, String existingCode) {
        String code = existingCode;
        if (code == null || code.length() != 9) {
            code = freshCode();
        } else if (findByCode(code) != null) {
            code = freshCode();
        }

        String deviceName = name == null || name.trim().isEmpty() ? host : name.trim();
        Device device = new Device(code, deviceName, host, videoPort, controlPort,
                System.currentTimeMillis());

        List<Device> devices = new ArrayList<>();
        for (Device entry : all()) {
            if (!entry.code.equals(code)) {
                devices.add(entry);
            }
        }
        devices.add(0, device);
        write(devices);
        return device;
    }

    Device touch(Device device) {
        Device updated = new Device(device.code, device.name, device.host, device.videoPort,
                device.controlPort, System.currentTimeMillis());
        List<Device> devices = new ArrayList<>();
        for (Device entry : all()) {
            if (!entry.code.equals(device.code)) {
                devices.add(entry);
            }
        }
        devices.add(0, updated);
        write(devices);
        return updated;
    }

    void remove(Device device) {
        List<Device> devices = new ArrayList<>();
        for (Device entry : all()) {
            if (!entry.code.equals(device.code)) {
                devices.add(entry);
            }
        }
        write(devices);
    }

    private void write(List<Device> devices) {
        JSONArray array = new JSONArray();
        for (Device device : devices) {
            JSONObject object = new JSONObject();
            try {
                object.put("code", device.code);
                object.put("name", device.name);
                object.put("host", device.host);
                object.put("video", device.videoPort);
                object.put("control", device.controlPort);
                object.put("last", device.lastUsedMs);
                array.put(object);
            } catch (JSONException ignored) {
                // Only string and number values are written, so this cannot fail in practice.
            }
        }
        preferences.edit().putString(KEY_DEVICES, array.toString()).apply();
    }

    private String freshCode() {
        while (true) {
            StringBuilder code = new StringBuilder(9);
            for (int index = 0; index < 9; index++) {
                code.append((char) ('0' + random.nextInt(10)));
            }
            String candidate = code.toString();
            if (!candidate.startsWith("0") && findByCode(candidate) == null) {
                return candidate;
            }
        }
    }
}

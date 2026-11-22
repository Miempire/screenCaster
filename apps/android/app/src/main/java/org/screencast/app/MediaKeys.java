package org.screencast.app;

import java.nio.charset.StandardCharsets;
import java.security.InvalidKeyException;
import java.security.NoSuchAlgorithmException;
import java.util.Arrays;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

final class MediaKeys {

    private static final byte[] CODE_PREFIX = "ScreenCast/code/v1".getBytes(StandardCharsets.UTF_8);
    private static final byte[] MEDIA_INFO = "ScreenCast/media-key/v1".getBytes(StandardCharsets.UTF_8);
    private static final int SECRET_BYTES = 32;

    private MediaKeys() {
    }

    static byte[] deriveMediaSecret(String code, byte[] sessionId) {
        if (code == null || code.isEmpty()) {
            throw new IllegalArgumentException("code is required");
        }
        if (sessionId == null || sessionId.length != 16) {
            throw new IllegalArgumentException("sessionId must be 16 bytes");
        }

        byte[] codeBytes = code.getBytes(StandardCharsets.UTF_8);
        byte[] ikm = new byte[CODE_PREFIX.length + codeBytes.length];
        System.arraycopy(CODE_PREFIX, 0, ikm, 0, CODE_PREFIX.length);
        System.arraycopy(codeBytes, 0, ikm, CODE_PREFIX.length, codeBytes.length);
        try {
            return hkdfSha256(ikm, sessionId, MEDIA_INFO, SECRET_BYTES);
        } finally {
            Arrays.fill(ikm, (byte) 0);
        }
    }

    private static byte[] hkdfSha256(byte[] ikm, byte[] salt, byte[] info, int length) {
        try {
            Mac mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(salt, "HmacSHA256"));
            byte[] prk = mac.doFinal(ikm);
            try {
                mac.init(new SecretKeySpec(prk, "HmacSHA256"));
                byte[] output = new byte[length];
                byte[] block = new byte[0];
                int offset = 0;
                int counter = 1;
                while (offset < length) {
                    mac.update(block);
                    mac.update(info);
                    mac.update((byte) counter);
                    block = mac.doFinal();
                    int copied = Math.min(block.length, length - offset);
                    System.arraycopy(block, 0, output, offset, copied);
                    offset += copied;
                    counter++;
                }
                return output;
            } finally {
                Arrays.fill(prk, (byte) 0);
            }
        } catch (NoSuchAlgorithmException | InvalidKeyException error) {
            throw new IllegalStateException(error);
        }
    }
}

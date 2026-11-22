package org.screencast.app;

import java.io.ByteArrayOutputStream;
import java.net.Inet4Address;
import java.net.Inet6Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.UnknownHostException;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.List;

final class SignalingWire {

    static final int MAGIC = 0x53435347;
    static final int VERSION = 1;
    static final int HEADER_SIZE = 24;
    static final int MAX_MESSAGE_BYTES = 8192;
    static final int MAX_BODY_BYTES = MAX_MESSAGE_BYTES - HEADER_SIZE;
    static final int MAX_TEXT_BYTES = 64;
    static final int MAX_BLOB_BYTES = 4096;
    static final int MAX_CANDIDATES = 8;
    static final int ID_BYTES = 16;

    static final int REGISTER = 1;
    static final int HEARTBEAT = 2;
    static final int CREATE_SESSION = 3;
    static final int JOIN_SESSION = 4;
    static final int ACCEPT_SESSION = 5;
    static final int REJECT_SESSION = 6;
    static final int PUNCH_START = 7;
    static final int CLOSE_SESSION = 8;
    static final int PEER_KEY_OFFER = 9;
    static final int PEER_KEY_ANSWER = 10;
    static final int PATH_COMMIT = 11;

    static final int REGISTERED = 128;
    static final int HEARTBEAT_ACK = 129;
    static final int SESSION_CREATED = 130;
    static final int SESSION_JOINED = 131;
    static final int SESSION_ACCEPTED = 132;
    static final int SESSION_REJECTED = 133;
    static final int SESSION_CLOSED = 134;
    static final int ACK = 135;

    static final int ERROR = 136;
    static final int JOIN_REQUESTED = 137;
    static final int PUNCH_START_NOTIFY = 138;
    static final int SESSION_INVITED = 139;
    static final int SESSION_ACTIVE = 140;

    static final int ERROR_MALFORMED = 1;
    static final int ERROR_UNSUPPORTED = 2;
    static final int ERROR_UNREGISTERED = 3;
    static final int ERROR_NOT_FOUND = 4;
    static final int ERROR_LIMIT_EXCEEDED = 5;
    static final int ERROR_INVALID_STATE = 6;
    static final int ERROR_NOT_PARTICIPANT = 7;
    static final int ERROR_ROLE_MISMATCH = 8;
    static final int ERROR_GENERATION_MISMATCH = 9;
    static final int ERROR_INVITE_INVALID = 10;
    static final int ERROR_BUSY = 11;

    static final int ROLE_HOST = 1;
    static final int ROLE_VIEWER = 2;

    static final int STATE_CREATED = 0;
    static final int STATE_ACCEPTED = 1;
    static final int STATE_NEGOTIATING = 2;
    static final int STATE_ACTIVE = 3;
    static final int STATE_CLOSED = 4;

    private static final SecureRandom RANDOM = new SecureRandom();

    private SignalingWire() {
    }

    static byte[] newId() {
        byte[] id = new byte[ID_BYTES];
        RANDOM.nextBytes(id);
        return id;
    }

    static byte[] zeroId() {
        return new byte[ID_BYTES];
    }

    static boolean isZeroId(byte[] id) {
        for (byte value : id) {
            if (value != 0) {
                return false;
            }
        }
        return true;
    }

    static String idHex(byte[] id) {
        StringBuilder text = new StringBuilder(id.length * 2);
        for (byte value : id) {
            text.append(Character.forDigit((value >> 4) & 0xF, 16));
            text.append(Character.forDigit(value & 0xF, 16));
        }
        return text.toString();
    }

    static byte[] encode(int type, byte[] requestId, byte[] body) {
        if (requestId == null || requestId.length != ID_BYTES) {
            throw new IllegalArgumentException("requestId must be 16 bytes");
        }
        if (body.length > MAX_BODY_BYTES) {
            throw new IllegalArgumentException("body exceeds the message limit");
        }
        byte[] message = new byte[HEADER_SIZE + body.length];
        message[0] = (byte) (MAGIC >>> 24);
        message[1] = (byte) (MAGIC >>> 16);
        message[2] = (byte) (MAGIC >>> 8);
        message[3] = (byte) MAGIC;
        message[4] = (byte) VERSION;
        message[5] = (byte) type;
        message[6] = (byte) (body.length >>> 8);
        message[7] = (byte) body.length;
        System.arraycopy(requestId, 0, message, 8, ID_BYTES);
        System.arraycopy(body, 0, message, HEADER_SIZE, body.length);
        return message;
    }

    static Header parseHeader(byte[] datagram) {
        if (datagram == null || datagram.length < HEADER_SIZE || datagram.length > MAX_MESSAGE_BYTES) {
            return null;
        }
        long magic = ((long) (datagram[0] & 0xFF) << 24)
                | ((long) (datagram[1] & 0xFF) << 16)
                | ((long) (datagram[2] & 0xFF) << 8)
                | (datagram[3] & 0xFF);
        if (magic != MAGIC || (datagram[4] & 0xFF) != VERSION) {
            return null;
        }
        int bodyLength = ((datagram[6] & 0xFF) << 8) | (datagram[7] & 0xFF);
        if (datagram.length != HEADER_SIZE + bodyLength) {
            return null;
        }
        byte[] requestId = new byte[ID_BYTES];
        System.arraycopy(datagram, 8, requestId, 0, ID_BYTES);
        return new Header(datagram[5] & 0xFF, requestId, bodyLength);
    }

    static final class Header {
        final int type;
        final byte[] requestId;
        final int bodyLength;

        Header(int type, byte[] requestId, int bodyLength) {
            this.type = type;
            this.requestId = requestId;
            this.bodyLength = bodyLength;
        }
    }

    static final class Reader {
        private final byte[] buffer;
        private final int end;
        private int offset;

        Reader(byte[] datagram, Header header) {
            if (header.bodyLength < 0 || header.bodyLength > MAX_BODY_BYTES) {
                throw new IllegalArgumentException("invalid body length");
            }
            if (datagram.length < HEADER_SIZE + header.bodyLength) {
                throw new IllegalArgumentException("datagram is shorter than its body");
            }
            this.buffer = datagram;
            this.offset = HEADER_SIZE;
            this.end = HEADER_SIZE + header.bodyLength;
        }

        int u8() {
            require(1);
            return buffer[offset++] & 0xFF;
        }

        int u16() {
            require(2);
            int value = ((buffer[offset] & 0xFF) << 8) | (buffer[offset + 1] & 0xFF);
            offset += 2;
            return value;
        }

        long u32() {
            require(4);
            long value = ((long) (buffer[offset] & 0xFF) << 24)
                    | ((long) (buffer[offset + 1] & 0xFF) << 16)
                    | ((long) (buffer[offset + 2] & 0xFF) << 8)
                    | (buffer[offset + 3] & 0xFF);
            offset += 4;
            return value;
        }

        byte[] id() {
            return raw(ID_BYTES);
        }

        String text(int maxBytes) {
            int length = u16();
            if (length > maxBytes) {
                throw new IllegalArgumentException("text field exceeds its limit");
            }
            return decode(raw(length));
        }

        byte[] blob(int maxBytes) {
            long length = u32();
            if (length < 0 || length > maxBytes) {
                throw new IllegalArgumentException("blob field exceeds its limit");
            }
            return raw((int) length);
        }

        InetSocketAddress endpoint() {
            int family = u8();
            switch (family) {
                case 0:
                    return null;
                case 4:
                    return new InetSocketAddress(fromBytes(raw(4)), u16());
                case 6:
                    return new InetSocketAddress(normalize(fromBytes(raw(16))), u16());
                default:
                    throw new IllegalArgumentException("unknown endpoint family");
            }
        }

        List<InetSocketAddress> endpoints(int maxCount) {
            int count = u16();
            if (count > maxCount) {
                throw new IllegalArgumentException("endpoint list exceeds its limit");
            }
            List<InetSocketAddress> endpoints = new ArrayList<>(count);
            for (int index = 0; index < count; index++) {
                InetSocketAddress endpoint = endpoint();
                if (endpoint != null) {
                    endpoints.add(endpoint);
                }
            }
            return endpoints;
        }

        int remaining() {
            return end - offset;
        }

        void requireEnd() {
            if (offset != end) {
                throw new IllegalArgumentException("trailing bytes after the last field");
            }
        }

        private byte[] raw(int count) {
            require(count);
            byte[] value = new byte[count];
            System.arraycopy(buffer, offset, value, 0, count);
            offset += count;
            return value;
        }

        private void require(int count) {
            if (count < 0 || end - offset < count) {
                throw new IllegalArgumentException("truncated body");
            }
        }
    }

    static final class Writer {
        private final ByteArrayOutputStream output = new ByteArrayOutputStream();

        void u8(int value) {
            output.write(value & 0xFF);
        }

        void u16(int value) {
            output.write((value >>> 8) & 0xFF);
            output.write(value & 0xFF);
        }

        void u32(long value) {
            output.write((int) (value >>> 24) & 0xFF);
            output.write((int) (value >>> 16) & 0xFF);
            output.write((int) (value >>> 8) & 0xFF);
            output.write((int) value & 0xFF);
        }

        void id(byte[] value) {
            if (value == null || value.length != ID_BYTES) {
                throw new IllegalArgumentException("id must be 16 bytes");
            }
            output.write(value, 0, ID_BYTES);
        }

        void text(String value) {
            byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
            if (bytes.length > MAX_TEXT_BYTES) {
                throw new IllegalArgumentException("text field exceeds its limit");
            }
            u16(bytes.length);
            output.write(bytes, 0, bytes.length);
        }

        void blob(byte[] value) {
            byte[] bytes = value == null ? new byte[0] : value;
            if (bytes.length > MAX_BLOB_BYTES) {
                throw new IllegalArgumentException("blob field exceeds its limit");
            }
            u32(bytes.length);
            output.write(bytes, 0, bytes.length);
        }

        void endpoint(InetSocketAddress value) {
            if (value == null) {
                u8(0);
                return;
            }
            InetAddress address = normalize(value.getAddress());
            if (address instanceof Inet4Address) {
                u8(4);
                output.write(address.getAddress(), 0, 4);
            } else if (address instanceof Inet6Address) {
                u8(6);
                output.write(address.getAddress(), 0, 16);
            } else {
                throw new IllegalArgumentException("unsupported endpoint family");
            }
            u16(value.getPort());
        }

        void endpoints(List<InetSocketAddress> values) {
            if (values.size() > MAX_CANDIDATES) {
                throw new IllegalArgumentException("endpoint list exceeds its limit");
            }
            u16(values.size());
            for (InetSocketAddress value : values) {
                endpoint(value);
            }
        }

        byte[] body() {
            return output.toByteArray();
        }
    }

    private static String decode(byte[] utf8) {
        try {
            return StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(utf8))
                    .toString();
        } catch (CharacterCodingException error) {
            throw new IllegalArgumentException("text field is not valid UTF-8", error);
        }
    }

    private static InetAddress normalize(InetAddress address) {
        if (address instanceof Inet6Address) {
            byte[] bytes = address.getAddress();
            boolean mapped = true;
            for (int index = 0; index < 10; index++) {
                if (bytes[index] != 0) {
                    mapped = false;
                    break;
                }
            }
            if (mapped && (bytes[10] & 0xFF) == 0xFF && (bytes[11] & 0xFF) == 0xFF) {
                return fromBytes(new byte[] { bytes[12], bytes[13], bytes[14], bytes[15] });
            }
        }
        return address;
    }

    private static InetAddress fromBytes(byte[] bytes) {
        try {
            return InetAddress.getByAddress(bytes);
        } catch (UnknownHostException error) {
            throw new IllegalArgumentException(error);
        }
    }
}

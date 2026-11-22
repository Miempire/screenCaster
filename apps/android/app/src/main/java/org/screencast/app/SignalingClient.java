package org.screencast.app;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

final class SignalingClient {

    static final int ROLE_HOST = SignalingWire.ROLE_HOST;
    static final int ROLE_VIEWER = SignalingWire.ROLE_VIEWER;

    private static final int MAX_DEVICE_ID_BYTES = 64;
    private static final int MAX_REQUEST_ATTEMPTS = 5;
    private static final long RETRY_INTERVAL_MS = 500;
    private static final int DEFAULT_HEARTBEAT_MS = 15_000;

    interface Listener {
        default void onRegistered(int heartbeatIntervalMs) {
        }

        default void onSessionClosed(SessionInfo view) {
        }

        default void onPeerKey(PeerKeyMessage message) {
        }

        default void onFailed(SignalingException error) {
        }

        default void onFaulted(Throwable error) {
        }
    }

    static final class SignalingException extends Exception {
        final int code;

        SignalingException(int code, String detail) {
            super(detail);
            this.code = code;
        }
    }

    static final class SessionInfo {
        final int type;
        final byte[] sessionId;
        final int state;
        final long generation;
        final int role;
        final String inviteCode;
        final String hostId;
        final String viewerId;
        final InetSocketAddress hostEndpoint;
        final InetSocketAddress viewerEndpoint;
        final int hostNat;
        final int viewerNat;
        final List<InetSocketAddress> hostCandidates;
        final List<InetSocketAddress> viewerCandidates;
        final long punchStartDelayMs;
        final int closeReason;

        private SessionInfo(int type, byte[] sessionId, int state, long generation, int role,
                String inviteCode, String hostId, String viewerId, InetSocketAddress hostEndpoint,
                InetSocketAddress viewerEndpoint, int hostNat, int viewerNat,
                List<InetSocketAddress> hostCandidates, List<InetSocketAddress> viewerCandidates,
                long punchStartDelayMs, int closeReason) {
            this.type = type;
            this.sessionId = sessionId;
            this.state = state;
            this.generation = generation;
            this.role = role;
            this.inviteCode = inviteCode;
            this.hostId = hostId;
            this.viewerId = viewerId;
            this.hostEndpoint = hostEndpoint;
            this.viewerEndpoint = viewerEndpoint;
            this.hostNat = hostNat;
            this.viewerNat = viewerNat;
            this.hostCandidates = hostCandidates;
            this.viewerCandidates = viewerCandidates;
            this.punchStartDelayMs = punchStartDelayMs;
            this.closeReason = closeReason;
        }

        static SessionInfo decode(byte[] datagram, SignalingWire.Header header) {
            SignalingWire.Reader reader = new SignalingWire.Reader(datagram, header);
            byte[] sessionId = reader.id();
            int state = reader.u8();
            long generation = reader.u32();
            int role = reader.u8();
            String inviteCode = reader.text(16);
            String hostId = reader.text(MAX_DEVICE_ID_BYTES);
            String viewerId = reader.text(MAX_DEVICE_ID_BYTES);
            InetSocketAddress hostEndpoint = reader.endpoint();
            InetSocketAddress viewerEndpoint = reader.endpoint();
            int hostNat = reader.u8();
            int viewerNat = reader.u8();
            List<InetSocketAddress> hostCandidates =
                    reader.endpoints(SignalingWire.MAX_CANDIDATES);
            List<InetSocketAddress> viewerCandidates =
                    reader.endpoints(SignalingWire.MAX_CANDIDATES);
            long punchStartDelayMs = reader.u32();
            int closeReason = reader.remaining() > 0 ? reader.u8() : -1;
            reader.requireEnd();
            return new SessionInfo(header.type, sessionId, state, generation, role, inviteCode,
                    hostId, viewerId, hostEndpoint, viewerEndpoint, hostNat, viewerNat,
                    hostCandidates, viewerCandidates, punchStartDelayMs, closeReason);
        }
    }

    static final class PeerKeyMessage {
        final int type;
        final byte[] sessionId;
        final byte[] originRequestId;
        final byte[] payload;

        PeerKeyMessage(int type, byte[] sessionId, byte[] originRequestId, byte[] payload) {
            this.type = type;
            this.sessionId = sessionId;
            this.originRequestId = originRequestId;
            this.payload = payload;
        }

        static PeerKeyMessage decode(byte[] datagram, SignalingWire.Header header) {
            SignalingWire.Reader reader = new SignalingWire.Reader(datagram, header);
            byte[] sessionId = reader.id();
            byte[] originRequestId = reader.id();
            byte[] payload = reader.blob(SignalingWire.MAX_BLOB_BYTES);
            reader.requireEnd();
            return new PeerKeyMessage(header.type, sessionId, originRequestId, payload);
        }
    }

    private final String host;
    private final int port;
    private final String deviceId;
    private final int roles;
    private final Listener listener;
    private final Map<String, ArrayBlockingQueue<Object>> pending = new ConcurrentHashMap<>();

    private volatile DatagramSocket socket;
    private volatile boolean closed;
    private volatile int heartbeatIntervalMs = DEFAULT_HEARTBEAT_MS;
    private Thread receiveThread;
    private Thread heartbeatThread;

    SignalingClient(String host, int port, String deviceId, int roles, Listener listener) {
        if (host == null || host.trim().isEmpty()) {
            throw new IllegalArgumentException("host is required");
        }
        if (deviceId == null || deviceId.isEmpty() || deviceId.length() > MAX_DEVICE_ID_BYTES) {
            throw new IllegalArgumentException("invalid device id");
        }
        if (roles <= 0 || roles > (ROLE_HOST | ROLE_VIEWER)) {
            throw new IllegalArgumentException("invalid roles");
        }
        this.host = host;
        this.port = port;
        this.deviceId = deviceId;
        this.roles = roles;
        this.listener = listener;
    }

    synchronized void start() throws IOException, TimeoutException, SignalingException,
            InterruptedException {
        if (socket != null) {
            return;
        }

        DatagramSocket created = new DatagramSocket(0);
        InetAddress address = InetAddress.getByName(host);
        created.connect(address, port);
        socket = created;
        closed = false;

        receiveThread = new Thread(this::receiveLoop, "screencast-signaling-rx");
        receiveThread.start();

        byte[] reply = request(SignalingWire.REGISTER, registerBody());
        SignalingWire.Header header = SignalingWire.parseHeader(reply);
        if (header == null || header.type != SignalingWire.REGISTERED) {
            throw new SignalingException(SignalingWire.ERROR_MALFORMED,
                    "registration reply is invalid");
        }

        SignalingWire.Reader reader = new SignalingWire.Reader(reply, header);
        reader.text(MAX_DEVICE_ID_BYTES);
        reader.u8();
        heartbeatIntervalMs = (int) reader.u32();
        reader.u32();
        reader.requireEnd();

        heartbeatThread = new Thread(this::heartbeatLoop, "screencast-signaling-hb");
        heartbeatThread.start();
        listener.onRegistered(heartbeatIntervalMs);
    }

    SessionInfo joinSession(String code) throws IOException, TimeoutException, SignalingException,
            InterruptedException {
        if (code == null || code.trim().isEmpty()) {
            throw new IllegalArgumentException("code is required");
        }
        SignalingWire.Writer writer = new SignalingWire.Writer();
        writer.text(code.trim());
        writer.id(SignalingWire.zeroId());
        byte[] reply = request(SignalingWire.JOIN_SESSION, writer.body());
        SignalingWire.Header header = SignalingWire.parseHeader(reply);
        if (header == null) {
            throw new SignalingException(SignalingWire.ERROR_MALFORMED, "session reply is invalid");
        }
        return SessionInfo.decode(reply, header);
    }

    void sendPeerKey(int type, byte[] sessionId, byte[] payload) throws IOException,
            TimeoutException, SignalingException, InterruptedException {
        if (type != SignalingWire.PEER_KEY_OFFER && type != SignalingWire.PEER_KEY_ANSWER) {
            throw new IllegalArgumentException("invalid peer key type");
        }
        if (payload == null || payload.length > SignalingWire.MAX_BLOB_BYTES) {
            throw new IllegalArgumentException("peer key payload is invalid");
        }
        SignalingWire.Writer writer = new SignalingWire.Writer();
        writer.id(sessionId);
        writer.id(SignalingWire.zeroId());
        writer.blob(payload);
        byte[] reply = request(type, writer.body());
        SignalingWire.Header header = SignalingWire.parseHeader(reply);
        if (header == null || header.type != SignalingWire.ACK) {
            throw new SignalingException(SignalingWire.ERROR_MALFORMED, "peer key reply is invalid");
        }
    }

    void close() {
        closed = true;
        DatagramSocket active = socket;
        socket = null;
        if (active != null) {
            active.close();
        }
        if (receiveThread != null) {
            receiveThread.interrupt();
            receiveThread = null;
        }
        if (heartbeatThread != null) {
            heartbeatThread.interrupt();
            heartbeatThread = null;
        }
        for (ArrayBlockingQueue<Object> queue : pending.values()) {
            queue.offer(new SignalingException(SignalingWire.ERROR_MALFORMED, "client closed"));
        }
        pending.clear();
    }

    private byte[] registerBody() {
        SignalingWire.Writer writer = new SignalingWire.Writer();
        writer.text(deviceId);
        writer.u8(roles);
        writer.u8(0);
        writer.blob(new byte[0]);
        writer.endpoints(new ArrayList<>());
        return writer.body();
    }

    private byte[] request(int type, byte[] body) throws IOException, TimeoutException,
            SignalingException, InterruptedException {
        DatagramSocket active = socket;
        if (active == null) {
            throw new IllegalStateException("client is not started");
        }

        byte[] requestId = SignalingWire.newId();
        String key = SignalingWire.idHex(requestId);
        ArrayBlockingQueue<Object> queue = new ArrayBlockingQueue<>(1);
        pending.put(key, queue);
        try {
            byte[] message = SignalingWire.encode(type, requestId, body);
            for (int attempt = 0; attempt < MAX_REQUEST_ATTEMPTS; attempt++) {
                active.send(new DatagramPacket(message, message.length));
                Object result = queue.poll(RETRY_INTERVAL_MS, TimeUnit.MILLISECONDS);
                if (result instanceof byte[]) {
                    return (byte[]) result;
                }
                if (result instanceof SignalingException) {
                    throw (SignalingException) result;
                }
                if (result instanceof IOException) {
                    throw (IOException) result;
                }
            }
            throw new TimeoutException("No reply to " + type + " from " + host + ":" + port);
        } finally {
            pending.remove(key);
        }
    }

    private void receiveLoop() {
        DatagramSocket active = socket;
        if (active == null) {
            return;
        }
        byte[] buffer = new byte[SignalingWire.MAX_MESSAGE_BYTES];
        while (!closed) {
            DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
            try {
                active.receive(packet);
            } catch (IOException error) {
                if (!closed) {
                    listener.onFaulted(error);
                }
                return;
            }
            byte[] datagram = Arrays.copyOf(packet.getData(), packet.getLength());
            try {
                dispatch(datagram);
            } catch (RuntimeException error) {
                listener.onFaulted(error);
            }
        }
    }

    private void dispatch(byte[] datagram) {
        SignalingWire.Header header = SignalingWire.parseHeader(datagram);
        if (header == null) {
            return;
        }

        String key = SignalingWire.idHex(header.requestId);
        if (header.type == SignalingWire.ERROR) {
            SignalingWire.Reader reader = new SignalingWire.Reader(datagram, header);
            int code = reader.u16();
            String detail = reader.text(100);
            reader.requireEnd();
            SignalingException error = new SignalingException(code, detail);
            ArrayBlockingQueue<Object> failed = pending.remove(key);
            if (failed != null) {
                failed.offer(error);
            } else {
                listener.onFailed(error);
            }
            return;
        }

        ArrayBlockingQueue<Object> completion = pending.remove(key);
        if (completion != null) {
            completion.offer(datagram);
            return;
        }

        switch (header.type) {
            case SignalingWire.SESSION_CLOSED:
                listener.onSessionClosed(SessionInfo.decode(datagram, header));
                break;
            case SignalingWire.PEER_KEY_OFFER:
            case SignalingWire.PEER_KEY_ANSWER:
                listener.onPeerKey(PeerKeyMessage.decode(datagram, header));
                break;
            default:
                break;
        }
    }

    private void heartbeatLoop() {
        while (!closed) {
            try {
                Thread.sleep(heartbeatIntervalMs);
                request(SignalingWire.HEARTBEAT, new byte[0]);
            } catch (InterruptedException interrupted) {
                return;
            } catch (TimeoutException ignored) {
                continue;
            } catch (SignalingException error) {
                if (error.code == SignalingWire.ERROR_UNREGISTERED) {
                    try {
                        request(SignalingWire.REGISTER, registerBody());
                        continue;
                    } catch (Exception retryError) {
                        listener.onFaulted(retryError);
                        return;
                    }
                }
                listener.onFaulted(error);
                return;
            } catch (IOException error) {
                if (!closed) {
                    listener.onFaulted(error);
                }
                return;
            }
        }
    }
}

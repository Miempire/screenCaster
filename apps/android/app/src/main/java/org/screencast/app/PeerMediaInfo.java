package org.screencast.app;

import java.net.Inet4Address;
import java.net.Inet6Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.NetworkInterface;
import java.net.SocketException;
import java.net.UnknownHostException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

final class PeerMediaInfo {

    static final int VERSION = 1;
    static final int HOST_ROLE = 0;
    static final int VIEWER_ROLE = 1;
    static final int MAX_ADDRESSES = 8;

    private static final int HEADER_BYTES = 7;

    final int role;
    final int videoPort;
    final int controlPort;
    final List<InetAddress> localAddresses;

    PeerMediaInfo(int role, int videoPort, int controlPort, List<InetAddress> localAddresses) {
        this.role = role;
        this.videoPort = videoPort;
        this.controlPort = controlPort;
        this.localAddresses = localAddresses == null
                ? Collections.emptyList()
                : new ArrayList<>(localAddresses);
    }

    byte[] encode() {
        if (role > VIEWER_ROLE) {
            throw new IllegalArgumentException("role must be 0 or 1");
        }
        if (localAddresses.size() > MAX_ADDRESSES) {
            throw new IllegalArgumentException("too many local addresses");
        }
        byte[] buffer = new byte[HEADER_BYTES + localAddresses.size() * 4];
        buffer[0] = (byte) VERSION;
        buffer[1] = (byte) role;
        buffer[2] = (byte) (videoPort >> 8);
        buffer[3] = (byte) videoPort;
        buffer[4] = (byte) (controlPort >> 8);
        buffer[5] = (byte) controlPort;
        buffer[6] = (byte) localAddresses.size();
        int offset = HEADER_BYTES;
        for (InetAddress address : localAddresses) {
            InetAddress normalized = normalize(address);
            if (!(normalized instanceof Inet4Address)) {
                throw new IllegalArgumentException("only IPv4 candidates are supported");
            }
            byte[] bytes = normalized.getAddress();
            System.arraycopy(bytes, 0, buffer, offset, 4);
            offset += 4;
        }
        return buffer;
    }

    static PeerMediaInfo decode(byte[] payload) {
        if (payload == null || payload.length < HEADER_BYTES || (payload[0] & 0xFF) != VERSION) {
            throw new IllegalArgumentException("unsupported peer media payload");
        }
        int count = payload[6] & 0xFF;
        if (count > MAX_ADDRESSES || payload.length != HEADER_BYTES + count * 4) {
            throw new IllegalArgumentException("invalid address count");
        }
        List<InetAddress> addresses = new ArrayList<>(count);
        for (int index = 0; index < count; index++) {
            byte[] raw = new byte[4];
            System.arraycopy(payload, HEADER_BYTES + index * 4, raw, 0, 4);
            addresses.add(fromBytes(raw));
        }
        int videoPort = ((payload[2] & 0xFF) << 8) | (payload[3] & 0xFF);
        int controlPort = ((payload[4] & 0xFF) << 8) | (payload[5] & 0xFF);
        return new PeerMediaInfo(payload[1] & 0xFF, videoPort, controlPort, addresses);
    }

    static List<InetAddress> localIPv4Addresses() {
        List<InetAddress> addresses = new ArrayList<>();
        try {
            for (NetworkInterface adapter : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!adapter.isUp() || adapter.isLoopback()) {
                    continue;
                }
                for (InetAddress address : Collections.list(adapter.getInetAddresses())) {
                    if (!(address instanceof Inet4Address)) {
                        continue;
                    }
                    byte[] bytes = address.getAddress();
                    if (bytes[0] == 127 || ((bytes[0] & 0xFF) == 169 && (bytes[1] & 0xFF) == 254)) {
                        continue;
                    }
                    if (!addresses.contains(address)) {
                        addresses.add(address);
                    }
                }
            }
        } catch (SocketException error) {
            return addresses;
        }
        return addresses;
    }

    static InetAddress choosePeerAddress(PeerMediaInfo peer, InetSocketAddress peerObserved,
            InetSocketAddress ownObserved) {
        boolean sameNat = peerObserved != null && ownObserved != null
                && normalize(peerObserved.getAddress()).equals(normalize(ownObserved.getAddress()));
        if (sameNat && !peer.localAddresses.isEmpty()) {
            return peer.localAddresses.get(0);
        }
        if (peerObserved != null) {
            return normalize(peerObserved.getAddress());
        }
        if (!peer.localAddresses.isEmpty()) {
            return peer.localAddresses.get(0);
        }
        throw new IllegalStateException("no peer address is available");
    }

    private static InetAddress normalize(InetAddress address) {
        if (address instanceof Inet6Address) {
            byte[] bytes = address.getAddress();
            if (isIPv4Mapped(bytes)) {
                return fromBytes(new byte[] { bytes[12], bytes[13], bytes[14], bytes[15] });
            }
        }
        return address;
    }

    private static boolean isIPv4Mapped(byte[] bytes) {
        if (bytes.length != 16) {
            return false;
        }
        for (int index = 0; index < 10; index++) {
            if (bytes[index] != 0) {
                return false;
            }
        }
        return (bytes[10] & 0xFF) == 0xFF && (bytes[11] & 0xFF) == 0xFF;
    }

    private static InetAddress fromBytes(byte[] bytes) {
        try {
            return InetAddress.getByAddress(bytes);
        } catch (UnknownHostException error) {
            throw new IllegalArgumentException(error);
        }
    }
}

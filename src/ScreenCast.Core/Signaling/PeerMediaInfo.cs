using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenCast.Core.Signaling;

public sealed record PeerMediaInfo(byte Role, ushort VideoPort, ushort ControlPort, IReadOnlyList<IPAddress> LocalAddresses)
{
    public const byte Version = 1;
    public const byte HostRole = 0;
    public const byte ViewerRole = 1;
    public const int MaxAddresses = 8;
    private const int HeaderBytes = 7;

    public bool IsHost => Role == HostRole;

    public byte[] Encode()
    {
        if (Role > ViewerRole) throw new ArgumentOutOfRangeException(nameof(Role));
        if (LocalAddresses.Count > MaxAddresses) throw new ArgumentOutOfRangeException(nameof(LocalAddresses));
        byte[] buffer = new byte[HeaderBytes + LocalAddresses.Count * 4];
        buffer[0] = Version;
        buffer[1] = Role;
        buffer[2] = (byte)(VideoPort >> 8);
        buffer[3] = (byte)VideoPort;
        buffer[4] = (byte)(ControlPort >> 8);
        buffer[5] = (byte)ControlPort;
        buffer[6] = (byte)LocalAddresses.Count;
        int offset = HeaderBytes;
        foreach (IPAddress address in LocalAddresses)
        {
            IPAddress normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (normalized.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("Only IPv4 candidates are supported.", nameof(LocalAddresses));
            }
            normalized.GetAddressBytes().CopyTo(buffer, offset);
            offset += 4;
        }
        return buffer;
    }

    public static PeerMediaInfo Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderBytes || payload[0] != Version) throw new FormatException("Unsupported peer media payload.");
        int count = payload[6];
        if (count > MaxAddresses || payload.Length != HeaderBytes + count * 4) throw new FormatException("Peer media payload has an invalid address count.");
        var addresses = new List<IPAddress>(count);
        for (int i = 0; i < count; i++)
        {
            addresses.Add(new IPAddress(payload.Slice(HeaderBytes + i * 4, 4)));
        }
        ushort videoPort = (ushort)((payload[2] << 8) | payload[3]);
        ushort controlPort = (ushort)((payload[4] << 8) | payload[5]);
        return new PeerMediaInfo(payload[1], videoPort, controlPort, addresses);
    }

    public static IReadOnlyList<IPAddress> LocalIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] bytes = unicast.Address.GetAddressBytes();
                if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254)) continue;
                if (!addresses.Contains(unicast.Address)) addresses.Add(unicast.Address);
            }
        }
        return addresses;
    }

    public static IPAddress ChoosePeerAddress(PeerMediaInfo peer, IPEndPoint? peerObserved, IPEndPoint? ownObserved)
    {
        bool sameNat = peerObserved is not null && ownObserved is not null &&
            peerObserved.Address.MapToIPv4().Equals(ownObserved.Address.MapToIPv4());
        if (sameNat && peer.LocalAddresses.Count > 0) return peer.LocalAddresses[0];
        if (peerObserved is not null) return peerObserved.Address;
        if (peer.LocalAddresses.Count > 0) return peer.LocalAddresses[0];
        throw new InvalidOperationException("No peer address is available.");
    }
}

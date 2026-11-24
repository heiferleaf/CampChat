using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace CampChat.Network;

internal sealed record RoomInvite(IPEndPoint Endpoint, byte[] Fingerprint, byte[] Secret)
{
    private const string Prefix = "CC1-";
    private const int PayloadLength = 54;

    public string Encode()
    {
        var data = new byte[PayloadLength];
        Endpoint.Address.GetAddressBytes().CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)Endpoint.Port);
        Fingerprint.CopyTo(data, 6);
        Secret.CopyTo(data, 38);
        return Prefix + Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_');
    }

    public static RoomInvite Parse(string value)
    {
        value = value.Trim();
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)
            || value.Length != Prefix.Length + 72)
        {
            throw new FormatException("请粘贴完整的房间码。");
        }

        Span<byte> data = stackalloc byte[PayloadLength];
        if (!Convert.TryFromBase64String(value[Prefix.Length..].Replace('-', '+').Replace('_', '/'),
                data, out var length) || length != PayloadLength)
        {
            throw new FormatException("房间码格式有误，请让房主重新复制。");
        }

        var address = new IPAddress(data[..4]);
        var port = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);
        if (port == 0 || !CanBind(address))
        {
            throw new FormatException("房间码里的地址无效。");
        }

        return new RoomInvite(new IPEndPoint(address, port), data[6..38].ToArray(), data[38..].ToArray());
    }

    public static bool CanBind(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes()[0] is > 0 and < 224;
}

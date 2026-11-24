using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Text.Json;

namespace CampChat.Network;

internal sealed record Packet(string Kind, string? Name = null, string? Text = null, string? Token = null);

internal static class Wire
{
    public const int MaxName = 24;
    public const int MaxText = 400;
    private const int MaxPacket = 4096;

    public static string? Clean(string? value, int max)
    {
        if (value == null)
            return null;

        var text = value.Trim();
        return text.Length is > 0 && text.Length <= max && !text.Any(char.IsControl) ? text : null;
    }

    public static async Task<Packet> ReadAsync(SslStream stream, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size is < 1 or > MaxPacket)
            throw new InvalidDataException("消息长度无效。");

        var data = new byte[size];
        await stream.ReadExactlyAsync(data, deadline.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Packet>(data) ?? throw new InvalidDataException("消息格式无效。");
    }

    public static byte[] Encode(Packet packet)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (data.Length > MaxPacket)
            throw new InvalidDataException("消息长度无效。");

        var frame = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, data.Length);
        data.CopyTo(frame, 4);
        return frame;
    }

    public static bool IsConnectionError(Exception error) =>
        error is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException
            or ObjectDisposedException or System.Security.Authentication.AuthenticationException or JsonException;
}

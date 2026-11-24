using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace CampChat.Network;

internal sealed class GuestRoom : IRoomSession
{
    private readonly RoomConnection _connection;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<RoomEvent> _notify;
    private Task _receiving = Task.CompletedTask;
    private Task _heartbeat = Task.CompletedTask;
    private int _terminated;

    private GuestRoom(RoomConnection connection, Action<RoomEvent> notify)
    {
        _connection = connection;
        _notify = notify;
    }

    public static async Task<GuestRoom> JoinAsync(RoomInvite invite, string nickname, Action<RoomEvent> notify,
        CancellationToken cancellationToken)
    {
        var name = Wire.Clean(nickname, Wire.MaxName) ?? throw new ArgumentException("请填写昵称。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(9));
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        RoomConnection? connection = null;
        try
        {
            await client.ConnectAsync(invite.Endpoint, deadline.Token).ConfigureAwait(false);
            var stream = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                certificate != null && CryptographicOperations.FixedTimeEquals(
                    certificate.GetCertHash(HashAlgorithmName.SHA256), invite.Fingerprint));
            connection = new RoomConnection(client, stream);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "CampChat Room",
                EnabledSslProtocols = SslProtocols.None,
            }, deadline.Token).ConfigureAwait(false);
            await connection.SendAsync(new("join", Name: name, Token: Convert.ToBase64String(invite.Secret)),
                deadline.Token).ConfigureAwait(false);
            var response = await connection.ReadAsync(TimeSpan.FromSeconds(5), deadline.Token).ConfigureAwait(false);
            if (response.Kind != "joined")
                throw new InvalidOperationException(response.Kind == "error"
                    ? Wire.Clean(response.Text, 120) ?? "房主拒绝了连接。"
                    : "房主返回了无法识别的消息。");

            return new GuestRoom(connection, notify);
        }
        catch
        {
            connection?.Dispose();
            client.Dispose();
            throw;
        }
    }

    public void Start()
    {
        _receiving = ReceiveAsync(_lifetime.Token);
        _heartbeat = HeartbeatAsync(_lifetime.Token);
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _connection.ReadAsync(TimeSpan.FromSeconds(40), cancellationToken)
                    .ConfigureAwait(false);
                switch (packet.Kind)
                {
                    case "message" when Wire.Clean(packet.Name, Wire.MaxName) is { } name
                                        && Wire.Clean(packet.Text, Wire.MaxText) is { } text:
                        _notify(new(RoomEventKind.Message, text, name));
                        break;
                    case "notice" when Wire.Clean(packet.Text, 120) is { } notice:
                        _notify(new(RoomEventKind.Notice, notice));
                        break;
                    case "count" when int.TryParse(packet.Text, out var count) && count is >= 1 and <= 8:
                        _notify(new(RoomEventKind.Count, Count: count));
                        break;
                    case "pong":
                        break;
                    default:
                        throw new InvalidDataException("房主发来了无法识别的消息。");
                }
            }
        }
        catch (Exception error) when (Wire.IsConnectionError(error))
        {
            Stop(notify: true);
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(12));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await _connection.SendAsync(new("ping"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Wire.IsConnectionError(error))
        {
            Stop(notify: true);
        }
    }

    public Task SendAsync(string text) => _connection.SendAsync(new("message", Text:
        Wire.Clean(text, Wire.MaxText) ?? throw new ArgumentException("请输入 1–400 字的单行消息。")), _lifetime.Token);

    private void Stop(bool notify)
    {
        if (Interlocked.Exchange(ref _terminated, 1) != 0)
            return;
        _lifetime.Cancel();
        _connection.Abort();
        if (notify)
            _notify(new(RoomEventKind.Closed, "连接已断开。房主开着房间时，可以用原房间码重新加入。"));
    }

    public async ValueTask DisposeAsync()
    {
        Stop(notify: false);
        await Task.WhenAll(_receiving, _heartbeat).ConfigureAwait(false);
        _connection.Dispose();
        _lifetime.Dispose();
    }
}

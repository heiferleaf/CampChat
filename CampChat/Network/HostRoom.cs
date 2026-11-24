using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CampChat.Network;

internal sealed class HostRoom : IRoomSession
{
    public const int DefaultPort = 47821;
    private const int MaxGuests = 7;
    private const int MaxConnections = 16;
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly string _hostName;
    private readonly Action<RoomEvent> _notify;
    private readonly ConcurrentDictionary<TcpClient, Task> _connections = new();
    private readonly ConcurrentDictionary<RoomConnection, string> _guests = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _publication = new(1, 1);
    private readonly SemaphoreSlim _guestSlots = new(MaxGuests, MaxGuests);
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(16);
    private readonly Task _accepting;

    public string InviteCode { get; }

    public HostRoom(IPAddress address, int port, string name, Action<RoomEvent> notify)
    {
        _hostName = Wire.Clean(name, Wire.MaxName) ?? throw new ArgumentException("请填写昵称。");
        if (!RoomInvite.CanBind(address))
            throw new ArgumentException("请选择有效的 IPv4 网卡。");

        _notify = notify;
        _certificate = CreateCertificate();
        _listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
        try
        {
            _listener.Start(MaxConnections);
            InviteCode = new RoomInvite((IPEndPoint)_listener.LocalEndpoint,
                _certificate.GetCertHash(HashAlgorithmName.SHA256), _secret).Encode();
        }
        catch
        {
            _listener.Stop();
            _certificate.Dispose();
            throw;
        }

        _accepting = AcceptAsync(_lifetime.Token);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=CampChat Room", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(2));
        // Schannel needs a Windows-backed private key for the TLS server credential.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.DefaultKeySet);
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (Wire.IsConnectionError(error))
            {
                if (!cancellationToken.IsCancellationRequested)
                    _notify(new(RoomEventKind.Closed, "房主监听已关闭。"));
                return;
            }

            if (_connections.Count >= MaxConnections)
            {
                client.Dispose();
                continue;
            }

            var handling = HandleAsync(client, cancellationToken);
            _connections[client] = handling;
            _ = RemoveAfterCompletionAsync(client, handling);
        }
    }

    private async Task RemoveAfterCompletionAsync(TcpClient client, Task handling)
    {
        try
        {
            await handling.ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(client, out _);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        RoomConnection? connection = null;
        SslStream? stream = null;
        try
        {
            client.NoDelay = true;
            stream = new SslStream(client.GetStream());
            connection = new RoomConnection(client, stream);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(7));
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.None,
                }, handshake.Token).ConfigureAwait(false);

                var hello = await connection.ReadAsync(TimeSpan.FromSeconds(5), handshake.Token).ConfigureAwait(false);
                var name = Wire.Clean(hello.Name, Wire.MaxName);
                if (hello.Kind != "join" || name == null || !MatchesSecret(hello.Token))
                {
                    await connection.SendAsync(new("error", Text: "房间码已失效或昵称无效。"), handshake.Token)
                        .ConfigureAwait(false);
                    return;
                }

                if (!await AddGuestAsync(connection, name, handshake.Token).ConfigureAwait(false))
                    return;
            }

            var windowStart = Environment.TickCount64;
            var packets = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await connection.ReadAsync(TimeSpan.FromSeconds(40), cancellationToken)
                    .ConfigureAwait(false);
                if (Environment.TickCount64 - windowStart >= 2000)
                {
                    windowStart = Environment.TickCount64;
                    packets = 0;
                }
                if (++packets > 24)
                    return;

                if (packet.Kind == "ping")
                {
                    await connection.SendAsync(new("pong"), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var text = Wire.Clean(packet.Text, Wire.MaxText);
                if (packet.Kind != "message" || text == null)
                    return;

                await PublishAsync(new("message", _guests[connection], text), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (Wire.IsConnectionError(error))
        {
            // A bad or disconnected guest affects only that connection.
        }
        finally
        {
            if (connection != null)
                await connection.CloseAsync().ConfigureAwait(false);
            else
            {
                stream?.Dispose();
                client.Dispose();
            }

            if (connection != null && _guests.TryRemove(connection, out var name))
            {
                _guestSlots.Release();
                try
                {
                    if (!cancellationToken.IsCancellationRequested)
                        await PublishAsync(new("notice", Text: name + " 离开了房间。"), cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
    }

    private bool MatchesSecret(string? token)
    {
        Span<byte> supplied = stackalloc byte[16];
        return token != null && Convert.TryFromBase64String(token, supplied, out var count)
            && count == supplied.Length && CryptographicOperations.FixedTimeEquals(_secret, supplied);
    }

    private async Task<bool> AddGuestAsync(RoomConnection connection, string name, CancellationToken cancellationToken)
    {
        if (!_guestSlots.Wait(0, cancellationToken))
        {
            await connection.SendAsync(new("error", Text: "房间已满。"), cancellationToken).ConfigureAwait(false);
            return false;
        }

        var added = false;
        try
        {
            await connection.SendAsync(new("joined"), cancellationToken).ConfigureAwait(false);
            connection.StartQueue(_lifetime.Token);
            added = _guests.TryAdd(connection, name);
        }
        finally
        {
            if (!added)
                _guestSlots.Release();
        }

        if (!added)
            return false;
        await PublishAsync(new("notice", Text: name + " 加入了房间。"), _lifetime.Token).ConfigureAwait(false);
        return true;
    }

    public Task SendAsync(string text) =>
        PublishAsync(new("message", _hostName, Wire.Clean(text, Wire.MaxText)
            ?? throw new ArgumentException("请输入 1–400 字的单行消息。")), _lifetime.Token);

    private async Task PublishAsync(Packet packet, CancellationToken cancellationToken)
    {
        await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var peers = _guests.Keys;
            var frame = Wire.Encode(packet);
            var notice = packet.Kind == "notice";
            var people = peers.Count + 1;
            byte[]? count = notice ? Wire.Encode(new("count", Text: people.ToString(
                System.Globalization.CultureInfo.InvariantCulture))) : null;
            foreach (var peer in peers)
            {
                if (!peer.Enqueue(frame) || (count != null && !peer.Enqueue(count)))
                    peer.Abort();
            }

            _notify(new(notice ? RoomEventKind.Notice : RoomEventKind.Message,
                packet.Text ?? "", packet.Name));
            if (notice)
                _notify(new(RoomEventKind.Count, Count: people));
        }
        finally
        {
            _publication.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        await _accepting.ConfigureAwait(false);
        foreach (var client in _connections.Keys)
            client.Dispose();
        await Task.WhenAll(_connections.Values).ConfigureAwait(false);
        _certificate.Dispose();
        _guestSlots.Dispose();
        _lifetime.Dispose();
    }
}

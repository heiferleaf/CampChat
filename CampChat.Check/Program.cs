using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading.Channels;
using CampChat.Network;

static void Require(bool result, string reason)
{
    if (!result)
        throw new Exception(reason);
}

static async Task<RoomEvent> ExpectAsync(Channel<RoomEvent> events, Func<RoomEvent, bool> predicate,
    CancellationToken timeout)
{
    while (true)
    {
        var notice = await events.Reader.ReadAsync(timeout);
        if (predicate(notice))
            return notice;
    }
}

static async Task CheckRoomAsync(IPAddress address, string description, bool rejectInvalid)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(22));
    var hostEvents = Channel.CreateUnbounded<RoomEvent>();
    var firstEvents = Channel.CreateUnbounded<RoomEvent>();
    var secondEvents = Channel.CreateUnbounded<RoomEvent>();
    var host = new HostRoom(address, 0, "房主", item => hostEvents.Writer.TryWrite(item));
    try
    {
        var invite = RoomInvite.Parse(host.InviteCode);
        Require(invite.Endpoint.Address.Equals(address) && invite.Encode() == host.InviteCode, "Room code round trip");
        if (rejectInvalid)
        {
            var incorrectFingerprint = invite.Fingerprint.ToArray();
            incorrectFingerprint[0] ^= 1;
            try
            {
                await using var unauthorized = await GuestRoom.JoinAsync(invite with { Fingerprint = incorrectFingerprint },
                    "无效", _ => { }, timeout.Token);
                throw new Exception("An invalid certificate pin was accepted.");
            }
            catch (AuthenticationException) { }

            var incorrectSecret = invite.Secret.ToArray();
            incorrectSecret[0] ^= 1;
            try
            {
                await using var unauthorized = await GuestRoom.JoinAsync(invite with { Secret = incorrectSecret },
                    "无效", _ => { }, timeout.Token);
                throw new Exception("An invalid invite secret was accepted.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("房间码已失效")) { }
        }
        await using var first = await GuestRoom.JoinAsync(invite, "队员甲", item => firstEvents.Writer.TryWrite(item), timeout.Token);
        first.Start();
        await using var second = await GuestRoom.JoinAsync(invite, "队员乙", item => secondEvents.Writer.TryWrite(item), timeout.Token);
        second.Start();
        await ExpectAsync(hostEvents, item => item.Kind == RoomEventKind.Count && item.Count == 3, timeout.Token);
        var chinese = new string('魂', 398) + "🗝";
        await first.SendAsync(chinese);
        foreach (var recipient in new[] { hostEvents, firstEvents, secondEvents })
        {
            var received = await ExpectAsync(recipient, item => item.Kind == RoomEventKind.Message, timeout.Token);
            Require(received.Text == chinese && received.Name == "队员甲", "Chinese and emoji message integrity");
        }
        await host.SendAsync("进下一间");
        foreach (var recipient in new[] { firstEvents, secondEvents })
        {
            var received = await ExpectAsync(recipient, item => item.Kind == RoomEventKind.Message, timeout.Token);
            Require(received.Text == "进下一间" && received.Name == "房主", "Host-to-guest message delivery");
        }
        if (rejectInvalid)
        {
            var extras = new List<GuestRoom>();
            try
            {
                for (var index = 0; index < 5; index++)
                {
                    var guest = await GuestRoom.JoinAsync(invite, $"队员{index}", _ => { }, timeout.Token);
                    guest.Start();
                    extras.Add(guest);
                }
                try
                {
                    await using var overflow = await GuestRoom.JoinAsync(invite, "多余", _ => { }, timeout.Token);
                    throw new Exception("Room admitted a ninth participant.");
                }
                catch (InvalidOperationException error) when (error.Message == "房间已满。") { }
            }
            finally
            {
                foreach (var guest in extras)
                    await guest.DisposeAsync();
            }
        }

        await host.DisposeAsync();
        host = null!;
        await ExpectAsync(firstEvents, item => item.Kind == RoomEventKind.Closed, timeout.Token);
        await ExpectAsync(secondEvents, item => item.Kind == RoomEventKind.Closed, timeout.Token);
        Console.WriteLine($"PASS {description}: TLS, Chinese and emoji, three-way broadcast, host disconnect"
                          + (rejectInvalid ? ", rejects wrong certificate and secret, eight-person limit" : ""));
    }
    finally
    {
        if (host != null)
            await host.DisposeAsync();
    }
}

await CheckRoomAsync(IPAddress.Loopback, "loopback", true);
await CheckSlowGuestAsync();
var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up);
foreach (var adapter in adapters.Where(item => item.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
    || item.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase)))
{
    var address = adapter.GetIPProperties().UnicastAddresses.Select(item => item.Address).FirstOrDefault(RoomInvite.CanBind);
    if (address != null)
        await CheckRoomAsync(address, adapter.Name, false);
}
try
{
    RoomInvite.Parse("CC1-incorrect");
    throw new Exception("A malformed invitation was accepted.");
}
catch (FormatException)
{
    Console.WriteLine("PASS malformed room code");
}

static async Task CheckSlowGuestAsync()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
    var events = Channel.CreateUnbounded<RoomEvent>();
    await using var host = new HostRoom(IPAddress.Loopback, 0, "房主", _ => { });
    var invite = RoomInvite.Parse(host.InviteCode);
    await using var fast = await GuestRoom.JoinAsync(invite, "正常队友", item => events.Writer.TryWrite(item), timeout.Token);
    fast.Start();

    using var slow = new TcpClient(AddressFamily.InterNetwork) { ReceiveBufferSize = 2048 };
    await slow.ConnectAsync(invite.Endpoint, timeout.Token);
    using var stalledStream = new SslStream(slow.GetStream(), false, (_, certificate, _, _) =>
        certificate != null && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256),
            invite.Fingerprint));
    await stalledStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost = "CampChat Room",
    }, timeout.Token);
    await stalledStream.WriteAsync(Wire.Encode(new("join", Name: "不读取消息的队友",
        Token: Convert.ToBase64String(invite.Secret))), timeout.Token);
    Require((await Wire.ReadAsync(stalledStream, TimeSpan.FromSeconds(3), timeout.Token)).Kind == "joined",
        "The slow test guest joined before it stopped reading.");
    var field = typeof(HostRoom).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var sockets = (System.Collections.Concurrent.ConcurrentDictionary<TcpClient, Task>)field.GetValue(host)!;
    var slowedSocket = sockets.Keys.Single(client => Equals(client.Client.RemoteEndPoint, slow.Client.LocalEndPoint));
    slowedSocket.SendBufferSize = 2048;

    var writeTime = System.Diagnostics.Stopwatch.StartNew();
    var publicationTime = TimeSpan.Zero;
    var payload = new string('魂', Wire.MaxText - 5);
    for (var index = 0; index < 220; index++)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await host.SendAsync($"{index:D3} {payload}");
        publicationTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
        await Task.Delay(4, timeout.Token);
    }
    await host.SendAsync("正常队友继续收到消息");
    await ExpectAsync(events, item => item.Kind == RoomEventKind.Message && item.Text == "正常队友继续收到消息",
        timeout.Token);
    Require(publicationTime < TimeSpan.FromSeconds(1), "A stalled client's socket did not block normal fanout.");
    Console.WriteLine($"PASS slow guest isolation: 221 messages in {writeTime.Elapsed.TotalMilliseconds:F0} ms, "
                      + $"{publicationTime.TotalMilliseconds:F0} ms in host publication.");
}

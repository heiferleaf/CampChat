using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;

namespace CampChat.Network;

internal sealed class RoomConnection(TcpClient client, SslStream stream) : IDisposable
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Channel<ReadOnlyMemory<byte>>? _outbox;
    private Task _draining = Task.CompletedTask;

    public Task SendAsync(Packet packet, CancellationToken cancellationToken) =>
        SendFrameAsync(Wire.Encode(packet), cancellationToken);

    private async Task SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        await _writer.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public Task<Packet> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        Wire.ReadAsync(stream, timeout, cancellationToken);

    public void StartQueue(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _outbox = channel;
        _draining = DrainAsync(channel.Reader, cancellationToken);
    }

    public bool Enqueue(ReadOnlyMemory<byte> frame) => _outbox?.Writer.TryWrite(frame) == true;

    private async Task DrainAsync(ChannelReader<ReadOnlyMemory<byte>> queue, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Wire.IsConnectionError(error))
        {
            Abort();
        }
    }

    public void Abort()
    {
        _outbox?.Writer.TryComplete();
        client.Dispose();
    }

    public async ValueTask CloseAsync()
    {
        Abort();
        await _draining.ConfigureAwait(false);
        stream.Dispose();
    }

    public void Dispose()
    {
        Abort();
        stream.Dispose();
    }
}

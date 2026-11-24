namespace CampChat.Network;

internal enum RoomEventKind { Message, Notice, Closed, Count }

internal sealed record RoomEvent(RoomEventKind Kind, string Text = "", string? Name = null, int Count = 0);

internal interface IRoomSession : IAsyncDisposable
{
    Task SendAsync(string text);
}

namespace Api.Entities;

internal sealed class RoomClockEntity
{
    public string RoomHash { get; set; } = string.Empty;
    public long LastLogicalTime { get; set; }
}

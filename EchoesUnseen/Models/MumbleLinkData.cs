namespace EchoesUnseen.Models;

/// <summary>
/// Snapshot of Guild Wars 2's MumbleLink shared-memory segment.
/// Null-return from the reader means the segment is empty (GW2 closed,
/// character on login screen, or loading screen between maps).
///
/// <see cref="PlayerX"/> and <see cref="PlayerY"/> are in continent coordinates,
/// which is the SAME coordinate system returned by
/// https://api.guildwars2.com/v2/continents/1/floors/1/regions/{r}/maps/{m}.
/// No conversion is needed — distances can be computed directly from these values.
/// </summary>
public class MumbleLinkData
{
    public int MapId { get; set; }
    public float PlayerX { get; set; }
    public float PlayerY { get; set; }
    public uint UiTick { get; set; }
}

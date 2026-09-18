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

    /// <summary>Character world position (metres) from fAvatarPosition — X (east),
    /// Y (up), Z (south). This is the coordinate space TacO / marker packs use
    /// (they store inches = these metres × 39.3701), so it drives marker navigation.</summary>
    public float AvatarX { get; set; }
    public float AvatarY { get; set; }
    public float AvatarZ { get; set; }

    /// <summary>Camera world position (metres) and forward vector, plus vertical FOV
    /// (radians). Used to project marker world positions onto the screen for the
    /// visible marker/trail overlay.</summary>
    public float CameraX { get; set; }
    public float CameraY { get; set; }
    public float CameraZ { get; set; }
    public float CameraFrontX { get; set; }
    public float CameraFrontY { get; set; }
    public float CameraFrontZ { get; set; }
    public float Fov { get; set; } = 0.873f;

    /// <summary>
    /// Horizontal facing vector (east, south) taken from MumbleLink's
    /// fAvatarFront. This is the direction the character is looking, in the SAME
    /// map plane as <see cref="PlayerX"/>/<see cref="PlayerY"/> — GW2 continent X
    /// is world X (east) and continent Y is world Z (south). Only the direction
    /// matters, so the values are used as a 2-D unit-ish vector.
    ///
    /// Always populated whenever GW2 is in a map, regardless of the in-game
    /// "rotate minimap" setting (unlike compassRotation). Used by the navigation
    /// compass to point RELATIVE to where the player is facing.
    /// </summary>
    public float FrontX { get; set; }
    public float FrontY { get; set; }

    /// <summary>
    /// Distance (in metres) between the camera and the character — i.e. how far
    /// zoomed out you are. Small when zoomed in close, large when zoomed out.
    /// The ground compass uses this to slide down to the feet as you zoom in, so
    /// it stays glued to the character instead of drifting up the screen.
    /// </summary>
    public float CameraDistance { get; set; }

    /// <summary>
    /// GW2's UI state bit-flags. The ones we use:
    ///   0x0010 = competitive mode (WvW or PvP)
    ///   0x0040 = in combat
    /// This is the game's own combat flag — reliable, unlike guessing from OCR.
    /// </summary>
    public uint UiState { get; set; }

    public bool IsInCombat   => (UiState & 0x0040) != 0;
    public bool IsCompetitive => (UiState & 0x0010) != 0;

    /// <summary>The current character's name, from the MumbleLink identity JSON. Lets
    /// features act on "whoever I'm playing right now" without a manual picker.</summary>
    public string? CharacterName { get; set; }
}

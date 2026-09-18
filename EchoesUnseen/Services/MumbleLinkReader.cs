using System.IO;
using System.IO.MemoryMappedFiles;
using EchoesUnseen.Models;

namespace EchoesUnseen.Services;

/// <summary>
/// Reads Guild Wars 2's MumbleLink shared-memory segment to get the player's
/// real-time map ID and position in continent coordinates.
///
/// WHY IT WORKS NOW (COMPARED TO THE OLD ELECTRON BUILD):
///   The previous Electron version shelled out to PowerShell because Node.js has
///   no native shared-memory API. That shelling introduced a locale bug — in
///   German/French/etc. system locales, PowerShell formatted floats with a comma
///   decimal separator ("12345,67") which broke our comma-delimited parsing and
///   made the app report "GW2 not detected" even when the game was running fine.
///
///   In C#, MemoryMappedFile.OpenExisting() reads the segment directly and
///   MemoryMappedViewAccessor.ReadSingle() returns a native float with no string
///   intermediate. The locale bug is architecturally impossible here.
///
/// BYTE LAYOUT (from the MumbleLink spec):
///   offset 0    uint32   uiVersion   (0 = GW2 not writing to the segment)
///   offset 4    uint32   uiTick      (0 = GW2 not yet in a loaded map)
///   offset 1112 uint32   mapId
///   offset 1144 float32  playerX     (continent coordinates)
///   offset 1148 float32  playerY     (continent coordinates)
///
/// LIFECYCLE:
///   The segment is typically created by GW2 on launch and torn down on exit.
///   If we try OpenExisting before GW2 is running we get FileNotFoundException.
///   In that case we CreateOrOpen instead — this reserves the segment so GW2
///   will write to it when it starts, avoiding a race where the overlay launches
///   before GW2 and misses the initial write.
/// </summary>
public class MumbleLinkReader : IDisposable
{
    private const string LinkName = "MumbleLink";
    private const int SegmentSize = 5460;

    // Byte offsets into the MumbleLink LinkedMem segment.
    //
    // These are derived from the standard layout, which the segment size proves:
    //   uiVersion(0) uiTick(4) fAvatarPosition[3](8) fAvatarFront[3](20)
    //   fAvatarTop[3](32) name[256]wchar(44) fCameraPosition[3](556)
    //   fCameraFront[3](568) fCameraTop[3](580) identity[256]wchar(592)
    //   context_len(1104) context[256](1108) description[2048]wchar(1364)
    //   => total 1364 + 4096 = 5460  (== SegmentSize, so context starts at 1108).
    //
    // GW2 writes its MumbleContext into context[]:
    //   serverAddress[28](+0) mapId(+28) mapType(+32) shardId(+36) instance(+40)
    //   buildId(+44) uiState(+48) compassWidth(+52) compassHeight(+54)
    //   compassRotation(+56) playerX(+60) playerY(+64) ...
    //
    // NOTE: earlier builds used 1112/1144/1148 (24 bytes too low — reading into
    // serverAddress), which produced garbage/negative map IDs and slightly-off
    // navigation. These are the spec-correct absolute offsets.
    private const int OffsetUiVersion   = 0;
    private const int OffsetUiTick      = 4;
    private const int OffsetAvatarPosX  = 8;     // fAvatarPosition.X
    private const int OffsetAvatarPosY  = 12;    // fAvatarPosition.Y
    private const int OffsetAvatarPosZ  = 16;    // fAvatarPosition.Z
    private const int OffsetAvatarFrontX = 20;   // fAvatarFront.X (east)
    private const int OffsetAvatarFrontZ = 28;   // fAvatarFront.Z (south)
    private const int OffsetCameraPosX  = 556;   // fCameraPosition.X
    private const int OffsetCameraPosY  = 560;   // fCameraPosition.Y
    private const int OffsetCameraPosZ  = 564;   // fCameraPosition.Z
    private const int OffsetCameraFrontX = 568;  // fCameraFront.X
    private const int OffsetCameraFrontY = 572;  // fCameraFront.Y
    private const int OffsetCameraFrontZ = 576;  // fCameraFront.Z
    private const int OffsetIdentity    = 592;   // identity JSON (wchar[256]) — has "fov"
    private const int OffsetMapId       = 1108 + 28;  // 1136
    private const int OffsetUiState     = 1108 + 48;  // 1156 (combat / competitive flags)
    private const int OffsetPlayerX     = 1108 + 60;  // 1168
    private const int OffsetPlayerY     = 1108 + 64;  // 1172

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private bool _disposed;

    public MumbleLinkReader()
    {
        try
        {
            // Preferred: segment already exists because GW2 is already running.
            _mmf = MemoryMappedFile.OpenExisting(LinkName);
        }
        catch (FileNotFoundException)
        {
            // GW2 hasn't launched yet. Create the segment ourselves so GW2 will
            // write to it when it starts. This also means the overlay CAN be
            // launched first — it will immediately start seeing data as soon as
            // GW2 loads a character into a map.
            _mmf = MemoryMappedFile.CreateOrOpen(LinkName, SegmentSize);
        }
        catch (Exception ex)
        {
            // Other failures (permissions, name collision) — surface to diagnostics.
            CrashLogger.Log("MumbleLinkReader ctor", ex);
            throw;
        }

        _accessor = _mmf.CreateViewAccessor(0, SegmentSize);
    }

    /// <summary>
    /// Reads the current MumbleLink state.
    /// Returns null when GW2 is not running or the character is not yet loaded
    /// into a map (login screen, character select, loading screen).
    /// Callers should handle null by showing "Waiting for Guild Wars 2..."
    /// rather than treating it as an error.
    /// </summary>
    public MumbleLinkData? Read()
    {
        if (_disposed || _accessor == null) return null;

        try
        {
            uint uiVersion = _accessor.ReadUInt32(OffsetUiVersion);
            uint uiTick    = _accessor.ReadUInt32(OffsetUiTick);

            // uiVersion == 0 → segment has never been written to (GW2 not running)
            // uiTick == 0    → GW2 is running but not yet ticking (character select)
            if (uiVersion == 0 || uiTick == 0) return null;

            uint rawMapId = _accessor.ReadUInt32(OffsetMapId);
            // GW2 map IDs are small positive numbers (< ~2000). Anything huge is a
            // stale/torn read; treat it as "no data" instead of firing off API
            // calls for maps/-1862258588 (the garbage-map-id spam in the log).
            if (rawMapId == 0 || rawMapId > 1_000_000) return null;

            // Camera-to-avatar distance (metres) = zoom level.
            float ax = _accessor.ReadSingle(OffsetAvatarPosX);
            float ay = _accessor.ReadSingle(OffsetAvatarPosY);
            float az = _accessor.ReadSingle(OffsetAvatarPosZ);
            float ccx = _accessor.ReadSingle(OffsetCameraPosX);
            float ccy = _accessor.ReadSingle(OffsetCameraPosY);
            float ccz = _accessor.ReadSingle(OffsetCameraPosZ);
            float ddx = ax - ccx, ddy = ay - ccy, ddz = az - ccz;
            float camDist = (float)Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);

            return new MumbleLinkData
            {
                MapId   = (int)rawMapId,
                PlayerX = _accessor.ReadSingle(OffsetPlayerX),
                PlayerY = _accessor.ReadSingle(OffsetPlayerY),
                AvatarX = ax, AvatarY = ay, AvatarZ = az,
                CameraX = ccx, CameraY = ccy, CameraZ = ccz,
                CameraFrontX = _accessor.ReadSingle(OffsetCameraFrontX),
                CameraFrontY = _accessor.ReadSingle(OffsetCameraFrontY),
                CameraFrontZ = _accessor.ReadSingle(OffsetCameraFrontZ),
                Fov = ReadFov(uiTick),
                CharacterName = _cachedName,
                FrontX  = _accessor.ReadSingle(OffsetAvatarFrontX),
                FrontY  = _accessor.ReadSingle(OffsetAvatarFrontZ),
                CameraDistance = camDist,
                UiState = _accessor.ReadUInt32(OffsetUiState),
                UiTick  = uiTick,
            };
        }
        catch (Exception ex)
        {
            // A read failure usually means the segment was torn down (GW2 closed).
            // Log once, return null — callers treat this as "GW2 not available".
            CrashLogger.Log("MumbleLinkReader.Read", ex);
            return null;
        }
    }

    private float _cachedFov = 0.873f;   // ~50° vertical, GW2's default-ish
    private string? _cachedName;         // current character name from identity JSON
    private uint _lastFovTick;

    /// <summary>Vertical field of view (radians) AND character name from the identity
    /// JSON. Parsed only occasionally (they barely change) rather than every frame.</summary>
    private float ReadFov(uint tick)
    {
        if (_cachedFov > 0 && tick - _lastFovTick < 30) return _cachedFov;
        _lastFovTick = tick;
        try
        {
            var bytes = new byte[512];
            _accessor!.ReadArray(OffsetIdentity, bytes, 0, 512);
            var json = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"fov\"\\s*:\\s*([0-9.]+)");
            if (m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f) && f is > 0.1f and < 3.5f)
                _cachedFov = f;
            var nm = System.Text.RegularExpressions.Regex.Match(json, "\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (nm.Success) _cachedName = nm.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        catch { }
        return _cachedFov;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accessor?.Dispose();
        _mmf?.Dispose();
        _accessor = null;
        _mmf = null;
    }
}

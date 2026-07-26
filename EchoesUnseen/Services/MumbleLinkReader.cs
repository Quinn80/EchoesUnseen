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

    // Byte offsets — constants documented in the MumbleLink spec
    private const int OffsetUiVersion = 0;
    private const int OffsetUiTick    = 4;
    private const int OffsetMapId     = 1112;
    private const int OffsetPlayerX   = 1144;
    private const int OffsetPlayerY   = 1148;

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

            return new MumbleLinkData
            {
                MapId   = (int)_accessor.ReadUInt32(OffsetMapId),
                PlayerX = _accessor.ReadSingle(OffsetPlayerX),
                PlayerY = _accessor.ReadSingle(OffsetPlayerY),
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

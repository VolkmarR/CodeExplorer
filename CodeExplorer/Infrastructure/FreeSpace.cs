using System.Globalization;

namespace CodeExplorer.Infrastructure;

/// <summary>
///     How much room is left where a path lives, which the free-space gate for a shadow index
///     (<c>ProjectIndexes.RoomForShadow</c>) and the repack of a local copy (<c>LocalCopyRepack</c>) both
///     ask about. Both used to ask <c>new DriveInfo(Path.GetPathRoot(path))</c>, which on Linux is
///     always <c>/</c>: in a container whose data directory is a mounted volume, that measured the
///     image's overlay instead of the disk the indexes and the clones fill (ADR-0003). Here in
///     <c>Infrastructure/</c> because two modules are handed it and no concept owns it (ADR-0005).
///     Also where the least free space a refresh is granted is read, since the repack keeps the same
///     margin and <c>Git/</c> cannot reach <c>Refresh/</c>.
/// </summary>
public static class FreeSpace
{
    public const string MinimumSetting = "Refresh:MinimumFreeBytes";

    /// <summary>
    ///     Default for <see cref="MinimumSetting" />, the least free space a refresh is granted; what a
    ///     shadow index costs beyond it is <c>ProjectIndexes.RoomForShadow</c>'s judgement. A project
    ///     with no index yet has nothing to scale from, so the floor stands in — 512 MiB out of the 8 GiB
    ///     ceiling ADR-0003 measured, which is a first build of a large repository and still leaves room
    ///     for the other projects. A repack keeps it free too, so upkeep of one copy never leaves a
    ///     refresh of any project refused for room.
    /// </summary>
    public const long DefaultMinimumBytes = 512L * 1024 * 1024;

    /// <summary>
    ///     A free-space figure as the refresh's refusal and the settle's skip both state it (#363). MiB,
    ///     not <c>ToolReply.Bytes</c>'s scaled MB: it sits next to the 8 GiB ceiling ADR-0003 documents,
    ///     and the two are only comparable in the same units.
    /// </summary>
    public static string Mib(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MiB");

    /// <summary>The configured <see cref="MinimumSetting" />, or its default.</summary>
    public static long Minimum(IConfiguration configuration) =>
        configuration.GetValue(MinimumSetting, DefaultMinimumBytes);

    /// <summary>
    ///     Bytes available to this process on the filesystem that holds <paramref name="path" />, which
    ///     need not exist yet.
    /// </summary>
    public static long Available(string path)
    {
        string full = Path.GetFullPath(path);
        var mounts = DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName);
        string root = MountHolding(full, mounts, OperatingSystem.IsWindows()
                          ? StringComparison.OrdinalIgnoreCase
                          : StringComparison.Ordinal)
                      ?? Path.GetPathRoot(full)!;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    /// <summary>
    ///     The mount point that holds <paramref name="path" />: the longest of
    ///     <paramref name="mounts" /> that it is, or lies under, compared a whole path segment at a time
    ///     so <c>/data</c> never claims <c>/database</c>. On Linux <see cref="DriveInfo.GetDrives" />
    ///     lists every mount point, <c>/</c> included, so the answer is the volume a path is really
    ///     written to. On Windows it lists the drive letters only, and a volume mounted into a folder
    ///     is measured as the drive holding that folder; no deployment here mounts one (the IIS guide
    ///     puts the data directory on a drive of its own). Null when nothing holds it.
    /// </summary>
    internal static string? MountHolding(string path, IEnumerable<string> mounts, StringComparison comparison)
    {
        string? best = null;
        int longest = -1;
        foreach (string mount in mounts)
        {
            // Without its trailing separator, except for a root that is nothing else: "/" and "C:\".
            string trimmed = mount.TrimEnd('/', '\\');
            if (trimmed.Length == 0 || trimmed.EndsWith(':')) trimmed = mount;
            bool root = trimmed.EndsWith('/') || trimmed.EndsWith('\\');
            bool holds = path.Equals(trimmed, comparison)
                         || (path.StartsWith(trimmed, comparison)
                             && (root || path[trimmed.Length] is '/' or '\\'));
            if (!holds || trimmed.Length <= longest) continue;
            best = mount;
            longest = trimmed.Length;
        }

        return best;
    }
}

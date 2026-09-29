using System.Buffers.Binary;
using System.Diagnostics;
using CodeExplorer.Infrastructure;
using LibGit2Sharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer.Git;

/// <summary>
///     Folds a local copy's packs and loose objects back into one pack once fetches have left too many
///     of them. Every fetch adds a pack and libgit2 has no gc, so since ADR-0007 made the copies full and
///     kept, a copy only ever fragments: a real one of 4,380 commits had grown to 353 packs and 41,344
///     loose objects in 457 MiB, and repacked it was one pack of 250 MiB that read every blob at HEAD in
///     4.0 s instead of 5.7 s and walked its history with tree diffs in 36.7 s instead of 47.9 s.
///     Written with <see cref="ObjectDatabase.Pack(PackBuilderOptions)" /> alone, never the git CLI
///     (CODING_STANDARDS, Git), and run only by <see cref="GitClones" /> under the clone's gate, right
///     after a fetch: nothing else transfers into the copy meanwhile, and no <see cref="LocalCopy" /> of
///     it is open, because a refresh opens one only after its transfer returns and refreshes run one at
///     a time. That matters on Windows, where libgit2 maps every pack it reads and a mapped file cannot
///     be deleted. A first clone is never repacked: it arrives as one pack.
///     The order of the swap is what keeps every object reachable at every instant, since a delete that
///     failed half-way through the old packs, with the new one not yet in place, was measured to leave a
///     copy missing objects: the new pack is written outside <c>objects/pack</c>, moved in, checked, and
///     only then are the old packs and the loose objects deleted. A failure before the check leaves the
///     copy as it was; one after it leaves a valid copy with some old files still beside the new pack,
///     which the next repack deletes. Nothing a repack does fails the refresh it runs in.
/// </summary>
internal sealed class LocalCopyRepack
{
    public const string PackThresholdSetting = "Git:RepackPackThreshold";

    public const string LooseObjectThresholdSetting = "Git:RepackLooseObjectThreshold";

    /// <summary>
    ///     Fifty packs. A repack of the measured copy took minutes, so it must be rare, and a copy with
    ///     fifty packs is one a lookup already searches fifty indexes for; on a server that refreshes
    ///     daily that is a repack every couple of months.
    /// </summary>
    public const int DefaultPackThreshold = 50;

    /// <summary>
    ///     Five thousand loose objects, an eighth of what the measured copy had collected: one file per
    ///     object, zlib'd alone with no delta, costs a directory lookup and an open per read.
    /// </summary>
    public const int DefaultLooseObjectThreshold = 5000;

    /// <summary>
    ///     Inside the copy's own folder and outside <c>objects</c>, so a pack written here is on the same
    ///     volume and moves in by rename, is counted by <see cref="GitClones.Footprint" /> while it
    ///     exists, goes when the copy is removed, and is never read by libgit2 as part of the copy.
    /// </summary>
    internal const string StagingDirectoryName = "codeexplorer-repack";

    // What git writes beside a pack, all named after it. libgit2 writes only the .pack and the .idx,
    // but a copy the git CLI has touched carries the rest — the measured one had .rev and .mtimes —
    // and each describes a pack that is about to be gone. The .idx is first: it is what makes a pack
    // visible to libgit2, so removing it before the .pack never leaves an index naming a missing file.
    private static readonly string[] _companions = [".idx", ".pack", ".rev", ".bitmap", ".mtimes", ".promisor"];

    private readonly int _packThreshold;
    private readonly int _looseObjectThreshold;
    private readonly ILogger _logger;

    public LocalCopyRepack(IConfiguration configuration, ILogger logger)
    {
        _packThreshold = Threshold(configuration, PackThresholdSetting, DefaultPackThreshold);
        _looseObjectThreshold = Threshold(configuration, LooseObjectThresholdSetting, DefaultLooseObjectThreshold);
        _logger = logger;
    }

    /// <summary>
    ///     A zero or a negative count would repack after every fetch or never mean anything, and the
    ///     operator who wrote it meant neither; an unreachable number is how a repack is switched off.
    /// </summary>
    private static int Threshold(IConfiguration configuration, string setting, int fallback)
    {
        int value = configuration.GetValue(setting, fallback);
        if (value <= 0)
            throw new InvalidOperationException(
                $"{setting} is {value}, but must be a count between 1 and {int.MaxValue}.");
        return value;
    }

    /// <summary>
    ///     Repacks the copy at <paramref name="path" /> when it holds more packs or more loose objects
    ///     than configured, and otherwise only clears a staging folder a crashed repack left. Never
    ///     throws: a copy that could not be repacked is still the copy the fetch just brought up to date.
    /// </summary>
    /// <param name="repository">The repository, which the log lines name.</param>
    /// <param name="path">The local copy, which nothing may hold open.</param>
    /// <param name="cancellationToken">
    ///     The refresh's, which is the application's stopping token. Asked once, before the pack is
    ///     written, because <c>Pack</c> itself takes no token and cannot be stopped once started. A
    ///     process killed under it leaves the copy as it was and a staging folder the next fetch clears.
    /// </param>
    public void RunIfDue(ProjectRepository repository, string path, CancellationToken cancellationToken)
    {
        string objects = Path.Combine(path, "objects");
        string staging = Path.Combine(path, StagingDirectoryName);
        try
        {
            // After every fetch and not only a due one: a staging folder is a whole pack's worth of disk,
            // and the copy may not cross a threshold again for months.
            DeleteDirectory(staging);

            int packs = PackCount(objects);
            int loose = LooseObjectCount(objects);
            if (packs <= _packThreshold && loose <= _looseObjectThreshold) return;
            if (cancellationToken.IsCancellationRequested) return;

            // The new pack is written beside the old objects, so for a moment the copy needs room for
            // both. It is at most about what the objects occupy now — the measured copy packed 457 MiB
            // into 250 — so that is what must be free. The free-space gate does not reserve it: it
            // sizes a refresh by what it keeps, and a repack gives back more than it borrows (ADR-0007).
            long occupied = Size(objects);
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            if (free < occupied)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("The local copy of repository {Repository} of project {Project} at {Path} "
                                       + "holds {Packs} packs and {Loose} loose objects but was not repacked: "
                                       + "that needs up to {Needed} bytes free and {Free} are", repository.Slug,
                        repository.ProjectSlug, path, packs, loose, occupied, free);
                return;
            }

            Repack(repository, path, objects, staging, packs, loose);
        }
        catch (Exception ex)
        {
            // Safe to swallow: a repack is upkeep, and the fetch it follows has already succeeded, so the
            // copy is current either way. Every failure before the new pack is checked leaves the copy as
            // it was (Repack), and the log line is how an operator learns it keeps fragmenting.
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(ex, "The local copy of repository {Repository} of project {Project} at {Path} "
                                       + "could not be repacked and is left as it was", repository.Slug,
                    repository.ProjectSlug, path);
            TryDeleteDirectory(staging);
        }
    }

    private void Repack(ProjectRepository repository, string path, string objects, string staging, int packs,
        int loose)
    {
        var watch = Stopwatch.StartNew();
        string packDirectory = Path.Combine(objects, "pack");
        // Taken before anything is written: every object in these is in the pack about to be written,
        // which is what makes them safe to delete once it is in place.
        string[] old = Directory.Exists(packDirectory) ? Directory.GetFiles(packDirectory) : [];

        Directory.CreateDirectory(staging);
        long written;
        // Disposed before anything moves: libgit2 maps the packs it reads, and Windows refuses to delete
        // a mapped file. MaximumNumberOfThreads stays at its 0, which libgit2 reads as one thread per
        // core for the delta search: on the measured copy that was about two minutes on 20 cores against
        // 159 s on one, with the same pack and the same 1.4 GiB peak, so there is nothing to tune.
        using (var clone = new Repository(path))
            written = clone.ObjectDatabase.Pack(new PackBuilderOptions(staging)).WrittenObjectsCount;

        string pack = Directory.GetFiles(staging, "*.pack").Single();
        string index = Path.ChangeExtension(pack, ".idx");
        string name = Path.GetFileNameWithoutExtension(pack);
        var placed = new List<string>();
        try
        {
            // The .pack before the .idx, because the index is what makes libgit2 read the pack.
            Place(pack, packDirectory, placed);
            Place(index, packDirectory, placed);
            Verify(path, Path.Combine(packDirectory, name), written);
        }
        catch
        {
            // Taken back out, index first, so a pack that failed its check is never read in place of the
            // old ones, which are all still there. A file that will not go is left to the log line the
            // caller writes for the failure this rethrows, which is the one worth reading.
            var ignored = new List<Exception>();
            foreach (string file in Enumerable.Reverse(placed)) TryDelete(file, ignored);
            throw;
        }

        // Empty by now, or holding only a file that was already in place, and a folder that will not go
        // is cleared by the next fetch, so it is no reason to report the repack as failed.
        TryDeleteDirectory(staging);
        var failures = RemoveOld(old, name, objects);
        if (failures.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(failures[0], "The local copy of repository {Repository} of project {Project} at "
                                                + "{Path} was repacked, but {Count} of its old files could not be "
                                                + "deleted; the copy is whole, and a later repack deletes them",
                    repository.Slug, repository.ProjectSlug, path, failures.Count);
            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Repacked the local copy of repository {Repository} of project {Project}: "
                                   + "{Packs} packs and {Loose} loose objects into one pack of {Objects} objects in "
                                   + "{Seconds:0.0} s", repository.Slug, repository.ProjectSlug, packs, loose, written,
                watch.Elapsed.TotalSeconds);
    }

    /// <summary>
    ///     Moves one staged file into <c>objects/pack</c> by rename. A file already there under the same
    ///     name is the same file, because a pack is named after the checksum of its content — a repack
    ///     that died after the move and is run again on the same objects writes it again — and it stays.
    /// </summary>
    private static void Place(string staged, string packDirectory, List<string> placed)
    {
        string target = Path.Combine(packDirectory, Path.GetFileName(staged));
        if (File.Exists(target)) return;
        File.Move(staged, target);
        placed.Add(target);
    }

    /// <summary>
    ///     Refuses a pack that is not whole before the old ones go. Cheap, because the copy can hold
    ///     millions of objects: the pack's header and its index's fan-out table must both count what the
    ///     builder wrote, the pack must end in the checksum its index recorded for it, so a truncated
    ///     pack fails, and the copy must still open and resolve HEAD's commit and tree with it in place.
    /// </summary>
    private static void Verify(string path, string pack, long written)
    {
        using (var handle = File.OpenHandle(pack + ".pack"))
        using (var indexHandle = File.OpenHandle(pack + ".idx"))
        {
            // A version 2 index: magic, version, then 256 cumulative counts, the last being the total.
            // It ends with the pack's checksum and then its own.
            Span<byte> header = stackalloc byte[12];
            RandomAccess.Read(handle, header, 0);
            Span<byte> fanOut = stackalloc byte[4];
            RandomAccess.Read(indexHandle, fanOut, 8 + (255 * 4));
            Span<byte> recorded = stackalloc byte[20];
            RandomAccess.Read(indexHandle, recorded, RandomAccess.GetLength(indexHandle) - 40);
            Span<byte> trailer = stackalloc byte[20];
            RandomAccess.Read(handle, trailer, RandomAccess.GetLength(handle) - 20);

            if (!header[..4].SequenceEqual("PACK"u8)
                || BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != (uint)written
                || BinaryPrimitives.ReadUInt32BigEndian(fanOut) != (uint)written
                || !trailer.SequenceEqual(recorded))
                throw new InvalidDataException(
                    $"The new pack does not hold the {written} objects the pack builder wrote, or is incomplete.");
        }

        using var clone = new Repository(path);
        if (clone.Head.Tip is { } tip && clone.Lookup<Tree>(tip.Tree.Id) is null)
            throw new InvalidDataException("The local copy cannot resolve HEAD's tree with the new pack in place.");
    }

    /// <summary>
    ///     Deletes what the new pack now holds: the packs listed before it was written, bar a pack marked
    ///     <c>.keep</c>, which whoever marked it wants kept, and the loose objects. Carries on past a file
    ///     that will not go — on Windows, one something still has open — and answers every failure, since
    ///     each object is in the new pack whatever is left beside it.
    /// </summary>
    private static List<Exception> RemoveOld(string[] old, string repacked, string objects)
    {
        var failures = new List<Exception>();
        var kept = old.Where(file => file.EndsWith(".keep", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        // A multi-pack index describes the old packs and nothing else. libgit2 stops using one whose
        // packs are missing, so it goes first, and no reader ever meets it half-true.
        foreach (string file in old.Where(file => Path.GetFileName(file).StartsWith("multi-pack-index",
                     StringComparison.Ordinal)))
            TryDelete(file, failures);

        // Any name and not only pack-*: git maintenance's loose-objects task writes loose-*.pack, and
        // the measured copy held four of them, one of 106 MiB, which a repack that looked only for
        // libgit2's names left beside the new pack. Only the kinds git writes beside a pack are
        // touched, so a temporary file of an interrupted fetch is never mistaken for one.
        var packs = old
            .Where(file => Array.IndexOf(_companions, Path.GetExtension(file)) >= 0
                           && !Path.GetFileName(file).StartsWith("multi-pack-index", StringComparison.Ordinal))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
            .Where(group => group.Key != repacked && !kept.Contains(group.Key));
        foreach (var files in packs)
        foreach (string file in files.OrderBy(file => Array.IndexOf(_companions, Path.GetExtension(file))))
            TryDelete(file, failures);

        foreach (string directory in LooseDirectories(objects))
        {
            try
            {
                DeleteDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Safe to swallow: answered as a failure, and every object in it is in the new pack.
                failures.Add(ex);
            }
        }

        return failures;
    }

    private static void TryDelete(string file, List<Exception> failures)
    {
        try
        {
            DeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Safe to swallow: answered as a failure, and the file's objects are in the new pack.
            failures.Add(ex);
        }
    }

    /// <summary>More packs than configured, not counting one marked <c>.keep</c>, which a repack leaves.</summary>
    private static int PackCount(string objects)
    {
        string packDirectory = Path.Combine(objects, "pack");
        return Directory.Exists(packDirectory)
            ? Directory.EnumerateFiles(packDirectory, "*.pack")
                .Count(pack => !File.Exists(Path.ChangeExtension(pack, ".keep")))
            : 0;
    }

    /// <summary>One file per object in the 256 fan-out folders, counted without being opened.</summary>
    private static int LooseObjectCount(string objects) =>
        LooseDirectories(objects).Sum(directory => Directory.EnumerateFiles(directory).Count());

    private static List<string> LooseDirectories(string objects) =>
        Directory.Exists(objects)
            ? Directory.EnumerateDirectories(objects)
                .Where(directory => Path.GetFileName(directory) is [var high, var low]
                                    && char.IsAsciiHexDigit(high) && char.IsAsciiHexDigit(low))
                .ToList()
            : [];

    private static long Size(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

    /// <summary>
    ///     libgit2 writes packs and loose objects read-only, and Windows refuses to delete a read-only
    ///     file, so the attribute is cleared first.
    /// </summary>
    private static void DeleteFile(string file)
    {
        if (!File.Exists(file)) return;
        File.SetAttributes(file, FileAttributes.Normal);
        File.Delete(file);
    }

    private static void DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(directory, true);
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            DeleteDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Safe to swallow: the folder is outside objects, so libgit2 never reads it, and the next
            // fetch tries again before it counts anything.
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(ex, "Could not remove the repack staging folder {Path}", directory);
        }
    }
}

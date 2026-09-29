using System.Buffers.Binary;
using System.Diagnostics;
using CodeExplorer.Infrastructure;
using LibGit2Sharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace CodeExplorer.Git;

/// <summary>
///     Folds a local copy's small packs and its loose objects into one pack once fetches have left too
///     many of them. Every fetch adds a pack and libgit2 has no gc, so since ADR-0007 made the copies full
///     and kept, a copy only ever fragments: a real one of 4,380 commits had grown to 353 packs and 41,344
///     loose objects in 457 MiB, and repacked it was one pack of 242 MiB that read every blob at HEAD in
///     4.0 s instead of 5.7 s and walked its history with tree diffs in 36.7 s instead of 47.9 s.
///     Which packs are folded is git's <c>repack --geometric=2</c> rule (<see cref="Plan" />): the large
///     packs that already hold most of the history stay, and only what was fetched since they were
///     written is folded. The first repack of a fragmented copy therefore folds everything — the
///     measured copy's largest pack held 145,575 objects, less than twice the rest — and each later one
///     costs what was fetched since, not the whole history again.
///     Written with <see cref="ObjectDatabase.Pack(PackBuilderOptions, Action{PackBuilder})" /> alone,
///     never the git CLI (CODING_STANDARDS, Git), and run only by <see cref="GitClones" /> under the
///     clone's gate, right after a fetch: nothing else transfers into the copy meanwhile, and no
///     <see cref="LocalCopy" /> of it is open, because a refresh opens one only after its transfer
///     returns and refreshes run one at a time. That matters on Windows, where libgit2 maps every pack
///     it reads and a mapped file cannot be deleted. A first clone is never repacked: it arrives as one
///     pack.
///     The order of the swap is what keeps every object reachable at every instant, since a delete that
///     failed half-way through the old packs, with the new one not yet in place, was measured to leave a
///     copy missing objects: the new pack is written outside <c>objects/pack</c>, moved in, checked, and
///     only then are the folded packs and the loose objects deleted. A failure before the check leaves
///     the copy as it was; one after it leaves a valid copy with some old files still beside the new
///     pack, which the next repack folds. Nothing a repack does fails the refresh it runs in.
/// </summary>
internal sealed class LocalCopyRepack
{
    public const string PackThresholdSetting = "Git:RepackPackThreshold";

    public const string LooseObjectThresholdSetting = "Git:RepackLooseObjectThreshold";

    /// <summary>
    ///     Fifty packs. The first repack of the measured copy took over a minute, so it must be rare, and
    ///     a copy with fifty packs is one a lookup already searches fifty indexes for; on a server that
    ///     refreshes daily that is a repack every couple of months.
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

    /// <summary>
    ///     git's <c>--geometric=2</c>: a pack stays while it holds at least twice what is smaller than it,
    ///     so the packs that stay are a progression in which each is at least double the next, and a copy
    ///     holds a logarithmic number of them however long it is fetched into.
    /// </summary>
    private const int _geometricFactor = 2;

    // A version 2 pack index: magic and version, 256 cumulative counts whose last is the total, then
    // that many 20-byte object names in sorted order.
    private static ReadOnlySpan<byte> IndexMagic => [0xff, (byte)'t', (byte)'O', (byte)'c', 0, 0, 0, 2];
    private const int _fanOutOffset = 8;
    private const int _namesOffset = _fanOutOffset + (256 * 4);
    private const int _idLength = 20;

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
            LocalCopyFiles.DeleteDirectory(staging);

            int packs = PackCount(objects);
            int loose = LooseObjectCount(objects);
            if (packs <= _packThreshold && loose <= _looseObjectThreshold) return;
            if (cancellationToken.IsCancellationRequested) return;

            var plan = Plan(objects, loose);

            // The new pack is written beside the old objects, so for a moment the copy needs room for
            // both. It is at most about what the folded packs and the loose objects occupy now — the
            // measured copy packed 457 MiB into 242 — so that is what must be free. The free-space gate
            // does not reserve it: it sizes a refresh by what it keeps, and a repack gives back more
            // than it borrows (ADR-0007).
            long folded = plan.Folded.Sum(pack => FileLength(pack + ".pack"))
                          + LooseDirectories(objects).Sum(Size);
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            if (free < folded)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("The local copy of repository {Repository} of project {Project} at {Path} "
                                       + "holds {Packs} packs and {Loose} loose objects but was not repacked: "
                                       + "that needs up to {Needed} bytes free and {Free} are", repository.Slug,
                        repository.ProjectSlug, path, packs, loose, folded, free);
                return;
            }

            Repack(repository, path, objects, staging, plan, loose);
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

    /// <summary>
    ///     Which packs to fold, as paths without their extension, and how many stay. Walking down from
    ///     the pack holding the most objects, a pack stays while it holds at least
    ///     <see cref="_geometricFactor" /> times everything smaller than it, the loose objects included;
    ///     the first that does not, and every pack below it, is folded. Counted from each index's
    ///     fan-out, so an object two packs share counts twice, as it does for git.
    ///     A split that would leave more packs than the threshold even after the fold — packs that each
    ///     double the next, past the threshold — folds everything instead, or the copy would be due again
    ///     after the very next fetch and never stop repacking. A pack marked <c>.keep</c> is neither
    ///     counted nor folded, and a <c>.pack</c> without its index, which libgit2 cannot see and a
    ///     delete that failed half-way can leave, holds nothing to fold and is folded, which deletes it.
    /// </summary>
    private (List<string> Folded, int Kept) Plan(string objects, int loose)
    {
        string packDirectory = Path.Combine(objects, "pack");
        var packs = Directory.EnumerateFiles(packDirectory, "*.pack")
            .Where(pack => !File.Exists(Path.ChangeExtension(pack, ".keep")))
            .Select(pack => Path.ChangeExtension(pack, null))
            .Select(pack => (Path: pack, Objects: File.Exists(pack + ".idx") ? IndexCount(pack + ".idx") : 0L))
            .OrderByDescending(pack => pack.Objects)
            .ToList();

        long smaller = packs.Sum(pack => pack.Objects) + loose;
        int kept = 0;
        while (kept < packs.Count)
        {
            smaller -= packs[kept].Objects;
            if (packs[kept].Objects < _geometricFactor * smaller) break;
            kept++;
        }

        // The kept packs and the new one must come in at or under the threshold.
        if (kept + 1 > _packThreshold) kept = 0;
        return (packs.Skip(kept).Select(pack => pack.Path).ToList(), kept);
    }

    private void Repack(ProjectRepository repository, string path, string objects, string staging,
        (List<string> Folded, int Kept) plan, int loose)
    {
        var watch = Stopwatch.StartNew();
        string packDirectory = Path.Combine(objects, "pack");
        // Taken before anything is written: every object in the folded packs listed here is in the pack
        // about to be written, which is what makes them safe to delete once it is in place.
        string[] old = Directory.GetFiles(packDirectory);

        Directory.CreateDirectory(staging);
        var added = new HashSet<ObjectKey>();
        long written;
        // The objects are named one by one, from the folded packs' indexes and the loose objects' file
        // names, rather than left to Pack(options), which enumerates the whole object database: that
        // meets every object once per pack holding it — 636,445 index entries and 41,344 loose files for
        // the measured copy's 256,719 objects — and it also takes the kept packs' objects along. Measured
        // on that copy, with the same 242 MiB pack written, it peaked at 1,097 MiB of private memory and
        // took 112 s; naming them took 616 MiB and 76 s. Each object is added once, which the set checks
        // for a few MiB, so what the builder wrote can be held to it below.
        // Disposed before anything moves: libgit2 maps the packs it reads, and Windows refuses to delete
        // a mapped file. MaximumNumberOfThreads stays at its 0, which libgit2 reads as one thread per
        // core for the delta search; one thread took half as long again with the same memory.
        using (var clone = new Repository(path))
            written = clone.ObjectDatabase.Pack(new PackBuilderOptions(staging), builder =>
            {
                foreach (string pack in plan.Folded.Where(pack => File.Exists(pack + ".idx")))
                    AddIndexed(pack + ".idx", added, builder);
                AddLoose(objects, added, builder);
            }).WrittenObjectsCount;

        if (written != added.Count)
            throw new InvalidDataException(
                $"The pack builder wrote {written} objects where {added.Count} were named to it.");

        string? name = null;
        var placed = new List<string>();
        if (written > 0)
        {
            string pack = Directory.GetFiles(staging, "*.pack").Single();
            string index = Path.ChangeExtension(pack, ".idx");
            name = Path.GetFileNameWithoutExtension(pack);
            try
            {
                // The .pack before the .idx, because the index is what makes libgit2 read the pack.
                Place(pack, packDirectory, placed);
                Place(index, packDirectory, placed);
                Verify(path, Path.Combine(packDirectory, name), written);
            }
            catch
            {
                // Taken back out, index first, so a pack that failed its check is never read in place of
                // the old ones, which are all still there. A file that will not go is left to the log line
                // the caller writes for the failure this rethrows, which is the one worth reading.
                var ignored = new List<Exception>();
                foreach (string file in Enumerable.Reverse(placed)) TryDelete(file, ignored);
                throw;
            }
        }

        // Empty by now, or holding only a file that was already in place, and a folder that will not go
        // is cleared by the next fetch, so it is no reason to report the repack as failed.
        TryDeleteDirectory(staging);
        var folded = plan.Folded.Select(pack => Path.GetFileName(pack)).Where(pack => pack != name)
            .ToHashSet(StringComparer.Ordinal);
        var failures = RemoveOld(old, folded, objects);
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
                                   + "{Packs} packs and {Loose} loose objects into one pack of {Objects} objects, "
                                   + "{Kept} larger packs kept, in {Seconds:0.0} s", repository.Slug,
                repository.ProjectSlug, plan.Folded.Count, loose, written, plan.Kept, watch.Elapsed.TotalSeconds);
    }

    /// <summary>
    ///     How many objects a pack's index lists, from the last entry of its fan-out table. Only a
    ///     version 2 index is read: libgit2 and every git since 1.5.2 write nothing else, and a version 1
    ///     index, which has no magic, lays its names out differently. One is refused rather than kept
    ///     aside, because a kept pack still counts toward the threshold and the copy would be due again
    ///     after every fetch; refused, the repack fails and says so once per fetch in the log, and the
    ///     copy stays as it is.
    /// </summary>
    private static long IndexCount(string index)
    {
        using var handle = File.OpenHandle(index);
        Span<byte> header = stackalloc byte[_namesOffset];
        if (RandomAccess.Read(handle, header, 0) != _namesOffset || !header[..IndexMagic.Length].SequenceEqual(IndexMagic))
            throw new InvalidDataException($"{Path.GetFileName(index)} is not a version 2 pack index.");
        return BinaryPrimitives.ReadUInt32BigEndian(header[(_namesOffset - 4)..]);
    }

    /// <summary>Names every object an index lists to the builder, read in blocks rather than whole.</summary>
    private static void AddIndexed(string index, HashSet<ObjectKey> added, PackBuilder builder)
    {
        long count = IndexCount(index);
        using var stream = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        stream.Position = _namesOffset;
        var id = new byte[_idLength];
        for (long i = 0; i < count; i++)
        {
            stream.ReadExactly(id);
            Add(id, added, builder);
        }
    }

    /// <summary>
    ///     Names every loose object, whose id is its folder's two hex digits and its file's thirty-eight.
    ///     A file named otherwise is a temporary one of an interrupted write and holds no object.
    /// </summary>
    private static void AddLoose(string objects, HashSet<ObjectKey> added, PackBuilder builder)
    {
        var id = new byte[_idLength];
        foreach (string directory in LooseDirectories(objects))
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(directory) + Path.GetFileName(file);
            if (name.Length != _idLength * 2 || !name.All(char.IsAsciiHexDigit)) continue;
            Convert.FromHexString(name, id, out _, out _);
            Add(id, added, builder);
        }
    }

    /// <summary>
    ///     Adds an object the first time it is named. The builder would drop a repeat itself, but an
    ///     ObjectId built for each of the measured copy's 636,445 index entries is garbage the set spares.
    /// </summary>
    private static void Add(byte[] id, HashSet<ObjectKey> added, PackBuilder builder)
    {
        if (added.Add(ObjectKey.Of(id))) builder.Add(new ObjectId(id.ToArray()));
    }

    /// <summary>A 20-byte object id as a value, so a set of a quarter of a million of them costs a few MiB.</summary>
    private readonly record struct ObjectKey(ulong First, ulong Second, uint Third)
    {
        public static ObjectKey Of(ReadOnlySpan<byte> id) =>
            new(BinaryPrimitives.ReadUInt64LittleEndian(id), BinaryPrimitives.ReadUInt64LittleEndian(id[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(id[16..]));
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
            // The index ends with the pack's checksum and then its own.
            Span<byte> header = stackalloc byte[12];
            RandomAccess.Read(handle, header, 0);
            Span<byte> recorded = stackalloc byte[_idLength];
            RandomAccess.Read(indexHandle, recorded, RandomAccess.GetLength(indexHandle) - (2 * _idLength));
            Span<byte> trailer = stackalloc byte[_idLength];
            RandomAccess.Read(handle, trailer, RandomAccess.GetLength(handle) - _idLength);

            if (!header[..4].SequenceEqual("PACK"u8)
                || BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != (uint)written
                || IndexCount(pack + ".idx") != written
                || !trailer.SequenceEqual(recorded))
                throw new InvalidDataException(
                    $"The new pack does not hold the {written} objects the pack builder wrote, or is incomplete.");
        }

        using var clone = new Repository(path);
        if (clone.Head.Tip is { } tip && clone.Lookup<Tree>(tip.Tree.Id) is null)
            throw new InvalidDataException("The local copy cannot resolve HEAD's tree with the new pack in place.");
    }

    /// <summary>
    ///     Deletes what the new pack now holds: the folded packs, each with the files git writes beside
    ///     it, and the loose objects. The kept packs stay, and so does a pack marked <c>.keep</c>, which
    ///     whoever marked it wants kept. Carries on past a file that will not go — on Windows, one
    ///     something still has open — and answers every failure, since each object is in the new pack or
    ///     a kept one whatever is left beside them.
    /// </summary>
    /// <param name="old">What <c>objects/pack</c> held before the new pack was written.</param>
    /// <param name="folded">The folded packs' names without their extension.</param>
    /// <param name="objects">The copy's object folder, whose loose objects go.</param>
    private static List<Exception> RemoveOld(string[] old, HashSet<string> folded, string objects)
    {
        var failures = new List<Exception>();

        // A multi-pack index describes the old packs, some of which are about to be gone. libgit2 stops
        // using one whose packs are missing, so it goes first, and no reader ever meets it half-true.
        foreach (string file in old.Where(file => Path.GetFileName(file).StartsWith("multi-pack-index",
                     StringComparison.Ordinal)))
            TryDelete(file, failures);

        // By name and not only pack-*: git maintenance's loose-objects task writes loose-*.pack, and the
        // measured copy held four of them, one of 106 MiB. Only the kinds git writes beside a pack are
        // touched, so a temporary file of an interrupted fetch is never mistaken for one.
        var packs = old
            .Where(file => Array.IndexOf(_companions, Path.GetExtension(file)) >= 0)
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
            .Where(group => folded.Contains(group.Key!));
        foreach (var files in packs)
        foreach (string file in files.OrderBy(file => Array.IndexOf(_companions, Path.GetExtension(file))))
            TryDelete(file, failures);

        foreach (string directory in LooseDirectories(objects))
        {
            try
            {
                LocalCopyFiles.DeleteDirectory(directory);
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
            LocalCopyFiles.DeleteFile(file);
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

    private static long FileLength(string file) => File.Exists(file) ? new FileInfo(file).Length : 0;

    private static long Size(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            LocalCopyFiles.DeleteDirectory(directory);
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

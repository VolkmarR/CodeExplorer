using System.Runtime.InteropServices;
using LibGit2Sharp;

namespace CodeExplorer.Git;

/// <summary>
///     A git object id as a value, so it can key a dictionary without a byte array per entry. Twenty
///     bytes of SHA-1, which is what the bundled libgit2 is built for (see <see cref="BlobReader" />),
///     laid out as libgit2's <c>git_oid</c> so a native struct can hold one and a call can take one.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct GitId(long Head, long Middle, int Tail);

/// <summary>
///     A commit's diff through libgit2 directly, because LibGit2Sharp's cost a first history import a
///     tenth more once the ceiling needed the changed paths sized before the patch (#263, #292).
///     LibGit2Sharp has no way to name the changes without rendering them, so the ceiling walked the
///     trees once for itself and the patch walked them again. Here one tree diff names the changes,
///     each blob is sized off its object header, and only then are renames detected and patches made,
///     from the same diff — so the trees are walked once, and a blob over the ceiling is never loaded.
///     The edits are read from the hunks and not from rendered text. With no context lines each hunk
///     is exactly one edit — the run of lines it removes and the run it adds — so its header numbers
///     are the whole answer, and not a line of content crosses into managed code. LibGit2Sharp copied
///     every line into three strings, and <see cref="UnifiedDiff.Edits" /> then threw them away.
///     The options are LibGit2Sharp's, so what is recorded is what its patch recorded: type changes
///     included, renames by the repository's <c>diff.renames</c>, the default Myers diff.
///     Sizes are kept by blob id for the whole walk, because each blob version is asked for twice: as
///     the old side of the commit that replaced it and, further down a newest-first walk, as the new
///     side of the commit that wrote it. One entry per blob version seen, fewer than the changes the
///     history pass holds in memory anyway.
/// </summary>
internal sealed class NativeDiff : IDisposable
{
    private const uint _includeTypeChange = 0x40; // GIT_DIFF_INCLUDE_TYPECHANGE, which LibGit2Sharp always sets
    private const uint _binary = 0x1; // GIT_DIFF_FLAG_BINARY
    private const int _submoduleMode = 0xE000; // 0160000: a commit of another repository, never sized

    private readonly NativeRepository _repository;
    private readonly Dictionary<GitId, long> _sizes = [];
    private readonly DiffOptions _options;
    private readonly DiffOptions _withoutOversized;
    // Held in a field for as long as libgit2 may call it: a collected delegate is a crash, not an error.
    private readonly Notify _skipOversized;
    private long _ceiling;
    private nint _odb;

    public NativeDiff(NativeRepository repository)
    {
        _repository = repository;
        var options = new DiffOptions();
        NativeRepository.Check(git_diff_options_init(ref options, 1), "initialise diff options");
        options.Flags |= _includeTypeChange;
        // No context lines, for the reason LocalCopy gives; it is also what makes a hunk one edit.
        options.ContextLines = 0;
        options.InterhunkLines = 0;
        _options = options;
        _skipOversized = SkipOversized;
        options.NotifyCallback = Marshal.GetFunctionPointerForDelegate(_skipOversized);
        _withoutOversized = options;
        // Last, so nothing after it can throw and leave it open.
        NativeRepository.Check(git_repository_odb(out _odb, repository), "open the object database");
    }

    /// <summary>
    ///     The changes from <paramref name="before" /> to <paramref name="after" />, a null
    ///     <paramref name="before" /> being the empty tree, as the history records them.
    ///     A change with a blob over <paramref name="maxBlobBytes" /> on either side is recorded without
    ///     lines, the way the file pass treats that blob. Rename detection reads the blobs it compares,
    ///     so a commit holding one is diffed a second time with those changes left out by libgit2 itself,
    ///     and only the rest is patched. It is the rare commit, and the second diff is its only extra cost.
    /// </summary>
    public List<ChangedPath> Diff(ObjectId? before, ObjectId after, long maxBlobBytes)
    {
        _ceiling = maxBlobBytes;
        List<Oversized>? oversized = null;
        nint diff = TreeToTree(before, after, _options);
        try
        {
            int count = checked((int)git_diff_num_deltas(diff));
            for (int index = 0; index < count; index++)
            {
                var delta = Marshal.PtrToStructure<Delta>(git_diff_get_delta(diff, (nuint)index));
                var kind = (ChangeKind)delta.Status;
                long oldSize = kind == ChangeKind.Added ? 0 : Size(delta.Old);
                long newSize = kind == ChangeKind.Deleted ? 0 : Size(delta.New);
                if (oldSize > maxBlobBytes || newSize > maxBlobBytes)
                    (oversized ??= []).Add(new Oversized(Marshal.PtrToStringUTF8(delta.New.Path)!, kind,
                        kind == ChangeKind.Added ? null : delta.Old.Id, kind == ChangeKind.Deleted ? null : delta.New.Id));
            }

            if (oversized is null) return Record(diff);
        }
        finally
        {
            git_diff_free(diff);
        }

        var files = Record(oversized);
        diff = TreeToTree(before, after, _withoutOversized);
        try
        {
            files.AddRange(Record(diff));
            return files;
        }
        finally
        {
            git_diff_free(diff);
        }
    }

    public void Dispose()
    {
        if (_odb == 0) return;
        git_odb_free(_odb);
        _odb = 0;
    }

    /// <summary>
    ///     libgit2's notify callback for the second diff of a commit with a blob over the ceiling: a
    ///     positive answer leaves the delta out of the diff. Per delta, so a file that replaced a
    ///     directory holding such a blob stays in while the blob goes; naming the rest to LibGit2Sharp
    ///     as paths matched the directory's contents as a prefix of the file and diffed the blob. The
    ///     sizes are the first diff's, already cached, so this reads no header and cannot throw.
    /// </summary>
    private int SkipOversized(nint diff, nint delta, nint pathspec, nint payload)
    {
        var read = Marshal.PtrToStructure<Delta>(delta);
        var kind = (ChangeKind)read.Status;
        bool over = (kind != ChangeKind.Added && _sizes.GetValueOrDefault(read.Old.Id) > _ceiling)
                    || (kind != ChangeKind.Deleted && _sizes.GetValueOrDefault(read.New.Id) > _ceiling);
        return over ? 1 : 0;
    }

    private nint TreeToTree(ObjectId? before, ObjectId after, DiffOptions options)
    {
        nint old = before is null ? 0 : Tree(before);
        try
        {
            nint now = Tree(after);
            try
            {
                // The trees are read while the diff is made; its deltas carry ids, and a patch loads
                // blobs by id, so neither tree is needed afterwards.
                NativeRepository.Check(git_diff_tree_to_tree(out nint diff, _repository, old, now, ref options),
                    "diff two trees");
                return diff;
            }
            finally
            {
                git_tree_free(now);
            }
        }
        finally
        {
            if (old != 0) git_tree_free(old);
        }
    }

    /// <summary>Renames detected, then every delta's patch read as the change the history records.</summary>
    private static List<ChangedPath> Record(nint diff)
    {
        // Null options is what LibGit2Sharp passes too: renames by the repository's configuration.
        NativeRepository.Check(git_diff_find_similar(diff, 0), "detect renames");
        int count = checked((int)git_diff_num_deltas(diff));
        var files = new List<ChangedPath>(count);
        for (int index = 0; index < count; index++)
        {
            NativeRepository.Check(git_patch_from_diff(out nint patch, diff, (nuint)index), "make a patch");
            try
            {
                // Read after the patch is made, because making it is what decides that a side is binary.
                var delta = Marshal.PtrToStructure<Delta>(git_diff_get_delta(diff, (nuint)index));
                var edits = patch == 0 ? [] : Edits(patch);
                int added = 0, deleted = 0;
                foreach (var edit in edits)
                {
                    added += edit.Added;
                    deleted += edit.Deleted;
                }

                bool binary = (delta.Flags & _binary) != 0;
                string path = Marshal.PtrToStringUTF8(delta.New.Path)!;
                // libgit2 points both sides at one string unless the delta moved, so most need one decode.
                string oldPath = delta.Old.Path == delta.New.Path ? path : Marshal.PtrToStringUTF8(delta.Old.Path)!;
                files.Add(new ChangedPath(path, oldPath, LocalCopy.KindName((ChangeKind)delta.Status), added,
                    deleted, binary, binary ? [] : edits));
            }
            finally
            {
                if (patch != 0) git_patch_free(patch);
            }
        }

        return files;
    }

    /// <summary>
    ///     The changes over the ceiling, recorded the way the file pass treats such a blob: as binary,
    ///     with no lines. One whose blob left one path and arrived at another in the same commit is the
    ///     move libgit2 detects first, by id, and is recorded as the rename it is — the id is all that
    ///     telling needs, so nothing is read (#292). A large file moved and edited in one commit has a
    ///     new id, and stays a deletion and an addition, since matching it means reading it. The pairing
    ///     does not consult <c>diff.renames</c>; only a git configuration that switched rename detection
    ///     off would make it disagree with the patch's.
    /// </summary>
    private static List<ChangedPath> Record(List<Oversized> oversized)
    {
        var files = new List<ChangedPath>(oversized.Count);
        var departed = new Dictionary<GitId, Queue<string>>();
        foreach (var change in oversized)
        {
            if (change is not { Old: { } old, New: null }) continue;
            if (!departed.TryGetValue(old, out var paths)) departed[old] = paths = new Queue<string>();
            paths.Enqueue(change.Path);
        }

        // Both ends of every move, which is one set because a commit names each path once.
        var moved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in oversized)
        {
            if (change is not { Old: null, New: { } arrived }
                || !departed.TryGetValue(arrived, out var from) || !from.TryDequeue(out string? oldPath))
                continue;
            moved.Add(oldPath);
            moved.Add(change.Path);
            files.Add(new ChangedPath(change.Path, oldPath, LocalCopy.KindName(ChangeKind.Renamed), 0, 0, true, []));
        }

        foreach (var change in oversized)
            if (!moved.Contains(change.Path))
                files.Add(new ChangedPath(change.Path, change.Path, LocalCopy.KindName(change.Kind), 0, 0, true, []));

        return files;
    }

    /// <summary>One edit per hunk, placed off its header by the rule <see cref="UnifiedDiff.OldPosition" /> holds.</summary>
    private static List<LineEdit> Edits(nint patch)
    {
        int count = checked((int)git_patch_num_hunks(patch));
        var edits = new List<LineEdit>(count);
        for (int index = 0; index < count; index++)
        {
            NativeRepository.Check(git_patch_get_hunk(out nint hunk, out _, patch, (nuint)index), "read a hunk");
            int oldStart = Marshal.ReadInt32(hunk), oldLines = Marshal.ReadInt32(hunk, 4);
            int newLines = Marshal.ReadInt32(hunk, 12);
            edits.Add(new LineEdit(UnifiedDiff.OldPosition(oldStart, oldLines), oldLines, newLines));
        }

        return edits;
    }

    /// <summary>A side's blob size from its header, read the first time its id is seen.</summary>
    private long Size(DiffFile file)
    {
        if (file.Mode == _submoduleMode) return 0;
        if (_sizes.TryGetValue(file.Id, out long size)) return size;
        NativeRepository.Check(git_odb_read_header(out nuint length, out _, _odb, in file.Id), "read an object header");
        return _sizes[file.Id] = (long)length;
    }

    private nint Tree(ObjectId id)
    {
        NativeRepository.Check(git_tree_lookup(out nint tree, _repository, id.RawId), "load a tree");
        return tree;
    }

    /// <summary>A change over the ceiling, with the blob on each side it has.</summary>
    private sealed record Oversized(string Path, ChangeKind Kind, GitId? Old, GitId? New);

    // int (*git_diff_notify_cb)(const git_diff *, const git_diff_delta *, const char *matched_pathspec, void *payload)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Notify(nint diff, nint delta, nint pathspec, nint payload);

    // git_diff_options as the bundled libgit2 lays it out, the same fields LibGit2Sharp declares.
    [StructLayout(LayoutKind.Sequential)]
    private struct DiffOptions
    {
        public uint Version;
        public uint Flags;
        public int IgnoreSubmodules;
        public nint PathspecStrings;
        public nuint PathspecCount;
        public nint NotifyCallback;
        public nint ProgressCallback;
        public nint Payload;
        public uint ContextLines;
        public uint InterhunkLines;
        public ushort IdAbbrev;
        public long MaxSize;
        public nint OldPrefix;
        public nint NewPrefix;
    }

    // git_diff_file: the id's twenty bytes, then the path, size, flags and mode.
    [StructLayout(LayoutKind.Sequential)]
    private struct DiffFile
    {
        public GitId Id;
        public nint Path;
        public long Size;
        public uint Flags;
        public ushort Mode;
        public ushort IdAbbrev;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Delta
    {
        public int Status;
        public uint Flags;
        public ushort Similarity;
        public ushort FileCount;
        public DiffFile Old;
        public DiffFile New;
    }

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_repository_odb(out nint odb, NativeRepository repository);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void git_odb_free(nint odb);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_odb_read_header(out nuint length, out int type, nint odb, in GitId id);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_tree_lookup(out nint tree, NativeRepository repository, byte[] id);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void git_tree_free(nint tree);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_diff_options_init(ref DiffOptions options, uint version);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_diff_tree_to_tree(out nint diff, NativeRepository repository, nint oldTree,
        nint newTree, ref DiffOptions options);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void git_diff_free(nint diff);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_diff_find_similar(nint diff, nint options);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint git_diff_num_deltas(nint diff);

    // The delta belongs to the diff and is valid while it is.
    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint git_diff_get_delta(nint diff, nuint index);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_patch_from_diff(out nint patch, nint diff, nuint index);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void git_patch_free(nint patch);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint git_patch_num_hunks(nint patch);

    // The hunk belongs to the patch: four ints, old start and count then new start and count, lead it.
    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_patch_get_hunk(out nint hunk, out nuint lines, nint patch, nuint index);
}

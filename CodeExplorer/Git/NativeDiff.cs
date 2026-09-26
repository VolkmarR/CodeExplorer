using System.Runtime.InteropServices;
using LibGit2Sharp;

namespace CodeExplorer.Git;

/// <summary>
///     A git object id as a value, so it can key a dictionary without a byte array per entry. Twenty
///     bytes of SHA-1, which is what the bundled libgit2 is built for (see <see cref="BlobReader" />).
/// </summary>
internal readonly record struct GitId(long Head, long Middle, int Tail)
{
    public byte[] ToRaw()
    {
        var raw = new byte[20];
        BitConverter.TryWriteBytes(raw.AsSpan(0, 8), Head);
        BitConverter.TryWriteBytes(raw.AsSpan(8, 8), Middle);
        BitConverter.TryWriteBytes(raw.AsSpan(16, 4), Tail);
        return raw;
    }
}

/// <summary>
///     One side of a changed path: the entry's object, its git file mode and, for a blob, its size
///     from the object header. A submodule is a commit of another repository, which this one does not
///     hold, so it is never sized and reads as 0.
/// </summary>
internal readonly record struct TreeSide(GitId Id, int Mode, long Size)
{
    public const int SubmoduleMode = 0xE000; // 0160000
    public const int LinkMode = 0xA000; // 0120000

    public bool IsSubmodule => Mode == SubmoduleMode;
    public bool IsLink => Mode == LinkMode;
}

/// <summary>A path that is not a directory and differs between two trees; a side is null where the path is absent.</summary>
internal readonly record struct TreeChange(string Path, TreeSide? Old, TreeSide? New);

/// <summary>What a commit's diff came to: every changed path, and the recorded changes unless one is over the ceiling.</summary>
/// <param name="Changes">Every non-directory path the commit changed, before rename detection, with its blobs sized.</param>
/// <param name="Files">
///     The changes as the history records them, renames detected and edits read; null when a blob
///     in <paramref name="Changes" /> is over the ceiling, which the caller diffs around instead.
/// </param>
internal sealed record CommitChanges(List<TreeChange> Changes, List<ChangedPath>? Files);

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
    private const int _added = 1, _deleted = 2; // git_delta_t

    private readonly NativeRepository _repository;
    private readonly nint _odb;
    private readonly Dictionary<GitId, long> _sizes = [];
    private DiffOptions _options;

    public NativeDiff(NativeRepository repository)
    {
        _repository = repository;
        NativeRepository.Check(git_repository_odb(out _odb, repository), "open the object database");
        NativeRepository.Check(git_diff_options_init(ref _options, 1), "initialise diff options");
        _options.Flags |= _includeTypeChange;
        // No context lines, for the reason LocalCopy gives; it is also what makes a hunk one edit.
        _options.ContextLines = 0;
        _options.InterhunkLines = 0;
    }

    /// <summary>
    ///     The changes from <paramref name="before" /> to <paramref name="after" />, a null
    ///     <paramref name="before" /> being the empty tree. The recorded files are left out when a blob
    ///     on either side of any change is over <paramref name="maxBlobBytes" />: rename detection reads
    ///     the blobs it compares, so it may not run over a diff holding one.
    /// </summary>
    public CommitChanges Describe(ObjectId? before, ObjectId after, long maxBlobBytes)
    {
        nint diff = TreeToTree(before, after);
        try
        {
            int count = checked((int)git_diff_num_deltas(diff));
            var changes = new List<TreeChange>(count);
            bool oversized = false;
            for (int index = 0; index < count; index++)
            {
                var delta = Marshal.PtrToStructure<Delta>(git_diff_get_delta(diff, (nuint)index));
                var change = new TreeChange(Marshal.PtrToStringUTF8(delta.New.Path)!,
                    delta.Status == _added ? null : Side(delta.Old),
                    delta.Status == _deleted ? null : Side(delta.New));
                oversized |= change.Old?.Size > maxBlobBytes || change.New?.Size > maxBlobBytes;
                changes.Add(change);
            }

            return new CommitChanges(changes, oversized ? null : Record(diff));
        }
        finally
        {
            git_diff_free(diff);
        }
    }

    public void Dispose() => git_odb_free(_odb);

    private nint TreeToTree(ObjectId? before, ObjectId after)
    {
        nint old = before is null ? 0 : Tree(before);
        try
        {
            nint now = Tree(after);
            try
            {
                // The trees are read while the diff is made; its deltas carry ids, and a patch loads
                // blobs by id, so neither tree is needed afterwards.
                NativeRepository.Check(git_diff_tree_to_tree(out nint diff, _repository, old, now, ref _options),
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
                files.Add(new ChangedPath(Marshal.PtrToStringUTF8(delta.New.Path)!,
                    Marshal.PtrToStringUTF8(delta.Old.Path)!, LocalCopy.KindName((ChangeKind)delta.Status), added,
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
    ///     One edit per hunk, read off its header the way <see cref="UnifiedDiff.Edits" /> reads the
    ///     rendered one: the old start is 1-based and names the line before an insertion that removes
    ///     nothing, so it is the 0-based position of the edit in that case and one past it otherwise.
    /// </summary>
    private static List<LineEdit> Edits(nint patch)
    {
        int count = checked((int)git_patch_num_hunks(patch));
        var edits = new List<LineEdit>(count);
        for (int index = 0; index < count; index++)
        {
            NativeRepository.Check(git_patch_get_hunk(out nint hunk, out _, patch, (nuint)index), "read a hunk");
            int oldStart = Marshal.ReadInt32(hunk), oldLines = Marshal.ReadInt32(hunk, 4);
            int newLines = Marshal.ReadInt32(hunk, 12);
            edits.Add(new LineEdit(oldLines == 0 ? oldStart : oldStart - 1, oldLines, newLines));
        }

        return edits;
    }

    private TreeSide Side(DiffFile file)
    {
        var id = new GitId(file.IdHead, file.IdMiddle, file.IdTail);
        if (file.Mode == TreeSide.SubmoduleMode) return new TreeSide(id, file.Mode, 0);
        if (!_sizes.TryGetValue(id, out long size))
        {
            NativeRepository.Check(git_odb_read_header(out nuint length, out _, _odb, id.ToRaw()), "read an object header");
            _sizes[id] = size = (long)length;
        }

        return new TreeSide(id, file.Mode, size);
    }

    private nint Tree(ObjectId id)
    {
        NativeRepository.Check(git_tree_lookup(out nint tree, _repository, id.RawId), "load a tree");
        return tree;
    }

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

    // git_diff_file: the id's twenty bytes read as three fields, then the path, size, flags and mode.
    [StructLayout(LayoutKind.Sequential)]
    private struct DiffFile
    {
        public long IdHead;
        public long IdMiddle;
        public int IdTail;
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
    private static extern int git_odb_read_header(out nuint length, out int type, nint odb, byte[] id);

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

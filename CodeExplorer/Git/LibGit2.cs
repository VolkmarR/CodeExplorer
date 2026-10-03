using System.Runtime.InteropServices;
using LibGit2Sharp;

namespace CodeExplorer.Git;

/// <summary>
///     A git object id as a value, so it can key a dictionary without a byte array per entry. Twenty
///     bytes of SHA-1, which is what the bundled libgit2 is built for, laid out as libgit2's
///     <c>git_oid</c> so a native struct can hold one and a call can take one. Every id this server
///     hands libgit2 crosses as one of these, by reference.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct GitId(long Head, long Middle, int Tail)
{
    /// <summary>The same twenty bytes as LibGit2Sharp's id, in the order libgit2 reads them.</summary>
    public static GitId Of(ObjectId id)
    {
        ReadOnlySpan<byte> raw = id.RawId;
        return new GitId(MemoryMarshal.Read<long>(raw), MemoryMarshal.Read<long>(raw[8..]),
            MemoryMarshal.Read<int>(raw[16..]));
    }
}

/// <summary>
///     Every call into libgit2 that goes past LibGit2Sharp, and every fact about the bundled library
///     those calls rest on: its name, the option and error numbers, and the layout of the structs it
///     hands back. They are one fact, what the LibGit2Sharp package bundles, so a bump of the package
///     (ADR-0004) is a check of this one file. The callers are <see cref="TransferStallLimit" />,
///     <see cref="NativeRepository" />, <see cref="BlobReader" /> and <see cref="NativeDiff" />.
/// </summary>
internal static class LibGit2
{
    // The native library LibGit2Sharp ships and has already loaded; the name carries the libgit2 commit
    // it was built from, so it changes with the LibGit2Sharp package. A mismatch throws
    // DllNotFoundException the first time GitClones is built, which every refresh test does. Every
    // declaration below binds to the library through this constant, so an update of the package
    // changes the name here and nowhere else.
    internal const string Library = "git2-5853918";

    // git_libgit2_opt_t in libgit2 1.7 and later, which the bundled 1.9 is.
    internal const int SetServerConnectTimeout = 39;
    internal const int SetServerTimeout = 41;

    // GIT_ERROR_HTTP in libgit2 1.9's git_error_t.
    internal const int HttpErrorClass = 34;

    internal static void Check(int result, string action)
    {
        if (result < 0)
            throw new InvalidOperationException($"libgit2 could not {action} (error {result}).");
    }

    // libgit2's git_error: the message, then the class.
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct GitError(nint Message, int Class);

    [DllImport(Library, EntryPoint = "git_error_last", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint LastError();

    [DllImport(Library, EntryPoint = "git_libgit2_opts", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetOption(int option, int value);

    [DllImport(Library, EntryPoint = "git_libgit2_opts", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetOptionOnAppleSilicon(int option, nint unused1, nint unused2, nint unused3,
        nint unused4, nint unused5, nint unused6, nint unused7, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_repository_open(out NativeRepository repository, byte[] path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void git_repository_free(nint repository);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_blob_lookup(out nint blob, NativeRepository repository, in GitId id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void git_blob_free(nint blob);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_blob_is_binary(nint blob);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint git_blob_rawcontent(nint blob);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong git_blob_rawsize(nint blob);

    // int (*git_diff_notify_cb)(const git_diff *, const git_diff_delta *, const char *matched_pathspec, void *payload)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int Notify(nint diff, nint delta, nint pathspec, nint payload);

    // git_diff_options as the bundled libgit2 lays it out, the same fields LibGit2Sharp declares.
    [StructLayout(LayoutKind.Sequential)]
    internal struct DiffOptions
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
    internal struct DiffFile
    {
        public GitId Id;
        public nint Path;
        public long Size;
        public uint Flags;
        public ushort Mode;
        public ushort IdAbbrev;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Delta
    {
        public int Status;
        public uint Flags;
        public ushort Similarity;
        public ushort FileCount;
        public DiffFile Old;
        public DiffFile New;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_repository_odb(out nint odb, NativeRepository repository);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void git_odb_free(nint odb);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_odb_read_header(out nuint length, out int type, nint odb, in GitId id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_tree_lookup(out nint tree, NativeRepository repository, in GitId id);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void git_tree_free(nint tree);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_diff_options_init(ref DiffOptions options, uint version);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_diff_tree_to_tree(out nint diff, NativeRepository repository, nint oldTree,
        nint newTree, ref DiffOptions options);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void git_diff_free(nint diff);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_diff_find_similar(nint diff, nint options);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint git_diff_num_deltas(nint diff);

    // The delta belongs to the diff and is valid while it is.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint git_diff_get_delta(nint diff, nuint index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_patch_from_diff(out nint patch, nint diff, nuint index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void git_patch_free(nint patch);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nuint git_patch_num_hunks(nint patch);

    // The hunk belongs to the patch: four ints, old start and count then new start and count, lead it.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int git_patch_get_hunk(out nint hunk, out nuint lines, nint patch, nuint index);
}

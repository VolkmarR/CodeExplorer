using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeExplorer.Git;

/// <summary>
///     A libgit2 handle on a local copy of our own, beside LibGit2Sharp's, which is internal. It is for
///     the reads LibGit2Sharp makes too dear — a blob loaded once (<see cref="BlobReader" />) and a
///     commit diffed with its trees walked once (<see cref="NativeDiff" />) — and both share this one
///     handle and the object cache behind it. A refresh reads one copy from one thread at a time, which
///     is all a libgit2 repository allows.
/// </summary>
internal sealed class NativeRepository() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    public static NativeRepository Open(string gitDirectory)
    {
        // A NUL-terminated UTF-8 path, which is what libgit2 takes on every platform.
        Check(git_repository_open(out var repository, Encoding.UTF8.GetBytes(gitDirectory + "\0")),
            "open the local copy");
        return repository;
    }

    public static void Check(int result, string action)
    {
        if (result < 0)
            throw new InvalidOperationException($"libgit2 could not {action} (error {result}).");
    }

    protected override bool ReleaseHandle()
    {
        git_repository_free(handle);
        return true;
    }

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_repository_open(out NativeRepository repository, byte[] path);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void git_repository_free(nint repository);
}

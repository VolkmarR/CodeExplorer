using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using LibGit2Sharp;
using Microsoft.Win32.SafeHandles;

namespace CodeExplorer.Git;

/// <summary>
///     Reads a committed file's blob with one load of it (#263), through libgit2 directly because
///     LibGit2Sharp cannot. Its <c>Blob</c> looks the object up again for every property, and libgit2
///     caches no blob, so each lookup inflates the object and applies its delta chain once more:
///     <c>IsBinary</c> was one load, and <c>GetContentStream</c> two more, since it reads the lazy
///     <c>Size</c> before it opens the content. Here the binary verdict is libgit2's own
///     <c>git_blob_is_binary</c>, asked of the same loaded object the text is copied out of, so which
///     files are binary is exactly what it was.
///     It opens its own native handle on the clone beside LibGit2Sharp's, since that one is internal.
///     A refresh reads one copy from one thread at a time, which is all a libgit2 repository allows.
/// </summary>
internal sealed class BlobReader : IDisposable
{
    private readonly RepositoryHandle _repository;

    public BlobReader(string gitDirectory)
    {
        // A NUL-terminated UTF-8 path, which is what libgit2 takes on every platform.
        Check(git_repository_open(out _repository, Encoding.UTF8.GetBytes(gitDirectory + "\0")), "open the local copy");
    }

    /// <summary>How many blobs have been loaded, which is what proves each file is loaded once.</summary>
    public int Loads { get; private set; }

    /// <summary>The blob's content decoded the way <see cref="BlobText" /> decides, or null when libgit2 calls it binary.</summary>
    public string? Text(ObjectId id)
    {
        Check(git_blob_lookup(out var blob, _repository, id.RawId), "load a blob");
        Loads++;
        byte[]? buffer = null;
        try
        {
            if (git_blob_is_binary(blob) != 0) return null;
            // Copied out rather than decoded in place: a span over native memory needs unsafe code,
            // which nothing else here does. Into a pooled buffer, because the copy lives only as long
            // as the decode and a build makes one for every file it reads.
            int size = checked((int)git_blob_rawsize(blob));
            buffer = ArrayPool<byte>.Shared.Rent(size);
            Marshal.Copy(git_blob_rawcontent(blob), buffer, 0, size);
            return BlobText.Decode(buffer.AsSpan(0, size));
        }
        finally
        {
            git_blob_free(blob);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose() => _repository.Dispose();

    private static void Check(int result, string action)
    {
        if (result < 0)
            throw new InvalidOperationException($"libgit2 could not {action} (error {result}).");
    }

    private sealed class RepositoryHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle()
        {
            git_repository_free(handle);
            return true;
        }
    }

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_repository_open(out RepositoryHandle repository, byte[] path);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void git_repository_free(nint repository);

    // The id is a git_oid, twenty bytes of SHA-1 in the bundled build, passed as a pointer to them.
    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_blob_lookup(out nint blob, RepositoryHandle repository, byte[] id);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void git_blob_free(nint blob);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int git_blob_is_binary(nint blob);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint git_blob_rawcontent(nint blob);

    [DllImport(TransferStallLimit.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong git_blob_rawsize(nint blob);
}

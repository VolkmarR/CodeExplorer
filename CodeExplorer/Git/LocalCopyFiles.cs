using LibGit2Sharp;

namespace CodeExplorer.Git;

/// <summary>
///     Deleting what a local copy is made of. libgit2 writes packs and loose objects read-only, and
///     Windows refuses to delete a read-only file, so <c>Directory.Delete</c> fails on the first pack
///     unless the attribute is cleared first. One place, because a removal of the whole copy
///     (<see cref="GitClones" />) and a repack deleting the files it folded (<see cref="LocalCopyRepack" />)
///     both need it and had each written it. The failures caught around a local copy are told apart
///     here too, for the same reason: both classes caught them, with the same list written out nine
///     times.
/// </summary>
internal static class LocalCopyFiles
{
    /// <summary>The disk refusing a file of a local copy: locked, read-only, or gone under the walk.</summary>
    public static bool IsDiskFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <summary>
    ///     The same, or anything libgit2 throws while it opens, clones into or fetches into a local copy —
    ///     a remote that refused the credential or went quiet included, not only the disk. GitClones turns
    ///     each into the transfer's own failure, so narrowing this to the disk would let a failed fetch
    ///     escape as an unexplained exception.
    /// </summary>
    public static bool IsGitOrDiskFailure(Exception ex) => ex is LibGit2SharpException || IsDiskFailure(ex);

    /// <summary>Deletes a folder and everything in it, and nothing for a folder that is not there.</summary>
    public static void DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
        Directory.Delete(directory, true);
    }

    /// <summary>Deletes one file, and nothing for a file that is not there.</summary>
    public static void DeleteFile(string file)
    {
        if (!File.Exists(file)) return;
        File.SetAttributes(file, FileAttributes.Normal);
        File.Delete(file);
    }
}

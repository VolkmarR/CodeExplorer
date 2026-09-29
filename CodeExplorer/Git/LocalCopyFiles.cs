namespace CodeExplorer.Git;

/// <summary>
///     Deleting what a local copy is made of. libgit2 writes packs and loose objects read-only, and
///     Windows refuses to delete a read-only file, so <c>Directory.Delete</c> fails on the first pack
///     unless the attribute is cleared first. One place, because a removal of the whole copy
///     (<see cref="GitClones" />) and a repack deleting the files it folded (<see cref="LocalCopyRepack" />)
///     both need it and had each written it.
/// </summary>
internal static class LocalCopyFiles
{
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

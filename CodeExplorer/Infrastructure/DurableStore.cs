using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CodeExplorer.Infrastructure;

/// <summary>
///     Where the durable copy lives (CONTEXT.md): the Parquet set of every project index and the
///     control database's backup, the only things that survive the container's disk being wiped on
///     every stop. Names are blob names — <c>indexes/&lt;slug&gt;/files.parquet</c> — and the local
///     path they resolve to lays them out as folders, so both halves hold the same tree.
///     One class choosing between two code paths in its constructor, not a port with two adapters
///     (ADR-0005): <c>Storage:BlobContainerUrl</c> selects Blob Storage under the managed identity and
///     its absence selects a folder on disk, which is the documented offline default (ADR-0004) and
///     what the tests use. Nothing here may require Azure to start.
/// </summary>
public sealed class DurableStore
{
    /// <summary>
    ///     The container this account's blobs live in, named here because it selects the whole
    ///     local-or-Azure decision and because <see cref="KeyRing" /> shares the same container.
    /// </summary>
    public const string ContainerSetting = "Storage:BlobContainerUrl";

    /// <summary>
    ///     The identity every Azure client in this app authenticates with. One instance and not one per
    ///     client: a credential is a token cache and a chain of probes for where the token comes from,
    ///     and a second instance means both are paid twice for the same account. DefaultAzureCredential
    ///     rather than a connection string, here and in <see cref="KeyRing" />: a key in configuration
    ///     is a secret to rotate, and the app already has an identity for the container and the vault.
    /// </summary>
    public static TokenCredential Credential { get; } = new DefaultAzureCredential();

    /// <summary>What an unusable container URL is told to do, which is also what the key ring loses.</summary>
    public const string ContainerRemedy =
        "Give it one such as https://account.blob.core.windows.net/codeexplorer, or remove it to keep the "
        + "durable copy and the Data Protection key ring on local disk.";

    // Null is the local path. It is the whole of the local-or-Azure decision, made once here, so
    // every method below is one branch rather than a virtual call into a second implementation.
    private readonly BlobContainerClient? _container;

    private readonly string _directory;

    public DurableStore(IConfiguration configuration)
    {
        // Read through Setting so that a container URL that is not a URL names itself, as the key ring
        // setting and the OTLP endpoint do; the alternative is UriFormatException from inside the SDK.
        if (Setting.Url(configuration, ContainerSetting, ContainerRemedy) is { } container)
            _container = new BlobContainerClient(container, Credential);

        // Beside the data directory rather than inside it: what the container wipes and what survives
        // the wipe are different things, and a folder standing in for a blob account should read that
        // way even when both happen to sit on one developer disk.
        _directory = configuration["Storage:DurableDirectory"]
                     ?? Path.Combine(configuration["Storage:DataDirectory"] ?? "data", "durable");
    }

    /// <summary>
    ///     Copies a stored file to <paramref name="localPath" />, overwriting it, and answers false when
    ///     the store holds no such name. Absent is an ordinary answer here: a project that has never been
    ///     indexed has no durable copy, and neither has a first run against an empty account.
    /// </summary>
    public async Task<bool> FetchAsync(string name, string localPath, CancellationToken cancellationToken)
    {
        if (_container is null) return CopyIn(name, localPath);

        Prepare(localPath);
        try
        {
            await _container.GetBlobClient(name).DownloadToAsync(localPath, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Safe to swallow: "no such blob" is the answer the caller asked for, and the alternative
            // — Exists then Download — is two round trips that still race.
            return false;
        }
    }

    /// <summary>
    ///     The same, synchronously, for the one caller that runs before the server accepts a request:
    ///     <see cref="CodeExplorer.Control.ControlDatabase" />'s constructor has to have the file before it opens it. Both
    ///     paths are genuinely synchronous here, so nothing blocks a thread on an async call
    ///     (CODING_STANDARDS, Async).
    /// </summary>
    public bool Fetch(string name, string localPath)
    {
        if (_container is null) return CopyIn(name, localPath);

        Prepare(localPath);
        try
        {
            _container.GetBlobClient(name).DownloadTo(localPath);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Safe to swallow, for the reason FetchAsync gives.
            return false;
        }
    }

    /// <summary>Stores <paramref name="localPath" /> under <paramref name="name" />, replacing what was there.</summary>
    public async Task StoreAsync(string name, string localPath, CancellationToken cancellationToken)
    {
        if (_container is null)
        {
            CopyOut(name, localPath);
            return;
        }

        await using var file = File.OpenRead(localPath);
        await _container.GetBlobClient(name).UploadAsync(file, true, cancellationToken);
    }

    /// <summary>
    ///     The same, synchronously, for the same one caller <see cref="Fetch" /> exists for: a startup
    ///     that migrated the control database has to leave the store holding the migrated shape, and it
    ///     does that from a constructor.
    /// </summary>
    public void Store(string name, string localPath)
    {
        if (_container is null)
        {
            CopyOut(name, localPath);
            return;
        }

        using var file = File.OpenRead(localPath);
        _container.GetBlobClient(name).Upload(file, true);
    }

    /// <summary>
    ///     Forgets one stored name. Removing a name the store does not hold is not an error: the first
    ///     store of a project has nothing to remove, and neither has one that follows an interrupted one.
    /// </summary>
    public async Task RemoveOneAsync(string name, CancellationToken cancellationToken)
    {
        if (_container is null)
        {
            string stored = Resolve(name);
            if (File.Exists(stored)) File.Delete(stored);
            return;
        }

        await _container.DeleteBlobIfExistsAsync(name, DeleteSnapshotsOption.IncludeSnapshots,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     Forgets everything under a name prefix, for a project an operator deleted. Removing nothing is
    ///     not an error: a project deleted before it was ever indexed has no durable copy to forget, and
    ///     leaving one behind would restore a deleted project's files under a slug someone reused.
    ///     Every prefix this app removes ends at a slash, so locally it is a folder.
    /// </summary>
    public async Task RemoveAsync(string prefix, CancellationToken cancellationToken)
    {
        if (_container is null)
        {
            string stored = Resolve(prefix);
            if (Directory.Exists(stored)) Directory.Delete(stored, true);
            return;
        }

        // Listed and deleted one at a time: a container holds every project, so there is no container
        // to drop and a prefix is all that separates one project's durable copy from another's.
        await foreach (var blob in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix,
                           cancellationToken))
            await RemoveOneAsync(blob.Name, cancellationToken);
    }

    private void CopyOut(string name, string localPath)
    {
        string stored = Resolve(name);
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        // Copy and not move: the caller owns the local file and may still be writing siblings of it.
        File.Copy(localPath, stored, true);
    }

    private bool CopyIn(string name, string localPath)
    {
        string stored = Resolve(name);
        if (!File.Exists(stored)) return false;

        Prepare(localPath);
        File.Copy(stored, localPath, true);
        return true;
    }

    /// <summary>
    ///     A blob name's forward slashes become folders, so the folder and the account hold the same
    ///     tree and a name written for one reads correctly against the other.
    /// </summary>
    private string Resolve(string name) =>
        Path.Combine(_directory, name.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The SDK writes to the path but will not create the folder it sits in, nor overwrite it.</summary>
    private static void Prepare(string localPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
        File.Delete(localPath);
    }
}

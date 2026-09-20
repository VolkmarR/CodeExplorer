using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;

namespace CodeExplorer;

/// <summary>
///     Where the Data Protection key ring is kept, which is what decides whether anything protected
///     survives a stop: the container's disk is wiped every time, so a key ring on it is a new key
///     ring on every wake. What that breaks is not the key ring but what was protected under it — the
///     repository credentials in the control database, which then cannot be decrypted at all.
///     Two nullable settings and no enum over them. Three of the four combinations are shapes a
///     deployment means to be in and the fourth is a mistake, so a type naming three would still leave
///     the fourth to be handled beside it — and the pair is what both readers here already branch on.
/// </summary>
/// <param name="Container">The blob container to persist to, and null for the local key ring.</param>
/// <param name="KeyVaultKey">The Key Vault key that wraps it, and null for no wrapping.</param>
public sealed record KeyRingSettings(Uri? Container, Uri? KeyVaultKey);

/// <summary>
///     Registers Data Protection, in the container's shape or the developer's, selected by the same
///     absent-configuration rule every Azure dependency here follows (ADR-0004). It shares
///     <see cref="DurableStore" />'s container and its credential rather than taking its own: one
///     managed identity grant, one token cache, one account to watch, and the key ring sits under its
///     own prefix beside the Parquet sets and the control database backup rather than among them.
///     At the root rather than in a module folder, for the reason <c>DurableStore</c> is: ADR-0005's
///     folders are the concepts in CONTEXT.md, and a key ring belongs to no one of them — Control
///     protects credentials with it and Git unprotects them, so it sits where both can see it.
/// </summary>
public static class KeyRing
{
    /// <summary>
    ///     The key ring's blob, under a prefix of its own so that nothing walking <c>indexes/</c> or
    ///     <c>control/</c> — a project deletion above all, which removes a whole prefix — can reach it.
    ///     The container must already exist, as it must for the durable copy; the blob need not, and
    ///     the repository creates it on the first key it writes.
    /// </summary>
    public const string BlobName = "keyring/keys.xml";

    /// <summary>
    ///     The Data Protection purpose string for stored repository credentials. Ciphertext protected
    ///     under one purpose cannot be unprotected under another, so this constant is the only shared
    ///     secret between the writer in <c>Control/</c> and the reader in <c>Git/</c> — and it sits
    ///     here, with the key ring both of them protect under, rather than in either of them, because
    ///     a constant owned by one made that module a dependency of the other (ADR-0005).
    /// </summary>
    public const string CredentialPurpose = "CodeExplorer.RepositoryCredential";

    /// <summary>The Key Vault key that wraps the key ring, named next to the container it wraps.</summary>
    public const string KeyVaultKeySetting = "Storage:KeyVaultKeyUrl";

    /// <summary>
    ///     Fixes the application discriminator that would otherwise be derived from the content root
    ///     path. A persisted key ring is read back by a different container, at a path no deployment
    ///     promises to keep, and a changed discriminator makes every payload undecryptable while
    ///     leaving the keys themselves intact — the failure would read as a lost key ring. Set only
    ///     where the key ring is shared: the local path keeps the framework's default so that
    ///     credentials stored by an earlier build still decrypt. The cost of that is a one-way step:
    ///     a machine that starts persisting to a container is a machine whose locally stored
    ///     credentials have to be set again, which README says and which only a developer meets.
    /// </summary>
    private const string ApplicationName = "CodeExplorer";

    /// <summary>
    ///     Reads the two settings the key ring has. Absent is the answer on both: no container is the
    ///     local key ring, and no key is an unwrapped one.
    /// </summary>
    public static KeyRingSettings Settings(IConfiguration configuration) =>
        new(Setting.Url(configuration, DurableStore.ContainerSetting, DurableStore.ContainerRemedy),
            Setting.Url(configuration, KeyVaultKeySetting,
                "Give it one such as https://vault.vault.azure.net/keys/keyring, or remove it to leave the "
                + "key ring unwrapped."));

    /// <summary>
    ///     Registers Data Protection and hands back what it was registered with, so that the composition
    ///     root can say which shape came up: configuration runs before the container that holds a logger
    ///     exists, and a whole hosted service for one line at start is more machinery than the line.
    /// </summary>
    public static KeyRingSettings AddKeyRing(this WebApplicationBuilder builder)
    {
        var settings = Settings(builder.Configuration);
        var protection = builder.Services.AddDataProtection();

        if (settings.Container is { } container)
        {
            protection.SetApplicationName(ApplicationName)
                .PersistKeysToAzureBlobStorage(
                    new BlobContainerClient(container, DurableStore.Credential).GetBlobClient(BlobName));
            if (settings.KeyVaultKey is { } key)
                protection.ProtectKeysWithAzureKeyVault(key, DurableStore.Credential);
        }

        return settings;
    }

    /// <summary>
    ///     Says which of the three shapes the key ring came up in, which is the first thing to check
    ///     when a credential stops decrypting. Two of them are warned about: a key ring that does not
    ///     outlive the container loses every stored credential on the next stop, and one persisted
    ///     without a wrapping key is readable by anyone who can read the blob.
    /// </summary>
    public static void Report(this KeyRingSettings settings, ILogger logger)
    {
        // Read into locals first: a Uri argument to a log call is an evaluation the analyzer asks to
        // be held back until something is listening (CA1873), and a local costs nothing to pass.
        string? container = settings.Container?.OriginalString;
        string? key = settings.KeyVaultKey?.OriginalString;

        // Said before the shape below rather than as a shape of its own, because it is not one: a key
        // ring is wrapped only where it is persisted, so this deployment is the local one — and the
        // setting meant to make it durable is the evidence that nobody expects it to be.
        if (settings is { Container: null, KeyVaultKey: not null })
            logger.LogWarning("{KeySetting} is ignored because no {Setting} is configured: a key ring is "
                              + "wrapped only where it is persisted",
                KeyVaultKeySetting, DurableStore.ContainerSetting);

        switch (settings)
        {
            case { Container: null }:
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "Data Protection is local only: the default key ring is in use because no {Setting} "
                        + "is configured, so stored credentials do not survive a container restart",
                        DurableStore.ContainerSetting);
                break;
            case { KeyVaultKey: null }:
                logger.LogWarning(
                    "The Data Protection key ring is persisted to {Container} but not wrapped: set "
                    + "{KeySetting} so that reading the blob is not enough to decrypt a stored credential",
                    container, KeyVaultKeySetting);
                break;
            default:
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "The Data Protection key ring is persisted to {Container} and wrapped by {Key}",
                        container, key);
                break;
        }
    }
}

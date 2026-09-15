using System.Reflection;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Where the Data Protection key ring goes (#13), which is only interesting because of what it
///     takes with it: the repository credentials in the control database are ciphertext under it, and
///     a container that comes up with a new key ring cannot read a single one of them.
///     The Azure paths are asserted as wiring rather than end to end. Nothing here reaches an account:
///     registering a blob repository and a Key Vault encryptor does no I/O, so what these tests prove
///     is that a configured deployment gets those two and an unconfigured one gets neither. The round
///     trip against a real account is the manual check README describes; it needs credentials no test
///     run has.
/// </summary>
public sealed class KeyRingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Container = "https://account.blob.core.windows.net/codeexplorer";
    private const string Key = "https://vault.vault.azure.net/keys/keyring/1";

    /// <summary>The deployed shape: persisted to a container and wrapped by a key.</summary>
    private static readonly Dictionary<string, string?> Deployed = new()
        { [DurableStore.ContainerSetting] = Container, [KeyRing.KeyVaultKeySetting] = Key };

    [Fact]
    public void Absent_settings_select_the_local_key_ring_and_a_malformed_one_names_itself()
    {
        var absent = KeyRing.Settings(Settings.Of([]));
        Assert.Null(absent.Container);
        Assert.Null(absent.KeyVaultKey);

        var configured = KeyRing.Settings(Settings.Of(Deployed));
        Assert.Equal(new Uri(Container), configured.Container);
        Assert.Equal(new Uri(Key), configured.KeyVaultKey);

        // A setting that is not a URL names itself, as Telemetry:OtlpEndpoint does: the server refuses
        // to start over this, and "Invalid URI" does not say which setting was wrong.
        var bad = Assert.Throws<InvalidOperationException>(() =>
            KeyRing.Settings(Settings.Of(new Dictionary<string, string?>
                { [KeyRing.KeyVaultKeySetting] = "keyring" })));
        Assert.Contains(KeyRing.KeyVaultKeySetting, bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_Azure_settings_the_default_key_ring_is_used_and_reported_as_local_only()
    {
        // Neither a repository nor an encryptor: the framework's own defaults stay in place, which is
        // the offline default ADR-0004 requires and the only one a plain `dotnet run` has.
        var options = await OptionsAsync([]);
        Assert.Null(options.XmlRepository);
        Assert.Null(options.XmlEncryptor);

        Assert.Contains("local only", Reported([]).Only(LogLevel.Information, "Data Protection").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_container_persists_the_key_ring_under_its_own_prefix()
    {
        var options = await OptionsAsync(Deployed);

        Assert.NotNull(options.XmlRepository);
        Assert.Equal("AzureBlobXmlRepository", options.XmlRepository.GetType().Name);
        // The blob the repository was handed, read back through the field holding it: the name is the
        // whole of the "its own prefix" requirement, and the type is internal to the package, so there
        // is no public way to ask. A package upgrade that renames the field fails here, which is the
        // right place to find out that this is no longer being checked.
        Assert.Equal(KeyRing.BlobName, BlobOf(options.XmlRepository).Name);
        Assert.StartsWith("keyring/", KeyRing.BlobName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configured_key_wraps_the_persisted_key_ring()
    {
        var options = await OptionsAsync(Deployed);

        Assert.NotNull(options.XmlEncryptor);
        Assert.Equal("AzureKeyVaultXmlEncryptor", options.XmlEncryptor.GetType().Name);
        Assert.Contains(Key, Reported(Deployed).Only(LogLevel.Information, "wrapped by").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_ring_persisted_without_a_wrapping_key_is_warned_about()
    {
        var persisted = new Dictionary<string, string?> { [DurableStore.ContainerSetting] = Container };

        // Persisted, so a restart keeps the credentials, but readable by anyone who can read the blob.
        var options = await OptionsAsync(persisted);
        Assert.NotNull(options.XmlRepository);
        Assert.Null(options.XmlEncryptor);

        Assert.Contains(KeyRing.KeyVaultKeySetting,
            Reported(persisted).Only(LogLevel.Warning, "not wrapped").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrapping_key_with_no_container_is_warned_about_rather_than_half_applied()
    {
        var unpersisted = new Dictionary<string, string?> { [KeyRing.KeyVaultKeySetting] = Key };

        // A key ring is wrapped only where it is persisted, so this is the local deployment, said as
        // such — and the setting that was meant to make it durable is warned about rather than obeyed.
        var options = await OptionsAsync(unpersisted);
        Assert.Null(options.XmlRepository);
        Assert.Null(options.XmlEncryptor);

        var probe = Reported(unpersisted);
        Assert.Contains(DurableStore.ContainerSetting, probe.Only(LogLevel.Warning, "is ignored").Message,
            StringComparison.Ordinal);
        Assert.Contains("local only", probe.Only(LogLevel.Information, "Data Protection").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_stored_before_a_restart_decrypts_after_it()
    {
        using var host = new TestHost(SearchEngine.Substring);
        await host.CreateProjectAsync("keyring");
        await host.AddRepositoryAsync("keyring", "one",
            host.CreateGitRepository("keyring-one", new Dictionary<string, string> { ["a.cs"] = "class A;\n" }),
            "a-token");

        // Everything in memory is gone and the control database is read off disk again. This is the
        // local path only, and it is honest about what it can show: the key ring under the user
        // profile outlives the process, so a restart here is a restart on a developer machine and not
        // the container's disk being wiped. What it does hold to is that the pair still works end to
        // end — the purpose, the ciphertext column and the key ring — which is what the Azure path
        // then substitutes a different key ring into. The wipe itself is the README's manual check.
        host.Restart();

        var control = host.Services.GetRequiredService<ControlDatabase>();
        var repository = Assert.Single(await control.ListRepositoriesAsync("keyring", Ct));
        Assert.NotNull(repository.ProtectedCredential);
        // Through the same purpose GitClones unprotects with, so a purpose that drifted fails here too.
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(ControlDatabase.CredentialPurpose);
        Assert.Equal("a-token", protector.Unprotect(repository.ProtectedCredential));
    }

    /// <summary>What the key ring says about itself, which needs no host at all: it is a pure reading.</summary>
    private static LogProbe Reported(Dictionary<string, string?> settings)
    {
        var probe = new LogProbe();
        KeyRing.Settings(Settings.Of(settings)).Report(probe.CreateLogger(nameof(KeyRing)));
        return probe;
    }

    /// <summary>
    ///     What Data Protection was configured with under these settings. A host is built because
    ///     registration is what is under test, and never started: a started one would bind a port, and
    ///     the decision is already made by the time the services exist.
    /// </summary>
    private static async Task<KeyManagementOptions> OptionsAsync(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder();
        // Added last so that nothing on this machine — an environment variable naming a real account
        // above all — can decide what a test asserts on.
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddKeyRing();

        await using var app = builder.Build();
        return app.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
    }

    /// <summary>The blob an <c>AzureBlobXmlRepository</c> writes to, which it keeps to itself.</summary>
    private static BlobClient BlobOf(object repository) =>
        (BlobClient)repository.GetType()
            .GetField("_blobClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(repository)!;
}

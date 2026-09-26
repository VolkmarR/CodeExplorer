using System.Net;
using System.Text.RegularExpressions;

namespace CodeExplorer.Infrastructure;

/// <summary>Where a repository URL points, which decides whether it is accepted and whether it is read.</summary>
public enum RepositoryUrlKind
{
    /// <summary>Not accepted: unparseable, an unknown scheme, or carrying a password or token.</summary>
    Invalid,

    /// <summary>
    ///     A path or <c>file://</c> URL, which is on the server's own disk, or a remote whose host is
    ///     loopback, which reaches the server itself over the network: accepted only where
    ///     <see cref="RepositoryUrl.AllowLocalSetting" /> is true.
    /// </summary>
    Local,

    /// <summary>http(s), ssh, git, or scp-style <c>git@host:path</c>.</summary>
    Remote
}

/// <summary>
///     The one place that knows which URL shapes libgit2 accepts, and which of them reach the server's
///     own disk. <c>Control/</c> reads it to decide whether a repository may be added, and <c>Git/</c>
///     to refuse a stored local one once local repositories are switched off; one classifier keeps the
///     two from disagreeing about what counts as local (ADR-0005, revisited for GHSA-5373-pppr-q3q9).
/// </summary>
public static partial class RepositoryUrl
{
    /// <summary>
    ///     Off unless set to true, on a developer machine too: a local repository is any repository the
    ///     server's account can read, other projects' local copies under the data directory included, and
    ///     whoever may add a repository — anyone in the tenant, or anyone who can reach the port when
    ///     authentication is off — could have it cloned and read it through MCP (GHSA-5373-pppr-q3q9).
    /// </summary>
    public const string AllowLocalSetting = "Control:AllowLocalRepositories";

    public const string Rule =
        "URL must be http(s), ssh (ssh://git@host/path or git@host:path) or git, with no password or token "
        + $"in it; file URLs, local paths and loopback hosts are accepted only where {AllowLocalSetting} is "
        + "true. Put a token in the 'credential' field instead.";

    /// <summary>Why a local repository was refused, for the API's answer and the refresh's alike.</summary>
    public const string LocalRefusal =
        "It is a local path, a file URL or a remote on a loopback host, which reaches the server's own "
        + "machine, and local repositories are switched off on this server. Use the repository's http(s) or "
        + $"ssh remote on another host, or ask the operator to set {AllowLocalSetting} to true.";

    /// <summary>
    ///     Why a credential was refused for its URL, for the API's answer and the refresh's alike
    ///     (GHSA-4f8q-c6jj-fr44).
    /// </summary>
    public const string ClearTextCredentialRefusal =
        "A credential is only sent over https or ssh, where it is encrypted; use the https or ssh URL of this "
        + "repository.";

    /// <summary>
    ///     scp-style syntax as git accepts it: an optional user, a host, a colon, a path. A second colon
    ///     before the <c>@</c> would be a password and is left to fall through as invalid.
    ///     A host holds no backslash, and is not a lone letter: <c>C:repo</c>, <c>\\?\C:\repo</c> and
    ///     <c>dir\sub:x</c> are Windows paths, and libgit2 on Windows opens a URL that names an existing
    ///     directory through its local transport before it tries ssh, so reading any of them as scp-style
    ///     would be a way past <see cref="AllowLocalSetting" />. git itself reads a drive letter the same way.
    /// </summary>
    [GeneratedRegex(@"^(?:[^@:/\\]+@)?(?![A-Za-z]:)(?<host>[^@:/\\]+):(?![0-9]+/)[^:]*$")]
    private static partial Regex ScpSyntax { get; }

    /// <summary>
    ///     Whether this server accepts local repositories. A value that is not a boolean stops the server
    ///     with the configuration binder's message, which names the setting.
    /// </summary>
    public static bool LocalAllowed(IConfiguration configuration) =>
        configuration.GetValue<bool>(AllowLocalSetting);

    /// <summary>
    ///     Whether a credential would cross the network unencrypted: libgit2 hands it to an <c>http://</c>
    ///     remote as basic auth, and <c>git://</c> is just as unencrypted (GHSA-4f8q-c6jj-fr44). The API and
    ///     the refresh both ask this, so the pair refused on the way in is the pair skipped on the way out.
    ///     A prefix and not <see cref="Uri" />, because libgit2 picks its transport by prefix: a URL .NET
    ///     cannot parse, such as one with a space in the host, is invalid to <see cref="Classify" /> now,
    ///     but a server may have stored one before, and it is still an http URL to libgit2.
    /// </summary>
    public static bool SendsCredentialInClear(string? url, bool hasCredential)
    {
        if (!hasCredential) return false;
        string trimmed = url?.Trim() ?? "";
        return trimmed.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("git://", StringComparison.OrdinalIgnoreCase);
    }

    public static RepositoryUrlKind Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return RepositoryUrlKind.Invalid;
        url = url.Trim();

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // A Windows path like C:\repo parses as an absolute URI with a one-letter scheme.
            if (uri.Scheme.Length == 1 || uri.Scheme == "file") return RepositoryUrlKind.Local;
            bool accepted = uri.Scheme switch
            {
                // A user name is how ssh names the account (git@); a password after it is a secret.
                "ssh" or "git" => !uri.UserInfo.Contains(':'),
                "http" or "https" => uri.UserInfo.Length == 0,
                _ => false
            };
            // IdnHost and not Host: a fullwidth "ｌｏｃａｌｈｏｓｔ" is folded to the name a resolver looks up.
            return accepted ? KindByHost(uri.IdnHost) : RepositoryUrlKind.Invalid;
        }

        // A URL .NET could not parse, such as http://%6cocalhost/: read as scp-style its host would be
        // the scheme, and it would pass as remote while libgit2 still reads it as a URL, with a host
        // this classifier never saw.
        if (url.Contains("://")) return RepositoryUrlKind.Invalid;
        if (ScpSyntax.Match(url) is { Success: true } scp) return KindByHost(scp.Groups["host"].Value);
        // Whatever else fails to parse is a relative path. Anything with an '@' is more likely a
        // mistyped remote carrying a credential than a folder name, and is refused.
        return url.Contains('@') ? RepositoryUrlKind.Invalid : RepositoryUrlKind.Local;
    }

    /// <summary>
    ///     A remote on a loopback host is local: it reaches the server's own services over the network,
    ///     which is as much the server's own machine as a path is. Loopback is <c>localhost</c> or an
    ///     address in 127.0.0.0/8 or <c>::1</c>, IPv4-mapped included. <see cref="IPAddress.TryParse(string, out IPAddress)" />
    ///     reads the spellings a resolver does, decimal and hex among them, and a trailing dot is the DNS
    ///     root, not part of the name. A name that merely contains "localhost" or "127" is no address.
    /// </summary>
    private static RepositoryUrlKind KindByHost(string host)
    {
        host = host.TrimEnd('.');
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        bool loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || (IPAddress.TryParse(host, out var address)
                            && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));
        return loopback ? RepositoryUrlKind.Local : RepositoryUrlKind.Remote;
    }
}

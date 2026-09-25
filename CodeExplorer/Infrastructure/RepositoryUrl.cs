using System.Text.RegularExpressions;

namespace CodeExplorer.Infrastructure;

/// <summary>Where a repository URL points, which decides whether it is accepted and whether it is read.</summary>
public enum RepositoryUrlKind
{
    /// <summary>Not accepted: unparseable, an unknown scheme, or carrying a password or token.</summary>
    Invalid,

    /// <summary>
    ///     A path or <c>file://</c> URL: a repository on the server's own disk, accepted only where
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
        + $"in it; file URLs and local paths are accepted only where {AllowLocalSetting} is true. "
        + "Put a token in the 'credential' field instead.";

    /// <summary>Why a local repository was refused, for the API's answer and the refresh's alike.</summary>
    public const string LocalRefusal =
        "It is a local path or file URL, which reaches the server's own disk, and local repositories are "
        + "switched off on this server. Use the repository's http(s) or ssh remote, or ask the operator to set "
        + $"{AllowLocalSetting} to true.";

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
    [GeneratedRegex(@"^(?:[^@:/\\]+@)?(?![A-Za-z]:)[^@:/\\]+:(?![0-9]+/)[^:]*$")]
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
    ///     cannot parse, such as one with a space in the host, reads as scp-style to <see cref="Classify" />
    ///     and is still an http URL to libgit2.
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
            return uri.Scheme switch
            {
                // A user name is how ssh names the account (git@); a password after it is a secret.
                "ssh" or "git" when !uri.UserInfo.Contains(':') => RepositoryUrlKind.Remote,
                "http" or "https" when uri.UserInfo.Length == 0 => RepositoryUrlKind.Remote,
                _ => RepositoryUrlKind.Invalid
            };
        }

        if (ScpSyntax.IsMatch(url)) return RepositoryUrlKind.Remote;
        // Whatever else fails to parse is a relative path. Anything with an '@' is more likely a
        // mistyped remote carrying a credential than a folder name, and is refused.
        return url.Contains('@') ? RepositoryUrlKind.Invalid : RepositoryUrlKind.Local;
    }
}

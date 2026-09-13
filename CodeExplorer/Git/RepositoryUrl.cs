using System.Text.RegularExpressions;

namespace CodeExplorer;

/// <summary>Where a repository URL points, which decides both whether it is accepted and how it is cloned.</summary>
public enum RepositoryUrlKind
{
    /// <summary>Not accepted: unparseable, an unknown scheme, or carrying a password or token.</summary>
    Invalid,

    /// <summary>A path or <c>file://</c> URL. libgit2's local transport cannot clone shallow (ADR-0003).</summary>
    Local,

    /// <summary>http(s), ssh, git, or scp-style <c>git@host:path</c>. Cloned shallow.</summary>
    Remote
}

/// <summary>
///     The one place that knows which URL shapes libgit2 accepts, so the API's validation and the
///     clone's shallow decision cannot drift apart.
/// </summary>
public static partial class RepositoryUrl
{
    public const string Rule =
        "URL must be http(s), ssh (ssh://git@host/path or git@host:path), git, file, or a local path, "
        + "with no password or token in it. Put a token in the 'credential' field instead.";

    /// <summary>
    ///     scp-style syntax as git accepts it: an optional user, a host, a colon, a path. A second colon
    ///     before the <c>@</c> would be a password and is left to fall through as invalid.
    /// </summary>
    [GeneratedRegex("^(?:[^@:/]+@)?[^@:/]+:(?![0-9]+/)[^:]*$")]
    private static partial Regex ScpSyntax { get; }

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

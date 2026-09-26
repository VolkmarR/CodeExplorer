using System.Runtime.InteropServices;

namespace CodeExplorer.Git;

/// <summary>
///     How long a clone, a fetch or a read of a remote's references may wait on a remote that has gone
///     quiet before libgit2 gives up on it (#231). Cancellation cannot do this job: libgit2 hands control
///     back only through <c>OnTransferProgress</c>, which it calls while data is arriving, so a remote that
///     accepts the connection and then says nothing holds the refresh, and its project's refresh slot,
///     until the process restarts.
///     The limit is libgit2's own connect and read timeout, which LibGit2Sharp does not expose, so it is
///     set through the native options call directly. It bounds each wait for the network rather than
///     the whole transfer, so a large clone that keeps receiving is never cut off however long it runs.
///     It is one setting for the whole process, because that is what libgit2 has.
/// </summary>
internal static class TransferStallLimit
{
    /// <summary>
    ///     Five minutes. A remote packing a large repository can be silent for a while before its first
    ///     byte, and cutting that off would fail the refreshes that most need to run; a remote that says
    ///     nothing for five minutes is gone, and five minutes of a held refresh slot is a delay rather
    ///     than the outage an unbounded wait was.
    /// </summary>
    public const int DefaultSeconds = 300;

    public const string Setting = "Git:TransferStallSeconds";

    // The native library LibGit2Sharp ships and has already loaded; the name carries the libgit2 commit
    // it was built from, so it changes with the LibGit2Sharp package. A mismatch throws
    // DllNotFoundException the first time GitClones is built, which every refresh test does. BlobReader
    // binds to the same library, so this is the one place the name is kept.
    internal const string Library = "git2-5853918";

    // git_libgit2_opt_t in libgit2 1.7 and later, which the bundled 1.9 is.
    private const int _setServerConnectTimeout = 39;
    private const int _setServerTimeout = 41;

    /// <summary>
    ///     Reads the configured limit and hands it to libgit2 as its connect and read timeouts. Returns
    ///     the limit in seconds, for the sentence that reports a remote which ran into it.
    /// </summary>
    public static int Apply(IConfiguration configuration)
    {
        int seconds = configuration.GetValue(Setting, DefaultSeconds);
        // A zero would be libgit2's "no timeout", which is the hang this exists to end, so it is refused
        // rather than honoured; the upper bound keeps the milliseconds inside the int libgit2 takes.
        if (seconds is <= 0 or > int.MaxValue / 1000)
            throw new InvalidOperationException(
                $"{Setting} is {seconds}, but must be a number of seconds between 1 and {int.MaxValue / 1000}.");

        Set(_setServerConnectTimeout, seconds * 1000);
        Set(_setServerTimeout, seconds * 1000);
        return seconds;
    }

    private static void Set(int option, int milliseconds)
    {
        // git_libgit2_opts is variadic. Everywhere but Apple silicon a variadic int travels where a fixed
        // one would; there, variadic arguments go on the stack and the seven unused registers must be
        // filled first, which is what LibGit2Sharp's own osx-arm64 declarations do.
        int result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? SetOptionOnAppleSilicon(option, 0, 0, 0, 0, 0, 0, 0, milliseconds)
            : SetOption(option, milliseconds);
        if (result != 0)
            throw new InvalidOperationException(
                $"libgit2 refused its server timeout (option {option}, error {result}); the bundled libgit2 "
                + "is older than 1.7 or the option numbers above no longer match it.");
    }

    [DllImport(Library, EntryPoint = "git_libgit2_opts", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetOption(int option, int value);

    [DllImport(Library, EntryPoint = "git_libgit2_opts", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetOptionOnAppleSilicon(int option, nint unused1, nint unused2, nint unused3,
        nint unused4, nint unused5, nint unused6, nint unused7, int value);
}

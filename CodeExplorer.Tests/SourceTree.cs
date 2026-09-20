using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CodeExplorer.Tests;

/// <summary>
///     The server's source as text, for the rules only the source can carry: a module boundary is a
///     folder and a chokepoint is "one file calls this", and reflection can see neither.
/// </summary>
internal static partial class SourceTree
{
    /// <summary>
    ///     The source tree, not the output directory: these tests need the folders, and the build copies
    ///     assemblies rather than the layout they came from.
    /// </summary>
    public static string Server(string module = "") =>
        Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "CodeExplorer", module);

    /// <summary>This project's own source tree, for the rule that it mirrors the server's folders.</summary>
    public static string Tests() => Path.GetDirectoryName(ThisFile())!;

    /// <summary>
    ///     Every hand-written <c>.cs</c> file of the server. <c>bin</c> and <c>obj</c> are left out:
    ///     they hold generated sources and a rule about what this code says is not about those.
    /// </summary>
    public static IEnumerable<string> ServerFiles() =>
        Directory.EnumerateFiles(Server(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal));

    /// <summary>
    ///     Comments and string literals removed, so a type named in a doc comment explaining why a
    ///     module does <em>not</em> use it is not read as a reference to it.
    /// </summary>
    public static string Code(string file) => NonCode().Replace(File.ReadAllText(file), " ");

    private static string ThisFile([CallerFilePath] string path = "") => path;

    [GeneratedRegex("""//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\\n])*"|"{3}[\s\S]*?"{3}""")]
    private static partial Regex NonCode();
}

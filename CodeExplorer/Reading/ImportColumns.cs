namespace CodeExplorer;

/// <summary>
///     How the <c>imports</c> table spells the two things an edge is not a string: which shape of name
///     it imports, and how strongly the answer is held (ADR-0008). Each pair is written here and read
///     here, so neither direction can be changed without the other.
///     It sits in <c>Reading/</c> and not beside the builder that writes the rows, because the
///     writer is <c>Index/</c> and the reader is <c>Search/</c>: a codec on the builder made the import
///     tools name a build type to decode a column they had already read (ADR-0005). The column is what
///     both modules share, so the column's spelling is what they are handed.
/// </summary>
public static class ImportColumns
{
    /// <summary>
    ///     The two values the <c>shape</c> column holds, spelled once so the writer and the readers
    ///     agree. They are words rather than codes because a resolution failure is printed to an agent
    ///     beside them, and a row that says <c>path</c> explains the sentence "no file at this path"
    ///     where a <c>1</c> would not.
    /// </summary>
    public const string ModuleShape = "module";

    /// <inheritdoc cref="ModuleShape" />
    public const string PathShape = "path";

    /// <summary>How the <c>evidence</c> column spells an answer's strength (ADR-0008).</summary>
    public static string Column(Evidence evidence) => evidence == Evidence.Parsed ? "parsed" : "text";

    public static Evidence Strength(string column) => column == "parsed" ? Evidence.Parsed : Evidence.Text;

    /// <summary>The column's two directions, beside each other so neither can be changed alone.</summary>
    public static string Column(ImportShape shape) => shape == ImportShape.Module ? ModuleShape : PathShape;

    public static ImportShape Shape(string column) =>
        column == ModuleShape ? ImportShape.Module : ImportShape.Path;
}

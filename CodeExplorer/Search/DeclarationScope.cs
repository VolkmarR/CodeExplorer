namespace CodeExplorer;

/// <summary>
///     One line that could be a declaration, as the index handed it over: which line it is, how far it
///     is indented, and what the file's <see cref="ILanguageAnalyzer" /> said it declares. Either name
///     may be null — a member declaration names no type and a type declaration no member — and a line
///     that reads as both fills both.
/// </summary>
/// <param name="LineNumber">1-based, as everything an agent is shown is.</param>
/// <param name="Indent">Columns of leading whitespace, a tab counted as four.</param>
/// <param name="Declared">What the line declares.</param>
internal sealed record DeclarationLine(int LineNumber, int Indent, Declared Declared);

/// <summary>
///     Which declaration a reference sits inside, worked out from indentation. What a line declares
///     is a language question and comes from the analyser (ADR-0008); how the declarations above a
///     line nest is one this cannot ask yet, so it assumes the C-family convention that indentation
///     shows nesting.
///     That assumption is false for the SQL and PL/SQL profiles registered here, for Delphi's
///     <c>begin</c>/<c>end</c>, and for legacy X# written flat — where it costs a wrong label on a
///     line, never a wrong kind. It sits on the search side rather than behind the seam because
///     <c>find_definition</c> (#54) needs the same structure and will be what moves it there, at
///     which point a Delphi profile or a parser can answer it properly.
/// </summary>
internal static class DeclarationScope
{
    /// <summary>
    ///     The nearest enclosing declaration above a reference, as <c>Type.Member</c>. A declaration
    ///     counts only if it is indented less than the reference itself, and the type only if it is
    ///     further out than the member.
    /// </summary>
    /// <param name="declarations">This file's declaration lines in ascending line order.</param>
    /// <param name="lineNumber">The line the reference is on.</param>
    /// <param name="indent">That line's indent, from the content the search already read.</param>
    public static string? Enclosing(IReadOnlyList<DeclarationLine> declarations, int lineNumber, int indent)
    {
        string? member = null, type = null;
        int memberIndent = int.MaxValue;

        for (int i = declarations.Count - 1; i >= 0; i--)
        {
            var declaration = declarations[i];
            if (declaration.LineNumber >= lineNumber) continue;

            if (member is null && declaration.Declared.Member is not null && declaration.Indent < indent)
            {
                member = declaration.Declared.Member;
                memberIndent = declaration.Indent;
                continue;
            }

            if (declaration.Declared.Type is not null && declaration.Indent < memberIndent &&
                declaration.Indent < indent)
            {
                type = declaration.Declared.Type;
                break;
            }
        }

        return (type, member) switch
        {
            (null, null) => null,
            (not null, null) => type,
            (null, _) => member,
            _ => $"{type}.{member}"
        };
    }
}

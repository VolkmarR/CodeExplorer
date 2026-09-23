namespace CodeExplorer.Infrastructure;

/// <summary>
///     What every index-backed answer is: its result, or a <see cref="Problem" />. A semantic failure
///     is an answer, never an exception (CODING_STANDARDS). One shape for every search, file read and
///     history question, so a caller tells a problem from a result the same way whichever it asked,
///     and a new answer inherits the shape rather than copying it.
///     It lives in <c>Infrastructure/</c> and not in <c>Search/</c> because every module that opens an
///     index refuses the same way — the MCP tools, the operator's pages and the searches alike — and
///     <c>Control/</c> and <c>Operator/</c> may not name a <c>Search/</c> type (ADR-0005).
/// </summary>
public abstract record Outcome;

/// <summary>
///     Why a problem is one, for the caller that has to map it onto something other than prose. HTTP
///     answers a missing index with a 404 the browse view draws as "nothing to browse yet", a file or
///     commit that is not there with a 404 too, and everything else with a 400; an MCP tool hands over
///     the explanation and never looks.
/// </summary>
public enum ProblemKind
{
    /// <summary>The request was malformed, or named a repository or directory the index does not have.</summary>
    Invalid,

    /// <summary>
    ///     The request named a file or a commit that is not in the index. Told apart from
    ///     <see cref="Invalid" /> because a link to it is a page that is not there, not a request that
    ///     was wrong.
    /// </summary>
    Missing,

    /// <summary>
    ///     The request named a path this project's history records and HEAD no longer holds, asked of a
    ///     tool that answers from the current file tree. Told apart from <see cref="Missing" /> because
    ///     the caller spelled a real path: the answer is "that file is gone, read its history instead",
    ///     not "no such path" (#136). A caller that cannot tell the two apart offers spelling advice for
    ///     a spelling that was right.
    /// </summary>
    Historical,

    /// <summary>The project has no index to read from: never built, or still building the first one.</summary>
    NoIndex
}

/// <summary>What went wrong and what to try instead, in agent-facing prose.</summary>
public sealed record Problem(string Explanation, ProblemKind Kind = ProblemKind.Invalid) : Outcome;

namespace CodeExplorer;

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
///     answers a missing index with a 404 the browse view draws as "nothing to browse yet", and every
///     other problem with a 400; an MCP tool hands over the explanation and never looks.
/// </summary>
public enum ProblemKind
{
    /// <summary>The request named something the index does not have, or was malformed.</summary>
    Invalid,

    /// <summary>The project has no index to read from: never built, or still building the first one.</summary>
    NoIndex
}

/// <summary>What went wrong and what to try instead, in agent-facing prose.</summary>
public sealed record Problem(string Explanation, ProblemKind Kind = ProblemKind.Invalid) : Outcome;

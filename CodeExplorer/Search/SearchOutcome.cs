namespace CodeExplorer;

/// <summary>
///     What every index search answers with: its result, or a <see cref="SearchProblem" />. A
///     semantic failure is an answer, never an exception (CODING_STANDARDS). One shape for grep,
///     list_matches and find_references, so a caller tells a problem from a result the same way
///     whichever search it asked, and a fourth search inherits the shape rather than copying it.
/// </summary>
public abstract record SearchOutcome;

/// <summary>What went wrong and what to try instead, in agent-facing prose.</summary>
public sealed record SearchProblem(string Explanation) : SearchOutcome;

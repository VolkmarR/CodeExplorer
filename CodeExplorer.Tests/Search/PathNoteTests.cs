using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The closing note every path-scoped reply carries, tested at the seam it now has rather than
///     through a built index. Five reads used to compose this note three different ways and the
///     differences between them were only ever reachable by building a repository, indexing it and
///     calling a tool; what is left is a pure function over a scope, so what each read says about a
///     rename is a table.
///     The cases that matter are the ones no branch of a tool reply can reach on its own: that a scope
///     with nothing to note gets its body back untouched, that the three sentences keep their order,
///     and that the two reads which state the caveat in their reply are the two that do.
/// </summary>
public sealed class PathNoteTests
{
    private static PathLineage Chain(params (string Spelled, int Commits)[] hops) =>
        new([.. hops.Select(hop => new PreviousPath(hop.Spelled, hop.Spelled, hop.Commits))], 0,
            hops.Sum(hop => hop.Commits) + 1, [.. hops.Select(hop => hop.Spelled)]);

    /// <summary>
    ///     Every read has prose to end a chain with. The switch holding them needs a default arm —
    ///     CS8524 counts a cast int as an uncovered value and this build treats that as an error — so
    ///     the compiler cannot be what catches a seventh read added without its sentence. This is: it
    ///     walks the enum rather than a list spelled out here, so declaring a member is what makes it
    ///     fail.
    /// </summary>
    [Fact]
    public void Every_read_has_a_tail()
    {
        foreach (var tool in Enum.GetValues<PathNoteFor>())
        {
            string reply = PathNote.After("body.", new ScopeNote(Chain(("one/old", 40))), tool);

            const string total = "recorded across the whole chain;";
            Assert.Contains(total, reply, StringComparison.Ordinal);
            // The tail is everything after the combined total, and an unwritten one would end it there.
            string tail = reply[(reply.IndexOf(total, StringComparison.Ordinal) + total.Length)..].Trim();
            Assert.False(tail.Length == 0, $"{tool} ends its chain note with nothing.");
        }
    }

    /// <summary>
    ///     The guarantee that keeps every reply this note does not concern byte-for-byte what it was.
    ///     It is the whole reason the note could be moved out of the branches: a wrap that altered a
    ///     body with nothing to say would have changed 18 tools' output, not five.
    /// </summary>
    [Fact]
    public void A_scope_with_nothing_to_note_gets_its_body_back_untouched()
    {
        const string body = "3 commits under 'main/src', newest first:\n\nabc12345  Do the thing\n";

        Assert.Equal(body, PathNote.After(body, null, PathNoteFor.GitLog));
        Assert.Equal(body, PathNote.After(body, new ScopeNote(null), PathNoteFor.HotFiles));
        Assert.Equal(body, PathNote.After(body, new ScopeNote(null), PathNoteFor.CoChangedRanking));
    }

    /// <summary>
    ///     A chain with no hop in it is a scope that was never renamed, which is the common answer and
    ///     must read exactly as it did before there was a lineage table at all.
    /// </summary>
    [Fact]
    public void An_empty_chain_is_no_chain()
    {
        const string body = "No commits are recorded under 'main/src'.";

        Assert.Equal(body, PathNote.After(body, new ScopeNote(Chain()), PathNoteFor.FileHistory));
    }

    /// <summary>
    ///     The note is a paragraph, whatever the body ended in. A line of prose tacked onto the last row
    ///     of a listing reads as part of the row, and the two reads that used to concatenate it onto a
    ///     sentence were the two whose empty answers are the ones an agent most needs to read twice.
    /// </summary>
    [Theory]
    [InlineData("No commits are recorded under 'one/src/Gone.cs'.")]
    [InlineData("3 commits, newest first:\n\nabc12345  Do the thing\n")]
    [InlineData("A body that ends in several newlines.\n\n\n")]
    public void The_note_is_its_own_paragraph_however_the_body_ended(string body)
    {
        string reply = PathNote.After(body, new ScopeNote(null, "one/src/Gone.cs"), PathNoteFor.GitLog);

        Assert.Contains("\n\nNothing is at 'one/src/Gone.cs' now", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\nNothing is at", reply, StringComparison.Ordinal);
        Assert.EndsWith("\n", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The order the three sentences are in, which is a decision and not an accident: the gone
    ///     sentence is a fact about this call, the caveat is a fact about every path scope, and the
    ///     chain is the particular fact worth acting on.
    /// </summary>
    [Fact]
    public void The_gone_sentence_leads_the_caveat_follows_and_the_chain_is_last()
    {
        string reply = PathNote.After("No commits are recorded under 'one/legacy'.",
            new ScopeNote(Chain(("one/old", 40)), "one/legacy"), PathNoteFor.GitLog);

        int gone = reply.IndexOf("Nothing is at 'one/legacy' now", StringComparison.Ordinal);
        int caveat = reply.IndexOf("matched by the path each commit recorded", StringComparison.Ordinal);
        int renamed = reply.IndexOf("This scope was renamed", StringComparison.Ordinal);

        Assert.True(gone >= 0 && caveat > gone && renamed > caveat, reply);
    }

    /// <summary>
    ///     Which reads state the caveat in the reply. <c>hot_files</c> states it in its
    ///     <c>directory</c> argument's description, where a model reads it before calling rather than
    ///     after, and <c>file_history</c> answers for one exact path. Two of five, said here so that the
    ///     split is a decision a reader can see rather than a sentence some replies drifted out of.
    /// </summary>
    [Theory]
    [InlineData(PathNoteFor.GitLog, true)]
    [InlineData(PathNoteFor.Authors, true)]
    [InlineData(PathNoteFor.FileHistory, false)]
    [InlineData(PathNoteFor.HotFiles, false)]
    [InlineData(PathNoteFor.CoChangedRanking, false)]
    [InlineData(PathNoteFor.CoChangedThin, false)]
    public void Only_git_log_and_authors_state_the_recorded_path_caveat(PathNoteFor tool, bool states)
    {
        string reply = PathNote.After("body.", new ScopeNote(Chain(("one/old", 40))), tool);

        Assert.Equal(states, reply.Contains("matched by the path each commit recorded", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A scope HEAD no longer holds says so in one wording, whichever read is answering. A second
    ///     spelling of one fact is how an agent ends up believing there are two, and this sentence used
    ///     to be appended by <c>file_history</c> inside one of its own branches.
    /// </summary>
    [Theory]
    [InlineData(PathNoteFor.GitLog)]
    [InlineData(PathNoteFor.Authors)]
    [InlineData(PathNoteFor.FileHistory)]
    public void A_gone_scope_is_named_the_same_way_by_every_read_that_answers_for_one(PathNoteFor tool)
    {
        string reply = PathNote.After("body.", new ScopeNote(null, "one/src/Gone.cs"), tool);

        Assert.Contains(
            "Nothing is at 'one/src/Gone.cs' now (no longer at HEAD) — a later commit deleted it or "
            + "renamed it away, so there is nothing there to read; this scope is its recorded history.",
            reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     All three parts of the chain or it is not worth saying (#131): the previous path, the total
    ///     across the chain, and the call that reads the previous path. Naming the path and withholding
    ///     the total reproduces the original fault one level up.
    /// </summary>
    [Fact]
    public void The_chain_names_the_path_the_total_and_the_call_that_reads_it()
    {
        string reply = PathNote.After("9 commits under 'one/new'.", new ScopeNote(Chain(("one/old", 1252))),
            PathNoteFor.GitLog);

        Assert.Contains("its content was at 'one/old' before, where 1252 commits are recorded",
            reply, StringComparison.Ordinal);
        Assert.Contains("1253 commits are recorded across the whole chain", reply, StringComparison.Ordinal);
        Assert.Contains("Call git_log with path=\"one/old\" to read it.", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A path renamed twice has a chain, oldest last. An agent that read only the newest hop would
    ///     stop one rename short of the history it asked for.
    /// </summary>
    [Fact]
    public void Every_hop_after_the_first_is_named_oldest_last()
    {
        string reply = PathNote.After("body.", new ScopeNote(Chain(("one/middle", 40), ("one/oldest", 7))),
            PathNoteFor.HotFiles);

        Assert.Contains("its content was at 'one/middle' before", reply, StringComparison.Ordinal);
        Assert.Contains("Before that, 'one/oldest' (7 commits).", reply, StringComparison.Ordinal);
        Assert.True(reply.IndexOf("one/middle", StringComparison.Ordinal)
                    < reply.IndexOf("one/oldest", StringComparison.Ordinal), reply);
    }

    /// <summary>
    ///     A capped chain says how many earlier names it did not print, because a note that silently
    ///     stopped would be the fault it exists to fix, one level further up again.
    /// </summary>
    [Fact]
    public void A_capped_chain_says_how_many_it_did_not_print()
    {
        var lineage = new PathLineage([new PreviousPath("one/old", "one/old", 40)], 3, 60, ["one/old"]);

        string reply = PathNote.After("body.", new ScopeNote(lineage), PathNoteFor.Authors);

        Assert.Contains("3 further earlier paths not shown.", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Each read names the call that reads the previous path, and names one it answers for.
    ///     <c>co_changed</c> is the exception in both directions: it refuses a path HEAD no longer holds
    ///     (#136), so it names no call, and it is the one read that follows a rename rather than
    ///     signalling it (#143), so it must not claim the count above is this path's alone.
    /// </summary>
    [Theory]
    [InlineData(PathNoteFor.GitLog, "Call git_log with path=\"one/old\" to read it.")]
    [InlineData(PathNoteFor.Authors, "Call authors with path=\"one/old\" to read it.")]
    [InlineData(PathNoteFor.FileHistory, "Call file_history with path=\"one/old\" to read it.")]
    [InlineData(PathNoteFor.HotFiles, "Call hot_files with directory=\"one/old\" to rank it.")]
    public void A_read_that_signals_a_rename_calls_the_count_literal_and_names_the_call(PathNoteFor tool,
        string call)
    {
        string reply = PathNote.After("body.", new ScopeNote(Chain(("one/old", 40))), tool);

        Assert.Contains("the count above is this path's alone", reply, StringComparison.Ordinal);
        Assert.Contains(call, reply, StringComparison.Ordinal);
    }

    /// <inheritdoc cref="A_read_that_signals_a_rename_calls_the_count_literal_and_names_the_call" />
    [Theory]
    [InlineData(PathNoteFor.CoChangedRanking, "The ranking above spans that chain")]
    [InlineData(PathNoteFor.CoChangedThin, "The pairing did span that chain")]
    public void Co_changed_follows_the_rename_and_says_so_in_two_forms(PathNoteFor tool, string said)
    {
        string reply = PathNote.After("body.", new ScopeNote(Chain(("one/old", 40))), tool);

        Assert.Contains(said, reply, StringComparison.Ordinal);
        Assert.DoesNotContain("the count above is this path's alone", reply, StringComparison.Ordinal);
        // It sends the caller nowhere, because a previous path is one it is specified to refuse.
        Assert.DoesNotContain("Call co_changed", reply, StringComparison.Ordinal);
        Assert.Contains("within the window", reply, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The thin form must not claim there was nothing more to do: those replies end by telling the
    ///     caller to raise <c>days</c> or to read <c>file_history</c>, and a note contradicting the
    ///     sentence directly above it is worse than no note (#143).
    /// </summary>
    [Fact]
    public void The_thin_co_changed_form_does_not_claim_there_is_nothing_left_to_do()
    {
        string reply = PathNote.After("No file changed alongside it.\n",
            new ScopeNote(Chain(("one/old", 40))), PathNoteFor.CoChangedThin);

        Assert.DoesNotContain("no second call to make", reply, StringComparison.Ordinal);
        Assert.Contains("what is missing here is not the rename", reply, StringComparison.Ordinal);
    }
}

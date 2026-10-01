using CodeExplorer.Control;
using CodeExplorer.Index;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     Writes that hang a row off a project, against that project's delete. The rows carry no foreign
///     key, so a delete committing between a write's existence check and its insert left the row behind,
///     and a project created later under the slug inherited it — a repository with the deleted
///     project's remote and credential (GHSA-253f-grfp-cqq7). The project gate in
///     <see cref="ControlDatabase" /> closes that race; what is tested here is what a test can reach
///     without holding a write half-way: the check against a project already deleted, and the clear of
///     rows an earlier race left behind.
/// </summary>
public sealed class ProjectDeleteRaceTests : IDisposable
{
    private const string Remote = "https://example.com/team/one.git";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    private ControlDatabase Control => _host.Services.GetRequiredService<ControlDatabase>();

    [Fact]
    public async Task A_repository_is_added_to_a_project_that_exists()
    {
        await _host.CreateProjectAsync("alpha");

        var (outcome, added) = await Control.AddRepositoryAsync("alpha", "one", Remote, "token", Ct);

        Assert.Equal(AddRepositoryOutcome.Created, outcome);
        Assert.NotNull(added);
        Assert.Equal(["one"], (await Control.ListRepositoriesAsync("alpha", Ct)).Select(r => r.Slug));
    }

    /// <summary>
    ///     Found once, so the cache holds it, and then deleted: the add must ask again rather than
    ///     believe the cache.
    /// </summary>
    [Fact]
    public async Task A_repository_is_refused_for_a_project_that_was_deleted()
    {
        await _host.CreateProjectAsync("alpha");
        Assert.NotNull(await Control.FindAsync("alpha", Ct));
        Assert.True(await Control.DeleteProjectAsync("alpha", Ct));

        var (outcome, added) = await Control.AddRepositoryAsync("alpha", "one", Remote, "token", Ct);

        Assert.Equal(AddRepositoryOutcome.NoProject, outcome);
        Assert.Null(added);
        Assert.Empty(await Control.ListRepositoriesAsync("alpha", Ct));
    }

    [Fact]
    public async Task Excluded_paths_are_saved_for_a_project_that_exists_and_refused_for_one_that_does_not()
    {
        await _host.CreateProjectAsync("alpha");

        var (saved, patterns, _) = await Control.SetExcludedPathsAsync("alpha", ["**/*.rc"], Ct);
        var (missing, _, _) = await Control.SetExcludedPathsAsync("ghost", ["**/*.rc"], Ct);

        Assert.Equal(ExcludedPathsOutcome.Saved, saved);
        Assert.Equal(["**/*.rc"], patterns);
        Assert.Equal(["**/*.rc"], await Control.ExcludedPathsAsync("alpha", Ct));
        Assert.Equal(ExcludedPathsOutcome.NoProject, missing);
        Assert.Empty(await Control.ExcludedPathsAsync("ghost", Ct));
    }

    /// <summary>
    ///     Rows the race left in a database before the gate closed it, written behind the control
    ///     database's back because nothing through it can orphan a row any more.
    /// </summary>
    [Fact]
    public async Task A_project_created_over_rows_a_deleted_one_left_behind_starts_without_them()
    {
        // Resolved first, which starts the server and so creates the file the connection opens.
        var control = Control;
        await using (var connection = await _host.OpenControlDatabaseAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                                   INSERT INTO repositories VALUES ('alpha', 'one', '{Remote}', 'protected-token');
                                   INSERT INTO excluded_paths VALUES ('alpha', 0, '**/*.rc');
                                   """;
            await command.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal(CreateProjectOutcome.Created, await control.CreateAsync("alpha", "Alpha", false, Ct));

        // Inherited, the repository would be cloned with the deleted project's credential.
        Assert.Empty(await control.ListRepositoriesAsync("alpha", Ct));
        Assert.Empty(await control.ExcludedPathsAsync("alpha", Ct));
    }

    /// <summary>A create refused because the slug is taken is rolled back, and must not clear the owner's rows.</summary>
    [Fact]
    public async Task A_create_refused_for_a_taken_slug_leaves_that_project_s_rows_alone()
    {
        await _host.CreateProjectAsync("alpha");
        Assert.Equal(AddRepositoryOutcome.Created, (await Control.AddRepositoryAsync("alpha", "one", Remote, "token", Ct)).Outcome);
        Assert.Equal(ExcludedPathsOutcome.Saved, (await Control.SetExcludedPathsAsync("alpha", ["**/*.rc"], Ct)).Outcome);

        Assert.Equal(CreateProjectOutcome.SlugTaken, await Control.CreateAsync("alpha", "Alpha again", false, Ct));

        Assert.Equal(["one"], (await Control.ListRepositoriesAsync("alpha", Ct)).Select(r => r.Slug));
        Assert.Equal(["**/*.rc"], await Control.ExcludedPathsAsync("alpha", Ct));
    }
}

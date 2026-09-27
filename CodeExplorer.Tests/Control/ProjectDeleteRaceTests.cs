using CodeExplorer.Control;
using CodeExplorer.Index;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     A write that hangs a row off a project, racing that project's delete. The rows carry no foreign
///     key, so a delete committing between a write's existence check and its insert left the row behind,
///     and a project created later under the slug inherited it — a repository with the deleted
///     project's remote and credential (GHSA-253f-grfp-cqq7). Each race holds the write at that point
///     through <see cref="ControlDatabase.ProjectChecked" />, starts the delete, and lets the write go
///     once the delete has either finished or queued behind it: deterministic both ways, so without the
///     project gate the delete finishes first and the row is orphaned every time.
/// </summary>
public sealed class ProjectDeleteRaceTests : IDisposable
{
    private const string Remote = "https://example.com/team/one.git";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestHost _host = new(SearchEngine.Substring);

    public void Dispose() => _host.Dispose();

    private ControlDatabase Control => _host.Services.GetRequiredService<ControlDatabase>();

    [Fact]
    public async Task A_repository_added_while_its_project_is_deleted_goes_with_the_project()
    {
        await _host.CreateProjectAsync("alpha");

        var (outcome, _) = await RaceDeleteAsync(() =>
            Control.AddRepositoryAsync("alpha", "one", Remote, "token", Ct));

        // The add held the gate first, so it lands before the delete and the delete takes it along.
        Assert.Equal(AddRepositoryOutcome.Created, outcome);
        Assert.Empty(await Control.ListRepositoriesAsync("alpha", Ct));
        await AssertRecreatedProjectStartsEmptyAsync();
    }

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

    /// <summary>
    ///     Runs <paramref name="write" /> against project alpha, holds it once it has found the project,
    ///     deletes the project, and releases the write when the delete has finished or is queued
    ///     behind it. Answers the write's result once both are done.
    /// </summary>
    private async Task<T> RaceDeleteAsync<T>(Func<Task<T>> write)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Control.ProjectChecked = async () =>
        {
            reached.TrySetResult();
            await release.Task;
        };
        Control.ProjectGateQueued = () => queued.TrySetResult();
        try
        {
            var writing = write();
            await reached.Task.WaitAsync(Ct);

            var deleting = Control.DeleteProjectAsync("alpha", Ct);
            await Task.WhenAny(deleting, queued.Task).WaitAsync(Ct);

            release.SetResult();
            var written = await writing;
            Assert.True(await deleting);
            return written;
        }
        finally
        {
            // Released even when the test failed, so nothing is left waiting on the gate.
            release.TrySetResult();
            Control.ProjectChecked = null;
            Control.ProjectGateQueued = null;
        }
    }

    /// <summary>The same slug again is a new project, and opens with nothing of the deleted one.</summary>
    private async Task AssertRecreatedProjectStartsEmptyAsync()
    {
        await _host.CreateProjectAsync("alpha");

        Assert.Empty(await Control.ListRepositoriesAsync("alpha", Ct));
        Assert.Empty(await Control.ExcludedPathsAsync("alpha", Ct));
    }
}

using System.Globalization;
using CodeExplorer.Index;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     The repositories the history tool tests are asked about, and the projects built out of them.
///     They are here rather than beside one of the tests because most of them are read by more than
///     one tool: a churn ranking, a scoped log and a previous-path note all want the same cutover.
///     Every project here is read-only. A test that refreshes again over new commits, or hollows a
///     commit out with SQL, builds a project of its own — on its class's host, not on a server of its
///     own — because sharing one of these with a writer would make the reader next to it depend on
///     which ran first.
/// </summary>
internal static class HistoryFixtures
{
    /// <summary>One repository, two commits, one file changed by the second. The plainest log there is.</summary>
    public const string Alpha = "alpha";

    /// <summary>Something to rank: one ancient commit, four ten days later, one deletion.</summary>
    public const string Churn = "churn";

    /// <summary><see cref="Churn" /> with a second repository, for the answers that span both.</summary>
    public const string Mixed = "mixed";

    /// <summary>Three commits with a body, a rename and a deletion: what <c>commit</c> is asked about.</summary>
    public const string Commits = "commits";

    /// <summary>Files that move together, one that moves alone, and one mass commit.</summary>
    public const string Coupled = "coupled";

    /// <summary>A directory cutover: every path renamed in one commit, no content changed.</summary>
    public const string Renames = "renames";

    /// <summary>
    ///     Builds one of the shared projects by name. A switch and not a dictionary of delegates,
    ///     because a fixture naming a project this does not know is a typo and should not compile away
    ///     into an empty host.
    /// </summary>
    public static Task BuildAsync(TestHost host, string project) => project switch
    {
        Alpha => BuildAlphaProjectAsync(host, project),
        Churn => BuildChurnProjectAsync(host, project, false),
        Mixed => BuildChurnProjectAsync(host, project, true),
        Commits => BuildCommitProjectAsync(host, project),
        Coupled => BuildCoupledProjectAsync(host, project),
        Renames => BuildRenamedProjectAsync(host, project),
        _ => throw new ArgumentOutOfRangeException(nameof(project), project, "No shared project by that name.")
    };

    /// <summary>
    ///     Routes an inline array argument through a parameter so CA1861 does not ask for a static
    ///     field per call.
    /// </summary>
    public static string[] Paths(params string[] paths) => paths;

    /// <inheritdoc cref="Alpha" />
    public static async Task BuildAlphaProjectAsync(TestHost host, string project)
    {
        string source = host.CreateEmptyGitRepository("one");
        host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond\nthird\n" },
            "Add the validator", "Ada", "ada@example.invalid", 0);
        host.CommitToGitRepositoryAs("one",
            new Dictionary<string, string> { ["src/Check.cs"] = "first\nsecond-changed\nthird\n" },
            "Tighten the check", "Grace", "grace@example.invalid", 1);
        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>
    ///     A repository with something to rank: one old commit, then four ten days later that touch
    ///     three files unequally, then one that deletes a fourth. Every date is decades before today, so
    ///     a window measured from the clock rather than from the history would rank nothing at all.
    /// </summary>
    public static async Task BuildChurnProjectAsync(TestHost host, string project, bool withSecondRepository)
    {
        const int tenDays = 10 * 24 * 60;
        string source = host.CreateEmptyGitRepository(project + "-one");
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["old/Ancient.cs"] = "one\n" },
            "Import the old code", "Ada", "ada@example.invalid", 0);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string>
            {
                ["docs/Note.md"] = "note\n",
                ["src/Cold.cs"] = "cold\n",
                ["src/Gone.cs"] = "gone\n",
                ["src/Hot.cs"] = "a\nb\nc\n"
            },
            "Add the module", "Ada", "ada@example.invalid", tenDays);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.cs"] = "a\nb2\nc\n" },
            "Fix the check", "Grace", "grace@example.invalid", tenDays + 1);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Hot.cs"] = "a\nb3\nc\n", ["src/Cold.cs"] = "cold2\n" },
            "Tighten it again", "Grace", "grace@example.invalid", tenDays + 2);
        host.RemoveInGitRepositoryAs(project + "-one", ["src/Gone.cs"], "Drop the dead file", "Grace",
            "grace@example.invalid", tenDays + 3);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        if (withSecondRepository)
            await host.AddRepositoryAsync(project, "two",
                host.CreateGitRepository(project + "-two", new Dictionary<string, string>
                    { ["src/Other.cs"] = "other\n" }));
        await host.RefreshAsync(project);
    }

    /// <summary>
    ///     One repository with a commit worth asking about: a message with a body under its subject, a
    ///     file it modified and one it added, and a later commit that deletes the second so a path the
    ///     index no longer holds is in the list.
    /// </summary>
    public static async Task BuildCommitProjectAsync(TestHost host, string project)
    {
        string name = project + "-one";
        string source = host.CreateEmptyGitRepository(name);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["docs/Note.md"] = "note\n", ["src/Api.cs"] = "a\n" },
            "Add the module", "Ada", "ada@example.invalid", 0);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a2\n", ["src/Gone.cs"] = "gone\n" },
            "BugFix 558185 - tighten the check\n\nThe validator accepted an empty name.\n",
            "Grace", "grace@example.invalid", 1);
        host.RemoveInGitRepositoryAs(name, ["src/Gone.cs"], "Drop the dead file", "Grace",
            "grace@example.invalid", 2);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>
    ///     A repository with coupling to find. Api.cs moves with Store.cs three times, with Dto.cs
    ///     twice and with Old.cs once before that file is deleted; Lonely.cs moves on its own; and one
    ///     reformat touches Api.cs and a dozen vendored paths at once, which is the mass commit the
    ///     ceiling exists for. Ancient.cs sits ten days before the rest so a narrow window can miss it.
    /// </summary>
    public static async Task BuildCoupledProjectAsync(TestHost host, string project)
    {
        const int tenDays = 10 * 24 * 60;
        string name = project + "-one";
        string source = host.CreateEmptyGitRepository(name);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["old/Ancient.cs"] = "one\n" },
            "Import the old code", "Ada", "ada@example.invalid", 0);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string>
            {
                ["src/Api.cs"] = "a\n",
                ["src/Dto.cs"] = "d\n",
                ["src/Old.cs"] = "o\n",
                ["src/Store.cs"] = "s\n"
            },
            "Add the feature", "Ada", "ada@example.invalid", tenDays);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a2\n", ["src/Store.cs"] = "s2\n" },
            "Extend the feature", "Grace", "grace@example.invalid", tenDays + 1);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a3\n", ["src/Store.cs"] = "s3\n" },
            "Extend it again", "Grace", "grace@example.invalid", tenDays + 2);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Api.cs"] = "a4\n", ["src/Dto.cs"] = "d2\n" },
            "Reshape the payload", "Grace", "grace@example.invalid", tenDays + 3);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string> { ["src/Lonely.cs"] = "l\n" },
            "Add a file nothing moves with", "Ada", "ada@example.invalid", tenDays + 4);

        var reformat = new Dictionary<string, string> { ["src/Api.cs"] = "a5\n" };
        for (int i = 1; i <= 12; i++)
            reformat[string.Create(CultureInfo.InvariantCulture, $"vendor/Bulk{i:00}.cs")] = $"bulk {i}\n";
        host.CommitToGitRepositoryAs(name, reformat, "Reformat everything", "Ada", "ada@example.invalid",
            tenDays + 5);
        host.RemoveInGitRepositoryAs(name, ["src/Old.cs"], "Drop the old file", "Grace",
            "grace@example.invalid", tenDays + 6);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>
    ///     A cutover: `model/` built up over several commits, then moved to `src/Model` wholesale in one,
    ///     beside an `src/Api` that never moved and an `src/Odds` that one file later leaves.
    /// </summary>
    public static async Task BuildRenamedProjectAsync(TestHost host, string project)
    {
        string source = host.CreateEmptyGitRepository(project + "-one");
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string>
            {
                ["model/Contact.cs"] = "contact\n",
                ["model/Order.cs"] = "order\n",
                ["src/Api/Handler.cs"] = "handler\n",
                ["src/Odds/Helper.cs"] = "helper\n"
            },
            "Import the model", "Ada", "ada@example.invalid", 30000);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["model/Contact.cs"] = "contact\nmore\n" },
            "Add the contact feature", "Ada", "ada@example.invalid", 30001);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["model/Order.cs"] = "order\nmore\n" },
            "Extend the order", "Grace", "grace@example.invalid", 30002);
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["src/Api/Handler.cs"] = "handler\nmore\n" },
            "Tighten the handler", "Grace", "grace@example.invalid", 30003);
        // Alone in its own commit, so the only commit it ever shares with Contact.cs is the cutover.
        // It is what says the rename is excluded from the pairing rather than merely diluted: a file
        // that moved beside the anchor and nothing more must not rank as coupled to it.
        host.CommitToGitRepositoryAs(project + "-one",
            new Dictionary<string, string> { ["model/Solo.cs"] = "solo\n" },
            "Add the solo model", "Ada", "ada@example.invalid", 30004);
        // The cutover, as the real one arrived: one commit, every path renamed, no content changed.
        host.MoveInGitRepositoryAs(project + "-one",
            new Dictionary<string, string>
            {
                ["model/Contact.cs"] = "src/Model/Contact.cs",
                ["model/Order.cs"] = "src/Model/Order.cs",
                ["model/Solo.cs"] = "src/Model/Solo.cs"
            },
            "Merged PR 39331: Moved Model to src\\Model", "Grace", "grace@example.invalid", 31000);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>A repository whose regenerated output and migrations outrank its hand-written code.</summary>
    public static async Task BuildGeneratedProjectAsync(TestHost host, string project)
    {
        const int tenDays = 10 * 24 * 60;
        string name = project + "-one";
        string source = host.CreateEmptyGitRepository(name);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string>
            {
                ["src/Service.cs"] = "a\n",
                ["src/Model.g.cs"] = "g\n",
                ["migrations/0001.sql"] = "create table t (id integer);\n"
            },
            "Add the module", "Ada", "ada@example.invalid", tenDays);
        // Two regenerations and one migration against one hand-written change: the shape the ranking
        // gets wrong, and it gets it wrong by counting correctly.
        for (int i = 1; i <= 3; i++)
            host.CommitToGitRepositoryAs(name,
                new Dictionary<string, string>
                {
                    ["src/Model.g.cs"] = $"g{i}\n",
                    ["migrations/0001.sql"] = $"create table t (id integer, c{i} integer);\n"
                },
                "Regenerate", "Ada", "ada@example.invalid", tenDays + i);
        host.CommitToGitRepositoryAs(name, new Dictionary<string, string> { ["src/Service.cs"] = "a2\n" },
            "Change the service", "Grace", "grace@example.invalid", tenDays + 4);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>A repository with a route directory and the sibling its character class would match.</summary>
    public static async Task BuildRoutedProjectAsync(TestHost host, string project)
    {
        const int tenDays = 10 * 24 * 60;
        string name = project + "-one";
        string source = host.CreateEmptyGitRepository(name);
        host.CommitToGitRepositoryAs(name,
            new Dictionary<string, string>
            {
                ["app/[slug]/page.tsx"] = "export default function Page() {}\n",
                ["app/s/page.tsx"] = "export default function S() {}\n"
            },
            "Add the routes", "Ada", "ada@example.invalid", tenDays);
        // The sibling churns harder, so a scope that took it in would rank it first and look right.
        for (int i = 1; i <= 3; i++)
            host.CommitToGitRepositoryAs(name,
                new Dictionary<string, string> { ["app/s/page.tsx"] = $"export default function S{i}() {{}}\n" },
                "Work on the sibling", "Grace", "grace@example.invalid", tenDays + i);

        await host.CreateProjectAsync(project);
        await host.AddRepositoryAsync(project, "one", source);
        await host.RefreshAsync(project);
    }

    /// <summary>
    ///     A project of one repository called <c>only</c>, which is what the tests about an index with
    ///     no history are built on. <c>TestHost.IndexedProjectAsync</c> would do it, but it names the
    ///     fixture directory after the repository slug, and a slug is unique only within its project
    ///     while the fixture directory is shared by every project on the host. Several tools each ask
    ///     for a repository called <c>only</c>, and on one host that is one directory taking a second
    ///     commit it has no changes for. Named after the project rather than the slug for that reason;
    ///     the slug stays <c>only</c>, because the qualified paths these tests assert on spell it.
    /// </summary>
    public static async Task OnlyRepositoryProjectAsync(TestHost host, string project,
        Dictionary<string, string> files, bool singleRepository = false)
    {
        await host.CreateProjectAsync(project, singleRepository);
        await host.AddRepositoryAsync(project, "only", host.CreateGitRepository($"{project}-only", files));
        await host.RefreshAsync(project);
    }
}

/// <summary>
///     One server per test class, and the read-only projects that class asks about, built once before
///     its first test. A class used to build a whole server per test — xunit constructs the test class
///     for every <c>[Fact]</c> — and then a git repository and an index on top of it. Measured on this
///     machine: the first client costs 265ms because that is where the host actually boots, and a
///     project on an already-booted host costs 430ms against 1150ms on a cold one.
///     A subclass names only the projects its own tools read, so splitting the history tools into a
///     class each also stopped every class paying for every fixture: the six shared projects used to be
///     built together, and are now built where they are read, by hosts that boot in parallel.
///     CODING_STANDARDS asks for a temp directory and a real database file per test <em>class</em>,
///     which is what each of these is.
/// </summary>
public abstract class HistoryFixture : IAsyncLifetime
{
    public TestHost Host { get; } = new(SearchEngine.Substring);

    /// <summary>The shared projects this class reads, by the names on <see cref="HistoryFixtures" />.</summary>
    protected abstract IReadOnlyList<string> Projects { get; }

    public async ValueTask InitializeAsync()
    {
        foreach (string project in Projects) await HistoryFixtures.BuildAsync(Host, project);
    }

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        // The base of a hierarchy, so CA1816 asks for this even though nothing here has a finalizer.
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

using System.Text.Json.Serialization;
using CodeExplorer.Control;
using CodeExplorer.Git;
using CodeExplorer.Index;
using CodeExplorer.Infrastructure;
using CodeExplorer.Operator;
using CodeExplorer.Reading;
using CodeExplorer.Refresh;
using CodeExplorer.Search;

// The image build runs this to bake the DuckDB fts extension into the image (#14, ADR-0004), and it
// exits without starting a server. A switch on the app rather than a tool of its own, because the
// install has to run against the DuckDB build the server will load. Read before the builder so that
// the argument never reaches command-line configuration, which rejects a switch carrying no value —
// which is why the switch is answered here even when the directory is missing: falling through would
// report a malformed build command as a configuration parse error naming neither.
if (args is [FtsExtension.InstallArgument, ..])
{
    if (args is not [_, var extensionDirectory])
    {
        await Console.Error.WriteLineAsync(
            $"Usage: {FtsExtension.InstallArgument} <directory>. One argument, the directory to install into.");
        return 1;
    }

    FtsExtension.InstallTo(extensionDirectory);
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

// Traces and metrics when an OTLP endpoint is configured, and nothing at all when it is not
// (ADR-0004): an empty appsettings must still yield a working, offline server.
builder.AddTelemetry();

// Blob Storage and Key Vault where they are configured, and the framework's default key ring where
// they are not (ADR-0004) — which is what protects stored credentials on a developer machine and
// what would lose them in a container.
var keyRing = builder.AddKeyRing();
// Entra where a tenant is configured and an open server where none is (ADR-0004). Read here rather
// than at first use because a half-configured tenant has to stop the server, not a request.
var authentication = builder.AddAuthentication();
// GHSA-qxhv-3r9w-q8h4: with no tenant, a page that rebinds its own name to 127.0.0.1 is refused.
bool loopbackOnly = builder.RestrictHostsWithoutTenant(authentication);
// Blob Storage when a container is configured and a folder on disk when none is (ADR-0004), so a
// plain `dotnet run` with an empty appsettings needs no Azure and still keeps a durable copy.
builder.Services.AddSingleton<DurableStore>();
builder.Services.AddSingleton<ControlDatabase>();
// Read here and not first by GitClones, so a mistyped repack setting stops the server naming it:
// thrown from that singleton, it took down every page and removal that needs one, not only a refresh.
_ = LocalCopyRepack.Settings(builder.Configuration);
builder.Services.AddSingleton<GitClones>();
builder.Services.AddSingleton<DurableIndex>();
builder.Services.AddSingleton<ProjectIndexes>();
// The one way into an index from outside Index/ (ADR-0005). Every reader takes this; only the
// refresh and the build take ProjectIndexes itself.
builder.Services.AddSingleton<IndexReaders>();
builder.Services.AddSingleton<HistoryBuilder>();
builder.Services.AddSingleton<OverviewBuilder>();
builder.Services.AddSingleton<ImportBuilder>();
builder.Services.AddSingleton<IndexBuilder>();
builder.Services.AddSingleton<ProjectRefresh>();
builder.Services.AddSingleton<RefreshService>();
builder.Services.AddSingleton<GrepSearch>();
builder.Services.AddSingleton<FileQueries>();
builder.Services.AddSingleton<HistoryQueries>();
builder.Services.AddSingleton<ReferenceSearch>();
builder.Services.AddSingleton<DefinitionSearch>();
builder.Services.AddSingleton<ImportGraph>();
builder.Services.AddSingleton<FileDeclarations>();
builder.Services.AddSingleton<MatchList>();
builder.Services.AddSingleton<ProjectOverview>();
builder.Services.AddSingleton<WarmUp>();
// Registered twice on purpose: as a singleton so a caller can await the pass it does, and as the
// hosted service that runs it. It does nothing unless Refresh:WarmUpOnStart is set.
builder.Services.AddSingleton<WarmUpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WarmUpService>());
// The same pairing for the tool-call counts the web UI shows: hosted so it listens from startup.
builder.Services.AddSingleton<ToolStatistics>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ToolStatistics>());
builder.Services.AddHttpContextAccessor();
// Every enum the API sends goes out by its member name, written once here rather than as an attribute
// on each enum: the web UI compares against a word, a state inserted later does not re-label the
// others, and an endpoint can return a read's own record instead of a copy that only spells its enums
// out. The member names as written and not camelCase: that is how the refresh state, the suggestion
// rule and the change period already went out, so the web's unions for them stay as they were.
// Property names keep the web defaults' camelCase. HTTP only: the MCP SDK serialises with options of
// its own and every tool answers prose, and the stored overview row has its own (IndexOverview), so
// neither moves with this.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddMcpServer().WithHttpTransport()
    // Each filter is registered once for every tool, present and future.
    .WithRequestFilters(filters => filters
        // Outermost first: the tool span has to cover everything an agent waits for, including the
        // argument filter below it, because what #90 is looking for is the time that is NOT in the
        // query span further in.
        .AddCallToolFilter(Telemetry.ToolFilter)
        // An argument the SDK cannot bind is answered by the tool's own schema rather than by the
        // transport's one sentence (#85, ToolArguments).
        .AddCallToolFilter(ToolArguments.Filter))
    .WithTools<ProjectTools>()
    .WithTools<SearchTools>()
    .WithTools<FileTools>()
    .WithTools<HistoryTools>()
    .WithTools<ImportTools>();

var app = builder.Build();

// Which local-or-Azure shape the key ring came up in, said here because this is the first line of the
// application that has a logger and because the answer is what a credential that stops decrypting
// needs. It warns about the two shapes that are not the deployed one.
keyRing.Report(app.Logger);
authentication.Report(app.Logger);
if (loopbackOnly) RequestOrigin.ReportLoopbackOnly(app.Logger);

// First, so a page on another origin is refused before anything reads a cookie or a project for it.
app.UseSameOriginOnly();

// Only where there is a tenant: with no scheme registered these throw, and an empty appsettings must
// still yield a complete server. The pair sits before the endpoints so that the fallback policy has a
// principal to judge, and before the static files so that the metadata document — which the MCP
// scheme serves from inside UseAuthentication — is answered without reaching an endpoint at all.
if (authentication.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// After authorization, so an unauthenticated caller learns nothing about which slugs exist, and
// before the endpoints, so a handler's Project parameter finds what it resolved (BoundProject).
app.UseBoundProject();

// Operator endpoints, one group per module (ADR-0005).
var api = app.MapGroup("/api").SameOriginOnly();
api.MapAuthentication(authentication);
api.MapControl();
api.MapRefresh();
api.MapSearch();
api.MapOperator();
// Any /api path no endpoint above matched, in the error shape every other /api answer has. Without it
// the SPA fallback below took such a GET and answered index.html with a 200, which the web client
// parsed as JSON and failed on with a parse error instead of a sentence.
api.MapFallback("{*path}", (HttpRequest request) =>
    ApiError.NotFound($"No API endpoint answers {request.Method} {request.Path}."));

// One MCP endpoint per project (ADR-0002), bound from the route before the SDK sees the request.
app.MapGroup("/projects/{project}").BindProject().MapMcp("/mcp").ProtectMcp(authentication).SameOriginOnly();

// The operator web UI is a Vite build into wwwroot. It is absent until someone runs that build, and
// the server must still start: MapFallbackToFile would 404 at request time, which is the same answer
// the browser gets today for an unbuilt UI. Every client-side route falls back to index.html. Being a
// fallback, it loses to every endpoint that matches, but it would match an unknown /api path as readily
// as a page — which is why /api has a fallback of its own above, more specific than this one.
//
// The fallback is an endpoint and is therefore behind the fallback policy, so a browser asking for a
// page is sent to sign in. The files beside it are not: MapFallbackToFile matches `{*path:nonfile}`,
// so anything with an extension reaches UseStaticFiles, which is middleware and enforces no policy.
// Left that way knowingly — what is anonymous is the UI's own bundle and nothing it renders, since
// every byte of that comes from /api. MapStaticAssets would close it by making them endpoints, and
// cannot be used: its manifest is built by msbuild, and wwwroot is filled by a Vite build that runs
// outside it, so every asset would 404 instead.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();
// Spelled out because the install switch above returns a code, which makes every exit an int.
return 0;

namespace CodeExplorer
{
    /// <summary>Marker so the tests can host the app through <c>WebApplicationFactory</c>.</summary>
    public partial class Program;
}

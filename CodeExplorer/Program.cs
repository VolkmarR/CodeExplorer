using CodeExplorer;

var builder = WebApplication.CreateBuilder(args);

// Traces and metrics when an OTLP endpoint is configured, and nothing at all when it is not
// (ADR-0004): an empty appsettings must still yield a working, offline server.
builder.AddTelemetry();

// The default key ring (a folder under the user profile) protects stored credentials until #13
// moves it to Blob Storage and Key Vault; absent configuration must still yield a working server.
builder.Services.AddDataProtection();
// Blob Storage when a container is configured and a folder on disk when none is (ADR-0004), so a
// plain `dotnet run` with an empty appsettings needs no Azure and still keeps a durable copy.
builder.Services.AddSingleton<DurableStore>();
builder.Services.AddSingleton<ControlDatabase>();
builder.Services.AddSingleton<GitClones>();
builder.Services.AddSingleton<DurableIndex>();
builder.Services.AddSingleton<ProjectIndexes>();
builder.Services.AddSingleton<IndexBuilder>();
builder.Services.AddSingleton<ProjectRefresh>();
builder.Services.AddSingleton<RefreshService>();
builder.Services.AddSingleton<GrepSearch>();
builder.Services.AddSingleton<ProjectOverview>();
builder.Services.AddSingleton<WarmUp>();
// Registered twice on purpose: as a singleton so a caller can await the pass it does, and as the
// hosted service that runs it. It does nothing unless Refresh:WarmUpOnStart is set.
builder.Services.AddSingleton<WarmUpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WarmUpService>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer().WithHttpTransport()
    .WithTools<ProjectTools>()
    .WithTools<SearchTools>()
    .WithTools<FileTools>();

var app = builder.Build();

// Operator endpoints, one group per module (ADR-0005).
var api = app.MapGroup("/api");
api.MapControl();
api.MapRefresh();
api.MapSearch();
api.MapOperator();

// One MCP endpoint per project (ADR-0002), bound from the route before the SDK sees the request.
app.MapGroup("/projects/{slug}").BindProject().MapMcp("/mcp");

// The operator web UI is a Vite build into wwwroot. It is absent until someone runs that build, and
// the server must still start: MapFallbackToFile would 404 at request time, which is the same answer
// the browser gets today for an unbuilt UI. Every client-side route falls back to index.html, and the
// fallback runs last so it cannot shadow /api or /projects/{slug}/mcp.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Marker so the tests can host the app through <c>WebApplicationFactory</c>.</summary>
public partial class Program;

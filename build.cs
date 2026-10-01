#:sdk Cake.Sdk@6.3.0

// Packages the three deployments `docs/deployment/` describes, from one checkout:
//
//   Package-Container   the image `azure-container-apps.md` deploys, built by the `Dockerfile`.
//   Package-Zip         the folder `iis-windows-server.md` unpacks into C:\CodeExplorer.
//   Package-Evaluation  the self-contained folder `local-evaluation.md` unpacks on one Windows PC,
//                       with the stdio proxy Claude Desktop starts. Not part of the default target.
//
// The first two share no steps on purpose. The `Dockerfile` builds the UI and bakes the `fts`
// extension inside its own stages, so a container package is one `docker build` and nothing here may
// duplicate it; the zip has to do both itself, on this machine, because there is no second stage to
// do it in. That asymmetry is the whole reason the two targets look so different in length. The
// evaluation package is the zip's sibling: the same UI build and the same `fts` bake, around a
// different publish.

var target = Argument("target", "Package");
var configuration = Argument("configuration", "Release");
var artifacts = Directory(Argument("artifacts", "artifacts"));

// The tag the Azure guide's `az acr build` uses, so a locally built image and one built in ACR Tasks
// from the same commit are named the same thing.
var version = Argument("version", GitShortSha());

// Empty means the image stays local under its bare name. Set it to an ACR login server
// (`<name>.azurecr.io`) to tag for a registry, and add --push to send it.
var registry = Argument("registry", string.Empty);
var push = HasArgument("push");

// The `fts` bake needs the network, and the IIS guide's section 2 says why it also needs Windows.
// Skipping it is an explicit choice for an offline build machine and produces a zip that is not yet
// deployable — never a default, because a missing extension is not an error at runtime, it is
// quietly different rankings (#14).
var bakeFts = !HasArgument("no-fts");

var zipStage = artifacts + Directory("zip");
var publishDirectory = zipStage + Directory("app");
var extensionDirectory = zipStage + Directory("duckdb/extensions");

// Laid out as local-evaluation.md's paths: `app` holds both programs, so the proxy finds the server
// and the settings file beside itself, and `data` appears beside `app` on the server's first start.
var evaluationStage = artifacts + Directory("evaluation");
var evaluationApp = evaluationStage + Directory("app");
var evaluationExtensions = evaluationStage + Directory("duckdb/extensions");
// The proxy publishes on its own and only its executable is copied into `app`: two publishes into
// one folder would let the second overwrite whatever file names they share.
var proxyStage = artifacts + Directory("proxy");

// What a self-contained win-x64 publish of the server must hold; Publish-Evaluation says why each one.
string[] evaluationRequired = ["CodeExplorer.exe", "duckdb.dll", "hostfxr.dll"];

Task("Clean")
    .Does(() =>
{
    CleanDirectory(artifacts);
});

Task("Build-Web")
    .IsDependentOn("Clean")
    .Does(() =>
{
    // Nothing in the .NET build produces `CodeExplorer/wwwroot` — the UI is a Vite+ build that runs
    // outside msbuild (ADR-0004) — so this runs before the publish that copies its output.
    Exec("pnpm", "install --frozen-lockfile", "web");
    Exec("pnpm", "exec vp build", "web");
});

Task("Publish")
    .IsDependentOn("Build-Web")
    .Does(() =>
{
    // Portable and framework-dependent, as the IIS guide says: the hosting bundle carries the
    // framework, and the per-RID `runtimes` folders beside the output are where `duckdb.dll` comes
    // from. Publishing for win-x64 alone would be smaller and is deliberately not done — it is the
    // documented procedure that is being packaged here, not a variant of it.
    DotNetPublish("CodeExplorer/CodeExplorer.csproj", new DotNetPublishSettings
    {
        Configuration = configuration,
        OutputDirectory = publishDirectory,
        NoLogo = true,
    });

    // The failure this catches is in the guide's troubleshooting table: a publish that dropped the
    // native folder starts fine and dies on the first database access, on the server, with a
    // `DllNotFoundException`. Cheaper to find here.
    var duckdb = publishDirectory + File("runtimes/win-x64/native/duckdb.dll");
    if (!FileExists(duckdb))
    {
        throw new CakeException($"The publish output has no {duckdb}. Nothing on the server will reach a database without it.");
    }
});

Task("Install-Fts")
    .IsDependentOn("Publish")
    .Does(() =>
{
    InstallFts(publishDirectory + File("CodeExplorer.dll"), extensionDirectory,
        "--no-fts: the zip carries no extension. Run section 2 of docs/deployment/iis-windows-server.md on the server before it serves anything.");
});

Task("Package-Zip")
    .IsDependentOn("Install-Fts")
    .Does(() =>
{
    // Laid out as the guide's paths, so unpacking it at C:\CodeExplorer puts `app` and
    // `duckdb\extensions` exactly where sections 2 to 5 already point.
    CopyFile("docs/deployment/iis-windows-server.md", zipStage + File("DEPLOYMENT.md"));

    var package = artifacts + File($"codeexplorer-{version}-win-x64.zip");
    Zip(zipStage, package);
    Information($"Packaged {package}");
});

Task("Publish-Evaluation")
    .IsDependentOn("Build-Web")
    .Does(() =>
{
    // Self-contained for win-x64, unlike the IIS zip: the evaluator's PC has no hosting bundle and
    // nobody to install one, so the runtime travels in `app`. The IIS publish above stays portable
    // and framework-dependent, as its guide documents.
    DotNetPublish("CodeExplorer/CodeExplorer.csproj", new DotNetPublishSettings
    {
        Configuration = configuration,
        OutputDirectory = evaluationApp,
        Runtime = "win-x64",
        SelfContained = true,
        NoLogo = true,
    });

    // A RID-specific publish flattens the `runtimes` tree the portable one carries, so the native
    // DuckDB library sits beside the executable rather than under runtimes\win-x64\native, and the
    // portable task's check would look in the wrong place. hostfxr.dll is what a self-contained
    // publish carries and a framework-dependent one does not: its absence is a package that would ask
    // the PC for a .NET install it does not have.
    foreach (var required in evaluationRequired)
    {
        if (!FileExists(evaluationApp + File(required)))
        {
            throw new CakeException($"The evaluation publish has no {required} in {evaluationApp}. The package would not run on a PC without .NET.");
        }
    }
});

Task("Publish-Proxy")
    .IsDependentOn("Clean")
    .Does(() =>
{
    // One file, its runtime inside it and trimmed to what it uses: it is the path a tester types into
    // Claude Desktop's configuration, and it starts once per project each time Claude Desktop does.
    // The project turns the trim and single-file analyzers on, so what would break trimmed fails the
    // ordinary build first.
    DotNetPublish("CodeExplorer.McpProxy/CodeExplorer.McpProxy.csproj", new DotNetPublishSettings
    {
        Configuration = configuration,
        OutputDirectory = proxyStage,
        Runtime = "win-x64",
        SelfContained = true,
        PublishSingleFile = true,
        PublishTrimmed = true,
        NoLogo = true,
    });
});

Task("Package-Evaluation")
    .IsDependentOn("Publish-Evaluation")
    .IsDependentOn("Publish-Proxy")
    .Does(() =>
{
    var proxy = evaluationApp + File("CodeExplorer.McpProxy.exe");
    if (FileExists(proxy))
    {
        throw new CakeException($"The server's publish already holds {proxy}; copying the proxy over it would hide which one is in the package.");
    }

    CopyFile(proxyStage + File("CodeExplorer.McpProxy.exe"), proxy);
    Information($"The proxy is {FileSize(proxy) / 1024.0 / 1024.0:0.0} MiB.");

    // The settings the server, the proxy and start.cmd all read. In `app`, because the server loads
    // appsettings.<environment>.json from its content root and the proxy looks beside itself.
    CopyFile("deploy/evaluation/appsettings.Evaluation.json", evaluationApp + File("appsettings.Evaluation.json"));

    InstallFts(evaluationApp + File("CodeExplorer.exe"), evaluationExtensions,
        "--no-fts: the package carries no extension. Its server pins Index:SearchEngine to Fts, so it downloads the extension on its first start and will not start offline.");

    CopyFile("deploy/evaluation/start.cmd", evaluationStage + File("start.cmd"));
    CopyFile("deploy/evaluation/stop.cmd", evaluationStage + File("stop.cmd"));
    CopyFile("deploy/evaluation/claude_desktop_config.example.json",
        evaluationStage + File("claude_desktop_config.example.json"));
    CopyFile("docs/deployment/local-evaluation.md", evaluationStage + File("README.md"));

    var package = artifacts + File($"codeexplorer-{version}-evaluation-win-x64.zip");
    Zip(evaluationStage, package);
    Information($"Packaged {package}");
});

Task("Package-Container")
    .IsDependentOn("Clean")
    .Does(() =>
{
    // No dependency on Build-Web or Publish: the `Dockerfile` runs both in its own stages, against a
    // clean copy, and `.dockerignore` excludes `bin`, `obj` and `wwwroot` precisely so a local build
    // cannot leak a host-shaped artifact into the image.
    var prefix = string.IsNullOrEmpty(registry) ? string.Empty : registry + "/";
    var tags = $"-t {prefix}codeexplorer:{version} -t {prefix}codeexplorer:latest";

    // This build needs the network for the `fts` bake, and fails when it cannot reach out. That is
    // the point of the image (ADR-0004), so it is also the point of this target.
    Exec("docker", $"build {tags} .");

    if (push)
    {
        if (string.IsNullOrEmpty(registry))
        {
            throw new CakeException("--push needs --registry=<name>.azurecr.io: there is nowhere to push a bare local tag.");
        }

        Exec("docker", $"push {prefix}codeexplorer:{version}");
        Exec("docker", $"push {prefix}codeexplorer:latest");
    }
});

// The two server deployments and not the evaluation package: that one is built for a tester when one
// is wanted, and leaving it out keeps what CI's default target produces, and how long it takes, as it was.
Task("Package")
    .IsDependentOn("Package-Container")
    .IsDependentOn("Package-Zip");

RunTarget(target);

// Bakes the `fts` extension into a package through the published application rather than fetching it
// some other way: it is stamped with the DuckDB version and the platform of the build that will load
// it, and only these binaries on this machine know both. Which is also why this cannot run on the
// container image's Linux — its copy is `linux_amd64` and a Windows host ignores it. An `.exe` is the
// self-contained evaluation publish and runs as it is; a `.dll` is the portable one and runs through
// `dotnet`.
void InstallFts(FilePath application, DirectoryPath extensions, string skipped)
{
    if (!bakeFts)
    {
        Warning(skipped);
        return;
    }

    if (!IsRunningOnWindows())
    {
        throw new CakeException("A Windows package has to be packaged on Windows: the fts extension is platform-stamped and a Linux build bakes a copy no Windows host will load. Use --no-fts to package without it.");
    }

    string arguments = $"--install-fts \"{MakeAbsolute(extensions)}\"";
    if (application.GetExtension() == ".exe")
    {
        int exitCode = StartProcess(MakeAbsolute(application), new ProcessSettings { Arguments = arguments });
        if (exitCode != 0)
        {
            throw new CakeException($"{application} {arguments} exited with {exitCode}.");
        }
    }
    else
    {
        DotNetExecute(application, arguments);
    }

    // The install reaches the network and the app deliberately does not swallow a failure, so this is
    // belt and braces — but it is also the one assertion that distinguishes "installed" from
    // "installed for the wrong platform", which is invisible until the server starts.
    if (GetSubDirectories(extensions).SelectMany(GetSubDirectories).All(d => d.GetDirectoryName() != "windows_amd64"))
    {
        throw new CakeException($"No windows_amd64 extension under {extensions} after the install.");
    }
}

// `pnpm` and `docker` are .cmd shims on Windows, and starting one directly fails with "file not
// found": Process.Start does not apply PATHEXT. cmd does the lookup; everywhere else the command is
// an executable on PATH already.
void Exec(string command, string arguments, DirectoryPath? workingDirectory = null)
{
    var settings = new ProcessSettings
    {
        Arguments = IsRunningOnWindows() ? $"/c {command} {arguments}" : arguments,
    };

    if (workingDirectory is not null)
    {
        settings.WorkingDirectory = workingDirectory;
    }

    var exitCode = StartProcess(IsRunningOnWindows() ? "cmd" : command, settings);
    if (exitCode != 0)
    {
        throw new CakeException($"{command} {arguments} exited with {exitCode}.");
    }
}

// The commit is the version, as the Azure guide tags it. A checkout that is not a repository — an
// exported source drop — still packages, under a name that says it came from one.
string GitShortSha()
{
    var settings = new ProcessSettings
    {
        Arguments = "rev-parse --short HEAD",
        RedirectStandardOutput = true,
    };

    var exitCode = StartProcess("git", settings, out var output);
    var sha = output?.FirstOrDefault();

    return exitCode == 0 && !string.IsNullOrWhiteSpace(sha) ? sha.Trim() : "local";
}

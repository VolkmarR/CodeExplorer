#:sdk Cake.Sdk@6.3.0

// Packages the two deployments `docs/deployment/` describes, from one checkout:
//
//   Package-Container  the image `azure-container-apps.md` deploys, built by the `Dockerfile`.
//   Package-Zip        the folder `iis-windows-server.md` unpacks into C:\CodeExplorer.
//
// They share no steps on purpose. The `Dockerfile` builds the UI and bakes the `fts` extension
// inside its own stages, so a container package is one `docker build` and nothing here may
// duplicate it; the zip has to do both itself, on this machine, because there is no second stage to
// do it in. That asymmetry is the whole reason the two targets look so different in length.

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
    if (!bakeFts)
    {
        Warning("--no-fts: the zip carries no extension. Run section 2 of docs/deployment/iis-windows-server.md on the server before it serves anything.");
        return;
    }

    // Run through the published application rather than fetching the extension some other way: it is
    // stamped with the DuckDB version and the platform of the build that will load it, and only
    // these binaries on this machine know both. Which is also why this cannot run on the container
    // image's Linux — its copy is `linux_amd64` and a Windows host ignores it.
    if (!IsRunningOnWindows())
    {
        throw new CakeException("The Windows zip has to be packaged on Windows: the fts extension is platform-stamped and a Linux build bakes a copy no Windows host will load. Use --no-fts to package without it and install it on the server.");
    }

    DotNetExecute(publishDirectory + File("CodeExplorer.dll"), $"--install-fts \"{MakeAbsolute(extensionDirectory)}\"");

    // The install reaches the network and the app deliberately does not swallow a failure, so this is
    // belt and braces — but it is also the one assertion that distinguishes "installed" from
    // "installed for the wrong platform", which is invisible until the server starts.
    if (GetSubDirectories(extensionDirectory).SelectMany(GetSubDirectories).All(d => d.GetDirectoryName() != "windows_amd64"))
    {
        throw new CakeException($"No windows_amd64 extension under {extensionDirectory} after the install.");
    }
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

Task("Package")
    .IsDependentOn("Package-Container")
    .IsDependentOn("Package-Zip");

RunTarget(target);

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

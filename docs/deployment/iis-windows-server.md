# Deploying to IIS on Windows Server

The same single deployable as the container, hosted by IIS instead. Choose it when the repositories
being indexed are reachable only from inside a network, or when there is no Azure subscription to put
the server in; everything the app needs from Azure is selected by configuration, and its absence
selects a local default (ADR-0004), so an entirely on-premises install is a supported shape rather
than a degraded one.

Nothing here is covered by the tests. `build.cs` packages sections 1 and 2 of what follows into a zip
— that much is a build artefact like the image — but everything from section 3 on is a worked
procedure, not something CI proves.

What you give up by not being in Azure is one thing and it is significant: with no blob container, the
Data Protection key ring is local to this machine and this application pool identity. What follows
says exactly where it lands and what breaks it, because that is the failure this deployment shape is
most likely to meet.

## Before you start

On the server:

- **The ASP.NET Core 10 Hosting Bundle**, which installs the runtime and the ASP.NET Core Module that
  lets IIS host the app. Install it after IIS, and run `iisreset` afterwards; installed before IIS it
  does not register the module.
- **The Microsoft Visual C++ 2015–2022 redistributable (x64).** The bundled `duckdb.dll` is a native
  library and a bare Windows Server image often lacks it. If the app starts and then fails on the
  first database access with a `DllNotFoundException` or a `BadImageFormatException` naming
  `duckdb.dll`, this is why.
- **No git.** LibGit2Sharp bundles libgit2 and clones through it, which is also why a token never
  reaches process arguments (ADR-0003). Do not install git expecting it to be used.

On a build machine — which can be the server, but does not have to be:

- The .NET 10 SDK, and Node 24 with pnpm through corepack, because the UI is a `vite-plus` build that
  runs outside msbuild and nothing in the .NET build produces it.
- **Windows, and outbound access.** Not because the app needs either — the publish is portable and
  builds anywhere — but because of the extension in section 2: it is stamped with the platform that
  will load it, so a Linux build machine produces one this server ignores. `build.cs` refuses to
  package there rather than shipping it.

## 1. Build the publish output

Sections 1 and 2 are what `build.cs` does, on a Windows build machine with the network:

```powershell
.\build.ps1 --target=Package-Zip
```

It writes `artifacts\codeexplorer-<commit>-win-x64.zip`, laid out for this guide's paths, so on the
server it is one command and sections 2 to 5 already point at what comes out:

```powershell
Expand-Archive codeexplorer-<commit>-win-x64.zip -DestinationPath C:\CodeExplorer
```

`app`, `duckdb\extensions` and a copy of this guide. Skip to section 3 if you took this route; the
rest of section 1 and section 2 are what the script did.

By hand, then, the UI first, because the publish copies its output:

```powershell
cd web
corepack enable
pnpm install --frozen-lockfile
pnpm exec vp build          # writes into CodeExplorer/wwwroot
cd ..
dotnet publish CodeExplorer/CodeExplorer.csproj -c Release -o C:\CodeExplorer\app
```

A portable, framework-dependent publish is right here: the hosting bundle carries the framework, and
the `runtimes\win-x64\native` folder beside the output is where `duckdb.dll` comes from. If you thin
the output, do not remove it.

`dotnet publish` on a Web SDK project writes a `web.config` naming the ASP.NET Core Module and the
in-process hosting model. Keep in-process — it is the default and the faster one, and nothing here
needs the out-of-process model.

## 2. Install the `fts` extension for Windows

`build.cs` did this if you unpacked the zip — it is the step that makes the package Windows-only —
and what follows is the same two commands by hand.

This step has no equivalent in the container guide because the image does it during its own build,
and its result cannot be reused: a DuckDB extension is stamped with the version and the platform of
the build that will load it, so the image's copy is `v1.5.5/linux_amd64` and a Windows host needs
`v1.5.5/windows_amd64`. Run the published application's install switch on a Windows machine with
network access — the same binaries that will serve, for the same reason the image builds it from the
published app rather than downloading it some other way:

```powershell
cd C:\CodeExplorer\app
dotnet CodeExplorer.dll --install-fts C:\CodeExplorer\duckdb\extensions
```

It installs and exits. You should find
`C:\CodeExplorer\duckdb\extensions\v1.5.5\windows_amd64\fts.duckdb_extension`.

Then pin the engine, as the image does, by setting `Index:ExtensionDirectory` to that path and
`Index:SearchEngine` to `Fts` in the next section. The pin is the point: left at `Auto`, an extension
that went missing is not an error — every search silently falls back to a substring scan, which ranks
differently and raises nothing an operator would see (#14). Pinned, the same fault stops the app at
startup.

If the server has no outbound access at all, run the install on another Windows machine with the same
published output and copy the `v1.5.5\windows_amd64` folder across.

## 3. The application pool

Create a pool — call it `CodeExplorer` — with **No Managed Code**, and then change four of its
defaults. All four are about the same thing: this app holds DuckDB files open, and DuckDB expects one
process per file.

| Setting                                   | Value              | Why                                                                 |
| ----------------------------------------- | ------------------ | -------------------------------------------------------------------- |
| Maximum Worker Processes                  | `1`                | A web garden would run two processes over one set of database files. |
| Disable Overlapped Recycle                | `True`             | Overlapped recycling starts the new process while the old one still holds the files. |
| Idle Time-out (minutes)                   | `0`                | An idle shutdown throws away every attached index and warm-up's work. |
| Regular Time Interval (minutes)           | `0`                | Stops the 29-hour default recycle from doing the same at an arbitrary hour. |
| Start Mode                                | `AlwaysRunning`    | With preload below, the app starts with the server rather than on the first request. |
| Load User Profile                         | `True`             | See the key ring section — this decides where keys land.             |

```powershell
Import-Module WebAdministration
$pool = 'IIS:\AppPools\CodeExplorer'
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ''
Set-ItemProperty $pool -Name processModel.maxProcesses -Value 1
Set-ItemProperty $pool -Name recycling.disallowOverlappingRotation -Value $true
Set-ItemProperty $pool -Name processModel.idleTimeout -Value '00:00:00'
Set-ItemProperty $pool -Name recycling.periodicRestart.time -Value '00:00:00'
Set-ItemProperty $pool -Name startMode -Value 'AlwaysRunning'
Set-ItemProperty $pool -Name processModel.loadUserProfile -Value $true
```

## 4. The site

Point a site at `C:\CodeExplorer\app`, bind it to HTTPS with a certificate, and set
`preloadEnabled` so `AlwaysRunning` has something to start:

```powershell
New-Website -Name CodeExplorer -PhysicalPath C:\CodeExplorer\app -ApplicationPool CodeExplorer -Port 443 -Ssl
Set-ItemProperty 'IIS:\Sites\CodeExplorer' -Name applicationDefaults.preloadEnabled -Value $true
```

IIS terminates TLS and forwards the request to the app over the loopback. The app does not call
`UseForwardedHeaders`, but the ASP.NET Core Module in-process preserves the original scheme and host,
so sign-in redirects come out as `https` without further configuration. **If** you put something else
in front of IIS — ARR, a hardware load balancer, a reverse proxy — that stops being true, and you
need `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` as the Azure guide describes. The scheme and host the
app sees are also what it takes as its own origin, so a proxy that terminates TLS without that
setting gets every write from the UI refused with a 403, with a tenant or without one
(GHSA-qxhv-3r9w-q8h4).

### Writable paths

The default data directory is `data`, resolved against the content root, which would put the clones
and every index inside the publish folder and lose them on the next deploy. Give it a directory of
its own and grant the pool identity — `IIS AppPool\CodeExplorer` — modify rights on it and on the
extension directory:

```powershell
New-Item -ItemType Directory C:\CodeExplorer\data -Force
icacls C:\CodeExplorer\data /grant 'IIS AppPool\CodeExplorer:(OI)(CI)M'
icacls C:\CodeExplorer\duckdb /grant 'IIS AppPool\CodeExplorer:(OI)(CI)RX'
```

Size it against ADR-0003's arithmetic rather than the 8 GiB the container is capped at: this disk has
no such ceiling, but it still holds every project's index, the shadow file written during a rebuild,
the bare clones and DuckDB's spill files at the same time. A SrcRadix-sized codebase is about 649 MB
of index. `Refresh:MinimumFreeBytes` — 512 MB by default — is what refuses a rebuild that would run
the disk out, and it is worth raising here.

## 5. Settings

`web.config` is the natural place for them on IIS, because they travel with the site rather than with
the machine. Add an `environmentVariables` block inside the generated `aspNetCore` element:

```xml
<aspNetCore processPath="dotnet" arguments=".\CodeExplorer.dll" hostingModel="inprocess">
  <environmentVariables>
    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
    <environmentVariable name="Storage__DataDirectory" value="C:\CodeExplorer\data" />
    <environmentVariable name="Storage__DurableDirectory" value="D:\CodeExplorer\durable" />
    <environmentVariable name="Index__ExtensionDirectory" value="C:\CodeExplorer\duckdb\extensions" />
    <environmentVariable name="Index__SearchEngine" value="Fts" />
    <environmentVariable name="Refresh__WarmUpOnStart" value="true" />
  </environmentVariables>
</aspNetCore>
```

`__` stands for `:`, as it does everywhere in .NET configuration. An `appsettings.Production.json`
beside the binaries works equally well and is easier to read; the one thing not to do is put a client
secret in either, where a deploy will overwrite it and a backup will keep it.

`Refresh:WarmUpOnStart` is `true` here and `false` in a scale-to-zero container, and the difference is
the whole reasoning behind the setting (ADR-0004): it exists for a replica that is kept running,
which is exactly what `AlwaysRunning` with no idle timeout produces. The app starts answering
immediately and restores the projects behind it.

`Git:TransferStallSeconds` — 300 by default — is how long a clone or fetch waits on a remote that has
gone silent before the refresh skips that repository and says the remote stopped responding. It
bounds each wait, not the transfer, so raise it only for a remote that is slow to start sending a
large pack. It must be between 1 and 2147483: zero would be libgit2's "no limit", the hang the
setting exists to end, so it is refused with an error naming the setting rather than honoured.

`Index:MaxFileBytes` — 25 MiB by default — is the largest file a refresh reads. A larger file is
listed with the reason but not indexed, and a commit that added or changed one is recorded in history
with no line counts rather than diffed, so a refresh inflates no file larger than this, a dump or a
generated bundle included. Raise it for a project of large hand-written sources, and size the
application pool's memory for it.

`Control:AllowLocalRepositories` — `false` by default — decides whether a repository may be a local
path, a UNC share or a `file://` URL. Leave it off unless every caller who can add a repository may
also read everything the application pool's identity can: with it on, any of them can have the server
clone a repository from its own disk, another project's local copy under the data directory
included, and read it through MCP. Switching it off again stops a refresh reading the local
repositories already stored; each is reported as skipped.

A repository credential is accepted only beside an https or ssh URL; with an `http://` or `git://`
URL it is refused, because it would cross the network in clear text (GHSA-4f8q-c6jj-fr44). There is
no setting to allow it. A repository stored with that pair before the refusal is skipped by every
refresh until it is deleted and added again under its https or ssh URL.

The work one MCP call can ask for is bounded, and none of the bounds is a setting
(GHSA-v284-9964-6mjr): `list_tree` depth at most 64, `read_file` at most 100 entries and 100,000
lines or 16 million characters across them, and a `multiline` grep page at most 8 MiB of file content
beyond its first file. Globs keep SQL `GLOB`'s meaning but run as RE2, so a pattern full of stars no
longer holds one of the pool's cores for minutes; a glob or path term is at most 256 characters and an
argument at most 32 terms. The multiline budget is independent of `Index:MaxFileBytes`: a file larger
than 8 MiB is still shown, on a page of its own.

`Storage:DurableDirectory` is optional and the second disk above is a suggestion, not a requirement —
it defaults to `durable` under the data directory. Separating them is worth it precisely because they
mean different things: one is working storage that can be rebuilt, the other is the copy that
survives losing it.

## 6. The key ring, which is the part that bites

With no `Storage:BlobContainerUrl`, the app leaves Data Protection at the framework's default, and on
IIS that default is not a folder in the publish output — it is a per-application-pool location the
framework picks. With Load User Profile on, keys go under the pool account's profile; with it off,
the framework falls back to a registry key ACL'd to the pool's SID. Both persist across restarts,
which is what this deployment needs and what a container cannot have. Both are also tied to that
identity and that machine, so all of the following lose every stored repository credential and sign
every operator out:

- changing the application pool identity,
- recreating the application pool under the same name,
- moving the site to a second server,
- restoring the control database onto a machine that does not have the same key ring.

The app says at startup which of the three shapes it came up in, and warns about the two that are not
the deployed one — so a local key ring is reported as a warning here even though it is the intended
answer for this deployment. Read it as the reminder it is.

If any of the four bullets above is in your plans, use the Azure shape for this one thing: set
`Storage:BlobContainerUrl` (and, to wrap it, `Storage:KeyVaultKeyUrl`) even on an on-premises server.
The identity resolution goes through `DefaultAzureCredential`, so the machine needs either a
workload identity or `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_CLIENT_SECRET` in the environment,
and outbound access to the storage account. Switching an already-running server to a container is a
one-way step: a persisted key ring is read under a fixed application name rather than the framework's
path-derived one, so credentials stored before the switch have to be entered again.

Failing that, back up whatever holds the keys along with the control database, and treat "the
credentials must be re-entered" as a documented step in your disaster recovery rather than a
surprise.

## 7. Authentication

Entra works from an on-premises server exactly as it does in Azure, and needs only outbound HTTPS to
`login.microsoftonline.com`. Set `AzureAd:ClientId`, `AzureAd:TenantId`, `AzureAd:ClientSecret` and
`AzureAd:Scopes`, and register `https://<host>/signin-oidc` as the redirect URI; section 5 of the
Azure guide describes the registration in full and none of it differs here.

Leaving them unset is a real option for a server that is only reachable inside a network: every
endpoint then answers anonymously. It is a decision to make deliberately, because it applies to the
MCP endpoints too — anyone who can reach the host can read every indexed repository.
Unauthenticated, the server answers only the loopback host names (`localhost`, `127.0.0.1`,
`[::1]`) unless `AllowedHosts` names others, so set it to the names the site is reached under,
separated by semicolons (`codeexplorer.corp.example;codeexplorer`), with the section 5 settings — the
name is `AllowedHosts` there too, since it has no `:`. Every other name gets a 400, and
the startup log says why. That default is what keeps a web page from rebinding its own name to the
server's address and reading it from a browser inside the network.

Windows Authentication is not an alternative. The app registers a bearer scheme and a cookie scheme
and nothing else, and MCP clients discover the tenant from the 401's `resource_metadata` — an
IIS-level Negotiate would authenticate the browser and leave every agent without a way in. Leave
Anonymous Authentication enabled at the IIS level and let the app decide.

## 8. Warm-up and refresh

With `Refresh:WarmUpOnStart` set, a restart restores everything by itself, so the only scheduled work
left is the refresh — rebuilding indexes after the day's commits. Task Scheduler is the equivalent of
the Container Apps Job:

```powershell
# Unauthenticated deployment
Invoke-RestMethod -Method Post https://<host>/api/projects/<slug>/refresh
```

With a tenant configured, the scheduled task needs a token, and it needs it as an application rather
than as a user: register a second application, grant it the app role from the Azure guide, and
request `api://<client-id>/.default` with client credentials. Store its secret in the Windows
Credential Manager or a certificate rather than in the script.

One rebuild runs at a time across the whole server, and `GET /api/projects/{slug}/refresh` reports
progress, so a task that walks several projects should fire them and poll rather than expect parallel
work.

## 9. Verify it

1. Browse to the site. The UI loads, and — with a tenant configured — sends you to sign in first.
2. Check the Windows Application event log, or `stdout` logging if you switch it on temporarily in
   `web.config`. The startup lines name the key ring shape and the authentication shape.
3. Create a project, add a repository, refresh it, and search. A result set that ranks sensibly is
   `fts` loaded; if step 2 showed the app starting at all with `Index:SearchEngine=Fts`, the extension
   is there, because the pin makes a missing one fatal.
4. Restart the application pool and search again. Fast answers are the indexes still on disk; a
   credential that still clones is the local key ring surviving a restart, which is the thing worth
   knowing before the first real one.

## Troubleshooting

| What you see                                                    | What it usually is                                                        |
| --------------------------------------------------------------- | -------------------------------------------------------------------------- |
| HTTP 500.19, or the site will not start                          | The hosting bundle was installed before IIS. Reinstall it, then `iisreset`. |
| HTTP 500.30 with nothing else                                    | Turn on `stdoutLogEnabled` in `web.config` briefly; a configuration error that stops the app names itself there. |
| `DllNotFoundException` or `BadImageFormatException`, `duckdb.dll` | The VC++ redistributable, or a publish that dropped `runtimes\win-x64\native` — which `build.cs` fails on, so from the zip it is the redistributable. |
| Startup stops naming `Index:SearchEngine`                        | The extension directory has no `windows_amd64` copy. Section 2 — and note that the image's Linux copy will not do. |
| Credentials stopped decrypting after maintenance                 | The application pool identity changed. Section 6.                          |
| `IOException` on an index file, intermittently                   | Two worker processes. Check `maxProcesses` and overlapped recycling in section 3. |
| HTTP 400 on every request, no tenant configured                  | `AllowedHosts` is unset, so only loopback names are answered. Section 7; the startup log names it too. |
| HTTP 403 on every write from the UI, reads work                  | A proxy in front of IIS terminates TLS and the app sees `http`. `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, section 4. |
| A refresh skips a repository as "a local path or file URL"       | `Control:AllowLocalRepositories` is off, which it is by default, and has been since GHSA-5373-pppr-q3q9 for repositories stored before it. Point the repository at its remote, or section 5. |
| A refresh skips a repository because "a credential is only sent over https or ssh" | It was stored with a credential beside an `http://` or `git://` URL, which is refused since GHSA-4f8q-c6jj-fr44. Delete it and add it again under its https or ssh URL, section 5. |
| A refresh skips a repository because "the remote stopped responding" | The remote sent nothing for `Git:TransferStallSeconds`. Check the remote first; raise the setting only for one slow to start a pack. |
| An agent is told "depth may be at most 64", "One read takes at most 100 entries", "was not read: the entries before it already read as much as one call reads" or "may be at most 256 characters" | The per-call limits since GHSA-v284-9964-6mjr, section 5. They are not settings; the reply says how to split the call. |
| A multiline grep lists a file with "lines not shown" | The page spent its 8 MiB multiline read budget on the files above it (GHSA-v284-9964-6mjr). The reply names the `pageSize=1` page that shows that file. |

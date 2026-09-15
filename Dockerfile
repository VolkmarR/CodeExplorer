# syntax=docker/dockerfile:1

# The image ADR-0004 asks for: the operator UI built into wwwroot, the API published beside it, and
# the DuckDB fts extension already on disk so a replica never reaches the network for it. `INSTALL
# fts` fails silently when it cannot reach out, and a whole replica then answers by substring scan,
# which ranks differently and raises nothing an operator would see (#14).


# The UI first, because the publish below copies its output and nothing in the .NET build produces
# it: `vp build` writes into CodeExplorer/wwwroot and runs outside msbuild (ADR-0004).
# Debian and not Alpine: Vite+ is Rolldown and oxlint, whose native binaries are built against glibc.
FROM node:24-bookworm-slim AS web
WORKDIR /src/web
RUN corepack enable
# Manifests before sources, so the install layer is reused whenever only application code changed.
# pnpm-workspace.yaml is one of them: it carries the minimum-release-age exclusions the lockfile
# was resolved under, and pnpm refuses a frozen install that disagrees with it.
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
RUN --mount=type=cache,target=/root/.local/share/pnpm/store pnpm install --frozen-lockfile
COPY web/ ./
RUN pnpm exec vp build


# The API, published framework-dependent: the runtime image below carries the framework, and a
# self-contained publish would only duplicate it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# .editorconfig is part of the build and not only of the editor: it is where the analyzer severities
# live, and `TreatWarningsAsErrors` turns every rule it switches off into a failed image build if it
# is missing — seventeen CA1848 errors on log calls that are deliberate.
COPY global.json Directory.Build.props .editorconfig ./
COPY CodeExplorer/CodeExplorer.csproj CodeExplorer/
RUN dotnet restore CodeExplorer/CodeExplorer.csproj
COPY CodeExplorer/ CodeExplorer/
COPY --from=web /src/CodeExplorer/wwwroot CodeExplorer/wwwroot
RUN dotnet publish CodeExplorer/CodeExplorer.csproj -c Release --no-restore -o /app

# The bake. It runs the published application rather than a tool of its own, because an extension is
# stamped with the DuckDB version and platform that will load it, and only that build knows both.
# This is the one step in the image build that needs the network, and it fails the build when it
# cannot reach out — which is the whole point: the alternative is a silent downgrade at runtime.
RUN dotnet /app/CodeExplorer.dll --install-fts /duckdb/extensions


FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Where the extension went, and the ephemeral disk ADR-0003 budgets at 8 GiB: clones, indexes,
# scratch Parquet, the control database and DuckDB's spill files all sit under the data directory, so
# one path is what an operator mounts and watches. Both are settings, so a deployment can move them.
#
# Fts and not the Auto default, which is the other half of baking the extension in. Auto treats a
# failed load as an answer and falls back to substring scan, so an extension that went missing from
# this image — a path typo, a stage that stopped producing it — would come up green and quietly
# reorder every result. Pinned, the same fault stops the replica at startup, which is what an
# operator can see. Auto stays the right default off a developer's machine, where offline is normal.
ENV Index__ExtensionDirectory=/duckdb/extensions \
    Index__SearchEngine=Fts \
    Storage__DataDirectory=/data
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=$APP_UID:$APP_UID /duckdb/extensions /duckdb/extensions
# The application writes here on its first request, and a non-root user cannot create it at /.
RUN mkdir /data && chown $APP_UID:$APP_UID /data
# No git binary is installed anywhere above: LibGit2Sharp bundles libgit2 and clones through it, so a
# token never reaches process arguments (ADR-0003).
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CodeExplorer.dll"]

# Deploying to Azure Container Apps

The deployed shape ADR-0003 and ADR-0004 were written against: one container, the ephemeral disk as
working storage, Blob Storage as the only thing that survives a stop, Key Vault wrapping the key
ring, Entra in front of every endpoint, and a cron job outside the app doing the work no in-process
timer can do under scale to zero.

Nothing here is covered by the tests. They assert that a configured deployment gets a blob repository
and a Key Vault encryptor, and nothing in them reaches an account (README, "Running it"). Treat the
verification section at the end as part of the deployment rather than as an optional check.

## What you are deploying

| Piece                    | Why it exists                                                                      |
| ------------------------ | ---------------------------------------------------------------------------------- |
| Container app            | The whole server: API, MCP endpoints, operator UI. One deployable (ADR-0005).       |
| Storage account container | Parquet per project, the control database's backup, the Data Protection key ring.  |
| Key Vault key            | Wraps the key ring, so reading the blob is not enough to read a stored credential.  |
| Container registry       | Where the image built from this repo's `Dockerfile` lands.                          |
| Container Apps Job       | The cron that calls `/api/warmup` and the per-project refresh.                      |
| Log Analytics workspace  | Where container logs go. Traces and metrics go wherever `Telemetry:OtlpEndpoint` points. |
| Entra app registration   | One registration serving both the browser sign-in and the bearer API (ADR-0004).    |

## Before you start

- The Azure CLI, signed in, with the `containerapp` extension: `az extension add --name containerapp`.
- Docker, to build the image — and the .NET 10 SDK if you drive it through `build.cs` rather than
  calling `docker` yourself. Neither is needed for the `az acr build` in section 2, which builds in
  ACR Tasks. The build needs the network — the `fts` bake is deliberately the one step that fails
  the build rather than degrading at runtime (ADR-0004, #14).
- Permission to create role assignments on the storage account and the key vault. The app
  authenticates with `DefaultAzureCredential` and no connection string, so RBAC is the whole of its
  access (`DurableStore.Credential`).

Names used below; substitute your own.

```bash
RG=codeexplorer
LOC=westeurope
ACR=codeexplorerreg          # globally unique, alphanumeric
SA=codeexplorerstore         # globally unique, lowercase
CONTAINER=codeexplorer
KV=codeexplorer-kv           # globally unique
APP=codeexplorer
ENVNAME=codeexplorer-env
```

## 1. The resources

```bash
az group create -n $RG -l $LOC

az acr create -g $RG -n $ACR --sku Basic

az storage account create -g $RG -n $SA -l $LOC --sku Standard_LRS --kind StorageV2
az storage container create --account-name $SA -n $CONTAINER --auth-mode login

az keyvault create -g $RG -n $KV -l $LOC --enable-rbac-authorization true
az keyvault key create --vault-name $KV -n keyring --kty RSA --size 2048

az containerapp env create -g $RG -n $ENVNAME -l $LOC
```

The container must exist before the app starts; the app creates blobs in it but not the container
itself, and the key ring's blob is created on the first key written (`KeyRing.BlobName`).

## 2. The image

```bash
az acr build -g $RG -r $ACR -t codeexplorer:$(git rev-parse --short HEAD) -t codeexplorer:latest .
```

`az acr build` runs the same `Dockerfile` in ACR Tasks, which saves pushing an image over your
connection. `docker build` and `az acr login && docker push` do the same thing if you would rather
build locally, and

```bash
dotnet run build.cs -- --target=Package-Container --registry=$ACR.azurecr.io --push
```

is those two with the same two tags as above, after `az acr login -n $ACR`.

One build from a clean checkout produces both halves: the UI through `vite-plus` into
`CodeExplorer/wwwroot`, then the framework-dependent publish, then the `fts` bake. The image sets
`Index__SearchEngine=Fts` so an extension that ever went missing from it stops the replica at startup
instead of quietly reordering every result.

## 3. The app, with an identity

```bash
az containerapp create -g $RG -n $APP --environment $ENVNAME \
  --image $ACR.azurecr.io/codeexplorer:latest \
  --registry-server $ACR.azurecr.io --registry-identity system \
  --system-assigned \
  --target-port 8080 --ingress external \
  --cpu 2 --memory 4Gi \
  --min-replicas 0 --max-replicas 1
```

**`--max-replicas 1` is not a starting point to tune later.** Every project's index, the shadow file
a rebuild writes and the control database all live on the replica's own ephemeral disk, and the gate
that allows one rebuild at a time is in process (ADR-0003). A second replica would hold its own copy
of `control.duckdb`, restored from the same blob and written back over the first one's changes.
Scale out is a change to the storage design, not a slider.

`--cpu 2 --memory 4Gi` is the top of the Consumption plan, and the reason to ask for it is not CPU:
Container Apps allocates ephemeral storage by vCPU, "over 1 vCPU" is the top of that table, and the
8 GiB it yields is the hard ceiling ADR-0003 budgets — indexes, the shadow file during a rebuild,
the bare clones and DuckDB's spill files all share it. Microsoft documents nothing about what
happens when you exceed it, which is why `Refresh:MinimumFreeBytes` guards a rebuild before it
starts.

### Roles

```bash
ID=$(az containerapp show -g $RG -n $APP --query identity.principalId -o tsv)
SCOPE_SA=$(az storage account show -g $RG -n $SA --query id -o tsv)
SCOPE_KV=$(az keyvault show -g $RG -n $KV --query id -o tsv)

az role assignment create --assignee-object-id $ID --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" --scope $SCOPE_SA/blobServices/default/containers/$CONTAINER
az role assignment create --assignee-object-id $ID --assignee-principal-type ServicePrincipal \
  --role "Key Vault Crypto User" --scope $SCOPE_KV
az role assignment create --assignee-object-id $ID --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope $(az acr show -g $RG -n $ACR --query id -o tsv)
```

`Key Vault Crypto User` is the smallest role that carries wrap and unwrap, which is all the key ring
needs — it never reads the key material. Role assignments take a minute or two to propagate; a first
start that fails on the blob or the vault is usually this and not configuration.

## 4. Settings

Environment variables, with `__` where a setting has a `:`.

| Setting                       | Value                                                  | What it decides                                         |
| ----------------------------- | ------------------------------------------------------ | ------------------------------------------------------- |
| `Storage__BlobContainerUrl`   | `https://<sa>.blob.core.windows.net/<container>`        | Durable copy **and** the key ring. Absent means a folder on the wiped disk. |
| `Storage__KeyVaultKeyUrl`     | `https://<kv>.vault.azure.net/keys/keyring`             | Wraps the key ring. Ignored, with a warning, if no container is set. |
| `AzureAd__ClientId`           | the registration's application id                       | Turns authentication on at all.                          |
| `AzureAd__TenantId`           | the directory id                                        | The tenant to check against. Set without a client id it does nothing; a client id without it stops the server. |
| `AzureAd__ClientSecret`       | a secret reference                                      | Only the browser sign-in needs it.                       |
| `AzureAd__Scopes`             | `api://<client-id>/CodeExplorer.Access`                 | What a project's protected-resource document tells an MCP client to ask for. |
| `Refresh__WarmUpOnStart`      | `false` under scale to zero, `true` at `minReplicas` 1  | See "Warm-up" below.                                     |
| `Git__TransferStallSeconds`   | unset (300), or more for a remote slow to start a pack  | How long a clone or fetch waits on a silent remote before the repository is skipped. 1 to 2147483; zero is refused, not "no limit". |
| `Index__MaxFileBytes`         | unset (25 MiB)                                          | The largest file a refresh reads. A larger one is listed but not indexed, and a commit that changed it is recorded in history with no line counts, so a refresh inflates no file larger than this. Raising it raises the memory a refresh needs. |
| `Control__AllowLocalRepositories` | unset (`false`)                                     | Whether a local path or `file://` URL may be added as a repository. Leave it off: it lets whoever may add a repository read any repository on the replica's disk. |
| `AllowedHosts`                | unset with a tenant; the app's FQDN without one         | With no `AzureAd__ClientId` the server answers only `localhost`, `127.0.0.1` and `[::1]`, so ingress traffic gets a 400 until this names the FQDN. |
| `Telemetry__OtlpEndpoint`     | your collector, or unset                                | Unset exports nothing at all.                            |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true`                                          | See "Ingress terminates TLS" below.                      |

`Storage__DataDirectory`, `Index__ExtensionDirectory` and `Index__SearchEngine` are already set in
the image and should be left alone unless you are moving the data directory to a mount.

```bash
az containerapp secret set -g $RG -n $APP --secrets aad-secret=<the client secret>

az containerapp update -g $RG -n $APP --set-env-vars \
  Storage__BlobContainerUrl=https://$SA.blob.core.windows.net/$CONTAINER \
  Storage__KeyVaultKeyUrl=https://$KV.vault.azure.net/keys/keyring \
  AzureAd__ClientId=<client-id> \
  AzureAd__TenantId=<tenant-id> \
  AzureAd__ClientSecret=secretref:aad-secret \
  AzureAd__Scopes=api://<client-id>/CodeExplorer.Access \
  ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
```

### Ingress terminates TLS

Container Apps' ingress speaks HTTPS to the world and plain HTTP to the container, and the app does
not call `UseForwardedHeaders` itself. Without `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` the
framework builds absolute URLs from the scheme it actually sees, so the sign-in redirect goes out as
`http://…/signin-oidc` and Entra refuses it. The same scheme decides the server's own origin, so
without the setting every write from the UI, whose `Origin` is `https://…`, is refused with a 403
(GHSA-qxhv-3r9w-q8h4). Set it before you test sign-in, and treat a failing
sign-in with a working API as this until proven otherwise. This is the one item in this guide that
neither the tests nor the repo exercise, so confirm it against your own deployment rather than
trusting the paragraph.

### Health probes

There is no health endpoint. `/api/projects` requires an authenticated caller once a tenant is
configured, so it is not one either. Leave the default TCP probe alone rather than pointing an HTTP
probe at a path that answers 401 or 404; adding a real `/health` endpoint is a change to the app, not
to the deployment.

## 5. The Entra registration

One registration covers both flows, as ADR-0004 says.

- **Redirect URI**, Web platform: `https://<app fqdn>/signin-oidc`, and the front-channel logout URL
  `https://<app fqdn>/signout-callback-oidc`. These are Microsoft.Identity.Web's defaults and the app
  does not override them.
- **A client secret**, for the browser sign-in half only. A bearer client never needs it.
- **Expose an API**: set the application ID URI to `api://<client-id>` and add a scope,
  `CodeExplorer.Access`. This is what `AzureAd__Scopes` advertises, and what an MCP client asks for
  after it reads a project's protected-resource document.
- **An app role**, for the cron in the next section: an application-type role, say
  `CodeExplorer.Refresh`. Entra will not issue a client-credentials token for
  `api://<client-id>/.default` unless the calling application has been granted something, so the role
  exists to make that grant possible. The app itself checks only that the caller is authenticated —
  there is deliberately no per-project authorization, and every authenticated caller reaches every
  project.

A machine-to-machine client — the cron, an agent host — gets its own registration, is granted that
app role, and requests `api://<client-id>/.default`.

## 6. Warm-up and refresh

Projects attach on first connection, so a replica waking with an empty disk restores only the project
being connected to. Restoring all of them before working hours is a call from outside, because a
stopped container has nothing running to fire a timer and that call is also what wakes it.

```bash
az containerapp job create -g $RG -n codeexplorer-warmup --environment $ENVNAME \
  --trigger-type Schedule --cron-expression "0 6 * * 1-5" \
  --image $ACR.azurecr.io/codeexplorer-cron:latest \
  --system-assigned --cpu 0.5 --memory 1Gi
```

The job's image is yours to write — anything that can fetch a token and make one POST. Its shape:

1. Get a token for `api://<client-id>/.default` with the job's identity.
2. `POST https://<app fqdn>/api/warmup` with it. That single call walks every project and restores
   each one.
3. Optionally, for each project you want rebuilt overnight,
   `POST https://<app fqdn>/api/projects/{slug}/refresh`, then poll `GET …/refresh` for progress.
   One rebuild runs at a time across the whole server, so a loop that fires them all at once is
   answered in sequence rather than in parallel.

Under `minReplicas` 1 — a deployment kept warm rather than scaled to zero — set
`Refresh__WarmUpOnStart=true` and the replica restores every project once as it starts, in the
background, while it is already answering. Do not set it under scale to zero: wakes are frequent
there, and warming every project on each one is the exact cost that lazy attach exists to avoid
(ADR-0004).

## 7. Verify it

The Azure half is the half the tests do not reach, so check it end to end once, on the real
deployment:

1. `az containerapp logs show -g $RG -n $APP --tail 50`. The app says which key ring shape it came
   up in and which authentication shape, at start. You want the persisted, wrapped key ring and a
   configured tenant. A warning naming either of the other two shapes means a setting did not arrive.
2. Sign in to the UI in a browser. A redirect to `http://` is the forwarded-headers item above.
3. Add a repository with a credential, then confirm `keyring/keys.xml` appeared in the container:
   `az storage blob list --account-name $SA -c $CONTAINER --prefix keyring --auth-mode login -o table`.
4. Restart the app — `az containerapp revision restart` — and refresh that repository. A clone that
   authenticates is the key ring having survived; a clone that fails on credentials is a key ring
   that did not.
5. Point an MCP client at `https://<app fqdn>/projects/<slug>/mcp`. The 401 it gets first carries the
   `resource_metadata` URL, and that document names the authority to sign in against.

## 8. What survives, and what does not

The ephemeral disk is wiped on every stop. What comes back is what is in the container: each
project's Parquet set, the control database's backup, and the key ring. Everything else — the clones,
the attached DuckDB files, the scratch Parquet, DuckDB's spill — is rebuilt or re-fetched.

Two consequences worth stating once:

- **Deleting the blob container is deleting the deployment**, credentials included. There is no
  second copy.
- **A key ring lost is every stored repository credential lost.** They are `IDataProtector`
  ciphertext in the control database, and the key ring in the container is what decrypts them.
  Restoring the control database without the key ring restores rows nobody can read; every
  credential has to be entered again.

## Upgrading

`az acr build` a new tag, then `az containerapp update -g $RG -n $APP --image
$ACR.azurecr.io/codeexplorer:<tag>`. Container Apps replaces the revision, so the new replica starts
with an empty disk and restores lazily as ADR-0003 intends.

A release that raises `SchemaVersion` makes the first warm-up after it long, because every project is
rebuilt rather than restored (ADR-0007). Deploy those before a warm-up window rather than during one.

The first release with GHSA-5373-pppr-q3q9 refuses local repositories unless
`Control__AllowLocalRepositories` is `true`, and that covers ones stored before it: a refresh
reports each as skipped until it is pointed at a remote. The first with GHSA-qxhv-3r9w-q8h4 refuses
a foreign `Origin` on `/api` and the MCP endpoints, so a replica missing
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` now fails every write from the UI, not only the sign-in.
The first with GHSA-4f8q-c6jj-fr44 refuses a credential beside an `http://` or `git://` URL, which
would send it in clear text, and there is no setting to allow it: a repository stored with that pair
is reported as skipped by every refresh, and its token never sent, until it is deleted and added
again under its https or ssh URL. The first with GHSA-v284-9964-6mjr bounds what one MCP call can ask
of a replica, with no setting for any of it: `list_tree` depth at most 64, `read_file` at most 100
entries and 100,000 lines or 16 million characters across them, a `multiline` grep page at most 8 MiB
of file content beyond its first file, and a glob or path term at most 256 characters, 32 terms to an
argument. Globs keep their meaning but run as RE2, so an agent whose glob or `exclude`
held a query for minutes gets its answer, and one that passed a larger depth or more entries is told
the limit and how to split the call.

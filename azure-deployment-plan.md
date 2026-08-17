# dispatch-api — Azure deployment plan (phases 4–7)

Supersedes the AWS phases of the earlier plan. Phases 1–3 are **done**: container tooling
installed, local `docker compose` verified green (sql healthy, `/health` 200,
duplicate-assign 400), stack torn down with `docker compose down -v`.

Target changed from AWS to Azure because the repo already targets Azure
(`.github/workflows/ci-cd.yml:79-105`, `README.md:109-174`) and because the Azure free
grants are per-subscription rather than signup-only.

## Decisions taken

| Fork | Choice |
|---|---|
| Cloud | Azure, new signup |
| Database | Azure SQL Database free offer (managed, durable) |
| Registry | Azure Container Registry, Basic SKU |
| Region | `eastus` |
| Resource group | `rg-dispatch-api` — everything in one, so teardown is a single command |

## Cost model — read before starting

Two grants apply for the lifetime of the subscription, independent of signup credit:

- **Container Apps consumption**: 180,000 vCPU-s + 360,000 GiB-s + 2M requests / month.
- **Azure SQL free offer**: 100,000 vCore-s + 32 GB data + 32 GB backup / month, up to 10
  General Purpose databases.

**The Container Apps grant is not "always-on forever free."** One replica at 0.5 vCPU /
1.0 GiB burns the vCPU-s grant in ~100 hours (`180,000 / 0.5`) and the GiB-s grant in the
same ~100 hours. That is ~4 days of continuous running, not a month. Verify, then tear
down the same day — or set `--min-replicas 0` and accept cold starts.

**ACR Basic is not free**: ~$0.167/day. A few days is ~$0.50, and it is deleted at teardown.

Ordering rule carried over from the AWS plan and **not to be shuffled**: MFA and the budget
alert come before any billable resource.

---

## Phase 4 — Azure account and CLI

Both CLIs are installed: **`az` 2.89.1** and **`gh` 2.97.0**, on the machine PATH
(`C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin`, `C:\Program Files\GitHub CLI`). No install
step needed. If a shell started before the install cannot see them, that shell's PATH is stale
— open a new one, or prepend those two directories to `$env:Path` for the call.

1. Signup at <https://azure.microsoft.com/free>. A **credit card is required**, with a small
   temporary authorization. New accounts get $200 credit for 30 days plus 12 months of
   selected free services. I open the tab; **I do not enter personal or payment details.**
2. **MFA on the account owner.** New Entra tenants have security defaults enabled, which
   enforces MFA — confirm it is actually on rather than assuming.
3. **Budget before any resource**: Cost Management + Billing → Budgets → $5/month, alerts at
   50/80/100% to your email.
4. Authenticate the CLI:

```powershell
az login
az account show
az group create -n rg-dispatch-api -l eastus
```

Verification: `az account show` returns a subscription id and tenant id; the budget is
visible in the portal before step 4 creates anything.

---

## Phase 5 — ACR: image into the cloud

ACR names are globally unique, lowercase alphanumeric, 5–50 chars. Pick one and keep it
consistent below — `<acr>` stands in for it.

```powershell
az acr create -n <acr> -g rg-dispatch-api --sku Basic
az acr login -n <acr>
docker build -t dispatch-api:local .
docker tag dispatch-api:local <acr>.azurecr.io/dispatch-api:v1
docker push <acr>.azurecr.io/dispatch-api:v1
az acr repository show-tags -n <acr> --repository dispatch-api
```

Teaching points: registry vs repository vs tag; `az acr login` mints a **short-lived token**
into Docker's credential store rather than storing a password; re-push the same image and
watch every layer report as already present.

The local image is `linux/amd64`, which is what Container Apps runs — no cross-arch problem.

---

## Phase 6 — Azure SQL + Container Apps

### 6a. Database

```powershell
az sql server create -n sql-dispatch-<unique> -g rg-dispatch-api -l eastus `
  --admin-user sqladmin --admin-password '<strong-password>'

# Allow Azure-internal callers (the 0.0.0.0 sentinel), plus your own IP for sqlcmd testing.
az sql server firewall-rule create -g rg-dispatch-api -s sql-dispatch-<unique> `
  -n allow-azure --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
az sql server firewall-rule create -g rg-dispatch-api -s sql-dispatch-<unique> `
  -n my-ip --start-ip-address <your-ip> --end-ip-address <your-ip>

az sql db create -g rg-dispatch-api -s sql-dispatch-<unique> -n Dispatch `
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 2 `
  --use-free-limit --free-limit-exhaustion-behavior AutoPause
```

`--free-limit-exhaustion-behavior AutoPause` means the database **stops instead of billing**
when the monthly grant is exhausted. That is the deliberate choice, matching the "spend stops"
rule.

**Why the database is created explicitly:** `Program.cs:26-31` calls
`db.Database.EnsureCreated()`, which issues `CREATE DATABASE [Dispatch]` when the database is
absent — that worked locally (visible in the compose logs) but is not the right path on an
Azure SQL logical server. Creating it here means `EnsureCreated()` finds it and only creates
the tables.

**Connection string differs from `docker-compose.yml:23` in three ways** — encryption is
mandatory, and the serverless resume needs a longer timeout:

```
Server=tcp:sql-dispatch-<unique>.database.windows.net,1433;Database=Dispatch;User ID=sqladmin;Password=<strong-password>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;
```

Compose used `Encrypt=False;TrustServerCertificate=True` against a local container — fine
there, wrong here.

**Known risk:** the app opens its connection during startup (`Program.cs:26-31`) and
`Program.cs:13` configures `UseSqlServer` with no `EnableRetryOnFailure`. If the serverless
database is auto-paused, the resume can take 30–60s and a short timeout would crash the
container on boot. `Connection Timeout=60` is the mitigation; if it still trips, the honest
fix is adding a retry policy, which is an app code change and therefore a decision to raise
rather than make silently.

### 6b. Container App

```powershell
az extension add -n containerapp --upgrade
az provider register -n Microsoft.App
az provider register -n Microsoft.OperationalInsights

az containerapp env create -n cae-dispatch -g rg-dispatch-api -l eastus

az containerapp create -n ca-dispatch-api -g rg-dispatch-api --environment cae-dispatch `
  --image <acr>.azurecr.io/dispatch-api:v1 `
  --target-port 8080 --ingress external `
  --system-assigned `
  --registry-server <acr>.azurecr.io --registry-identity system `
  --min-replicas 1 --max-replicas 1 --cpu 0.5 --memory 1.0Gi `
  --secrets sqlconn='<connection string from 6a>' `
  --env-vars ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Dispatch=secretref:sqlconn
```

`--registry-identity system` is the point of choosing ACR: Container Apps assigns its own
system-assigned identity the `AcrPull` role and pulls with **no stored credential anywhere**.
Contrast with the `MSSQL_SA_PASSWORD` sitting in plaintext at `docker-compose.yml:6` — fine
locally, wrong in a cloud resource, which is why the connection string goes through
`--secrets` and is referenced as `secretref:sqlconn`.

`ASPNETCORE_ENVIRONMENT: Development` is still required — it is what gates `EnsureCreated()`
at `Program.cs:26-31`, and no migrations exist yet (`README.md:99-101`).

**Security, stated plainly:** the API has **no authentication** (`README.md:101-102`) and
Development mode publishes Swagger. `--ingress external` therefore puts a public write
endpoint on the internet. Restrict it to your own IP immediately, the same discipline the
AWS plan applied to its security group:

```powershell
az containerapp ingress access-restriction set -n ca-dispatch-api -g rg-dispatch-api `
  --rule-name my-ip --ip-address <your-ip>/32 --action Allow
```

### Verification for phase 6

```powershell
$fqdn = az containerapp show -n ca-dispatch-api -g rg-dispatch-api `
  --query properties.configuration.ingress.fqdn -o tsv
Invoke-WebRequest "https://$fqdn/health" -UseBasicParsing        # expect 200 Healthy
az containerapp logs show -n ca-dispatch-api -g rg-dispatch-api --tail 100
```

Then repeat the phase-3 walkthrough against `https://$fqdn`: `POST /api/units`,
`POST /api/incidents`, `POST /api/incidents/{id}/assign`, and **assign the same unit twice —
the second must return 400**. Logs should show `CREATE TABLE [Incidents]/[Units]/[Assignments]`
but **not** `CREATE DATABASE`, since 6a created it.

### Teardown — same day

```powershell
az group delete -n rg-dispatch-api --yes --no-wait
```

One command removes the Container App, environment, ACR, SQL server, and Log Analytics
workspace. Confirm afterwards with `az group list -o table`.

---

## Phase 7 — GitHub repo and CI/CD

The folder is **not a git repo yet**. `gh` 2.97.0 is installed, so after `git init` the repo can
be created from the command line — `gh auth login`, then `gh repo create <name> --private
--source . --remote origin --push` — rather than clicking through the browser. `.gitignore`
already covers `bin/`, `obj/`, `TestResults/`, `.env`.

`gh` also removes the manual step from the OIDC wiring below: repo secrets can be set with
`gh secret set AZURE_CLIENT_ID` and friends.

`.github/workflows/ci-cd.yml` needs less work than the AWS version would have:

- **`build-and-test` (`:14-39`)** — cloud-agnostic. Keep verbatim.
- **`publish-image` (`:41-77`)** — currently pushes to GHCR. Swap the login and image name for
  ACR (`azure/login` with OIDC, then `az acr login`), keeping the short-SHA + `latest` tagging.
- **`deploy` (`:79-105`)** — already `azure/login` + `az containerapp update`. Only the
  resource group, app name, and image reference change.
- **Health poll (`:107-121`)** — keep verbatim; it is provider-independent and already correct.

Auth is **GitHub OIDC → Entra workload identity federation**, no stored secrets: an app
registration with a federated credential whose subject is
`repo:<owner>/<repo>:ref:refs/heads/main`, granted `Contributor` on `rg-dispatch-api` and
`AcrPush` on the registry. Repo secrets are only the non-sensitive
`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID`.

`README.md:147-162` already describes exactly this federated-credential approach, so the
README work is **correcting names and drift**, not rewriting a section that describes a
deployment that no longer exists.

---

## Carried-over facts worth not rediscovering

- **`InvariantGlobalization` must stay `false`** (`src/DispatchApi/DispatchApi.csproj`). With
  it true, `Microsoft.Data.SqlClient` throws `CultureNotFoundException` on every connection
  open. This crashed the local container and would have crashed Container Apps identically.
  Setting `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` does **not** help — `runtimeconfig.json`
  wins over the env var.
- **Restore-layer caching works as designed**: editing a `.cs` file leaves
  `[build 6/9] RUN dotnet restore` `CACHED` and re-runs only `COPY . .` onward. Editing a
  `.csproj` invalidates the restore.
- **`wsl --install` is broken on this machine** — use `dism.exe /online /enable-feature`;
  exit 3010 means success with a reboot pending.

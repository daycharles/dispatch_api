# dispatch-api

A containerised ASP.NET Core Web API that models a minimal slice of computer-aided
dispatch: incidents, responding units, and the assignment lifecycle between them.
Built to be small enough to read in one sitting and complete enough to run in the
cloud.

**Stack:** C# / .NET 8 · ASP.NET Core Minimal APIs · Entity Framework Core ·
Microsoft SQL Server · xUnit · Docker · GitHub Actions · Azure Container Apps

---

## Why this exists

Dispatch is a good small domain for demonstrating a few things that are hard to
show in a CRUD sample:

- **State machines with real rules.** A unit cannot be committed to two calls at
  once. A closed incident cannot take new units. Clearing the last unit returns
  the call to the queue rather than leaving it looking handled.
- **A metric that has to be defined carefully.** `TimeToFirstAssignmentSeconds`
  measures receipt to the *first* unit assigned. Later units joining the call must
  not reset it — that is the bug that makes response-time reporting useless, and
  there is a test pinning the behaviour.
- **Ordering that belongs on the server.** The dispatcher queue sorts by priority,
  then by age, so every client sees the same order.

## Running it locally

```bash
docker compose up --build
```

Then open <http://localhost:8080/swagger>.

The compose file starts SQL Server 2022 and waits for it to pass a health check
before starting the API. First start takes a minute or two while SQL initialises.

### Without Docker

```bash
dotnet restore
dotnet test
dotnet run --project src/DispatchApi
```

You will need a reachable SQL Server; set the connection string via
`ConnectionStrings__Dispatch`.

## Tests

```bash
dotnet test
```

Thirteen unit tests cover the assignment rules, the response-time metric, and the
queue ordering. Time is injected through `IClock` so timing behaviour is tested
deterministically rather than by sleeping, and each test gets its own in-memory
store so they can run in parallel without leaking state.

## API

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/health` | Liveness probe |
| `GET` | `/api/units` | List units |
| `POST` | `/api/units` | Register a unit |
| `GET` | `/api/incidents` | Dispatcher queue, most urgent first |
| `GET` | `/api/incidents/{id}` | One incident with assigned units |
| `POST` | `/api/incidents` | Create an incident |
| `POST` | `/api/incidents/{id}/assign` | Assign a unit |
| `POST` | `/api/incidents/{id}/clear` | Clear a unit |
| `POST` | `/api/incidents/{id}/close` | Close and free all units |

Expected refusals — assigning a committed unit, assigning to a closed call —
return `400` with a message rather than throwing. Only genuinely unexpected
conditions become exceptions.

## Pipeline

`.github/workflows/ci-cd.yml` runs on every push and pull request:

1. **build-and-test** — restore, build in Release, run tests, upload the `.trx`.
2. **publish-image** — on `main` only: build the image and push it to GitHub
   Container Registry, tagged with the short SHA and `latest`. Layer cache is
   kept in GitHub Actions cache.
3. **deploy** — on `main` only: authenticate to Azure by OIDC federated
   credential (no stored secret), roll the Container App to the new image, then
   poll `/health` until it returns 200 or fail the run.

The Dockerfile is multi-stage: the SDK image restores, tests and publishes; the
runtime image carries only the published output and runs as the base image's
non-root `$APP_UID`.

## Known limitations

Stated deliberately rather than left for a reader to find:

- **Schema is created with `EnsureCreated()` in Development.** The next step is
  `dotnet ef migrations add Initial` so schema changes are versioned. Not done yet.
- **No authentication.** Any real deployment needs it; adding it here would have
  doubled the surface without demonstrating anything new.
- **Concurrency is optimistic-free.** Two dispatchers assigning the same unit in
  the same millisecond could both succeed. The fix is a rowversion token on
  `Unit` plus retry — worth doing, not done.
- **`GET /api/incidents` is unpaged.** Fine at demo volume, wrong at agency volume.
- **Single region, single replica.** No scale rules configured.

## One-time Azure setup

Only the registry costs money at rest (ACR Basic, ~$0.167/day); the database runs on the
Azure SQL free limit and the single 0.5-vCPU replica runs on the Container Apps free grant.
Tear the whole thing down with `az group delete -n rg-dispatch-api --yes`.

Names below are the ones actually in use, so they match `.github/workflows/ci-cd.yml`.

```bash
RG=rg-dispatch-api
LOC=eastus
ACR=acrdispatchapi                 # globally unique, lowercase alphanumeric
SQLSRV=sql-dispatch-cj2608-1
APP=ca-dispatch-api
ENVNAME=cae-dispatch

# A fresh subscription has no providers registered; each of these is required.
for p in Microsoft.ContainerRegistry Microsoft.App Microsoft.OperationalInsights Microsoft.Sql; do
  az provider register -n $p --wait
done

az group create -n $RG -l $LOC

# --- Registry, and the first image push from the workstation ---
az acr create -n $ACR -g $RG --sku Basic
az acr login -n $ACR
docker build -t dispatch-api:local .
docker tag dispatch-api:local $ACR.azurecr.io/dispatch-api:v1
docker push $ACR.azurecr.io/dispatch-api:v1

# --- Database. Note the region: eastus/eastus2 may refuse new SQL servers
#     ("RegionDoesNotAllowProvisioning"); centralus was used here. A failed attempt
#     pins the name to the failed region, so retry with a NEW server name. ---
az sql server create -n $SQLSRV -g $RG -l centralus \
  --admin-user sqladmin --admin-password '<STRONG_PASSWORD>'
az sql server firewall-rule create -n allow-azure -g $RG -s $SQLSRV \
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
az sql server firewall-rule create -n my-ip -g $RG -s $SQLSRV \
  --start-ip-address <YOUR_IP> --end-ip-address <YOUR_IP>

# Created explicitly so EnsureCreated() only builds tables, never CREATE DATABASE.
# AutoPause means the database stops instead of billing when the free grant runs out.
az sql db create -n Dispatch -s $SQLSRV -g $RG \
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 2 \
  --use-free-limit --free-limit-exhaustion-behavior AutoPause

# --- Container Apps. Ingress starts internal so the unauthenticated API is never
#     publicly reachable, even briefly, before the IP restriction is in place. ---
az containerapp env create -n $ENVNAME -g $RG -l $LOC

CONN='Server=tcp:'"$SQLSRV"'.database.windows.net,1433;Database=Dispatch;User ID=sqladmin;Password=<STRONG_PASSWORD>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'

az containerapp create -n $APP -g $RG --environment $ENVNAME \
  --image $ACR.azurecr.io/dispatch-api:v1 \
  --target-port 8080 --ingress internal \
  --system-assigned \
  --registry-server $ACR.azurecr.io --registry-identity system \
  --min-replicas 1 --max-replicas 1 --cpu 0.5 --memory 1.0Gi \
  --secrets sqlconn="$CONN" \
  --env-vars ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Dispatch=secretref:sqlconn

az containerapp ingress access-restriction set -n $APP -g $RG \
  --rule-name my-ip --ip-address <YOUR_IP>/32 --action Allow
az containerapp ingress enable -n $APP -g $RG --type external --target-port 8080 --transport auto

az containerapp show -n $APP -g $RG --query properties.configuration.ingress.fqdn -o tsv
```

`--registry-identity system` is the reason for choosing ACR over GHCR: Container Apps gets
`AcrPull` on its own system-assigned identity and pulls with no credential stored anywhere.
`ASPNETCORE_ENVIRONMENT=Development` is still required — it is what gates `EnsureCreated()`,
and no migrations exist yet. `Connection Timeout=60` covers a serverless resume, which can
take 30–60s; the app has no `EnableRetryOnFailure`.

Known CLI wart: `az containerapp create --registry-identity system` can return a bare
`(InternalServerError)` and still create the app — with the `k8se/quickstart` placeholder image
and no registry configured. Repair without recreating it:

```bash
PRINCIPAL=$(az containerapp show -n $APP -g $RG --query identity.principalId -o tsv)
az role assignment create --assignee-object-id $PRINCIPAL --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope $(az acr show -n $ACR -g $RG --query id -o tsv)
az containerapp registry set -n $APP -g $RG --server $ACR.azurecr.io --identity system
az containerapp update -n $APP -g $RG --image $ACR.azurecr.io/dispatch-api:v1
```

### Federated credential so Actions can deploy without a stored password

The subject pins one branch. This repo's default branch is `master`, which is what
`ci-cd.yml` triggers on — change both together or the token is rejected.

```bash
APPID=$(az ad app create --display-name gh-dispatch-api-oidc --query appId -o tsv)
SP=$(az ad sp create --id $APPID --query id -o tsv)

az role assignment create --assignee-object-id $SP --assignee-principal-type ServicePrincipal \
  --role Contributor --scope /subscriptions/<SUB>/resourceGroups/rg-dispatch-api
az role assignment create --assignee-object-id $SP --assignee-principal-type ServicePrincipal \
  --role AcrPush --scope /subscriptions/<SUB>/resourceGroups/rg-dispatch-api/providers/Microsoft.ContainerRegistry/registries/acrdispatchapi

az ad app federated-credential create --id $APPID --parameters '{
  "name": "gh-dispatch-api-master",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<GITHUB_USER>/dispatch_api:ref:refs/heads/master",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

Then set the repo secrets — none of these three is sensitive, and no password is stored:

```bash
gh secret set AZURE_CLIENT_ID       --body "$APPID"
gh secret set AZURE_TENANT_ID       --body "$(az account show --query tenantId -o tsv)"
gh secret set AZURE_SUBSCRIPTION_ID --body "$(az account show --query id -o tsv)"
```

No `APP_URL` variable is needed: the deploy job reads the FQDN from `az containerapp show`.
Because ingress is IP-restricted, that job also allow-lists the runner's own egress IP as a
rule named `gh-runner` for the length of the smoke test and removes it in an `if: always()`
step. Create an **Environment** named `production` under Settings → Environments, or remove
the `environment: production` line from the deploy job.

## Licence

MIT.

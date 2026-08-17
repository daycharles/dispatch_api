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

Costs nothing on the Container Apps free grant at this scale. Replace `<SUB>` and
`<GITHUB_USER>` before running.

```bash
RG=dispatch-api-rg
LOC=eastus
APP=dispatch-api
ENVNAME=dispatch-env

az group create -n $RG -l $LOC
az containerapp env create -n $ENVNAME -g $RG -l $LOC

# Deploy a placeholder so the app exists; the pipeline rolls it forward.
az containerapp create \
  -n $APP -g $RG --environment $ENVNAME \
  --image mcr.microsoft.com/k8se/quickstart:latest \
  --target-port 8080 --ingress external \
  --min-replicas 0 --max-replicas 1

# Managed SQL, or point the connection string at anything reachable.
az sql server create -n ${APP}-sql -g $RG -l $LOC \
  --admin-user sqladmin --admin-password '<STRONG_PASSWORD>'
az sql db create -n Dispatch -s ${APP}-sql -g $RG --service-objective Basic
az sql server firewall-rule create -n AllowAzure -g $RG -s ${APP}-sql \
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0

az containerapp secret set -n $APP -g $RG \
  --secrets dispatch-conn='Server=tcp:<APP>-sql.database.windows.net,1433;Database=Dispatch;User ID=sqladmin;Password=<STRONG_PASSWORD>;Encrypt=True;TrustServerCertificate=False'

az containerapp update -n $APP -g $RG \
  --set-env-vars ConnectionStrings__Dispatch=secretref:dispatch-conn

# Print the public URL — put this in the repo variable APP_URL.
az containerapp show -n $APP -g $RG --query properties.configuration.ingress.fqdn -o tsv
```

### Federated credential so Actions can deploy without a stored password

```bash
APPID=$(az ad app create --display-name dispatch-api-gh --query appId -o tsv)
az ad sp create --id $APPID
az role assignment create --assignee $APPID \
  --role Contributor \
  --scope /subscriptions/<SUB>/resourceGroups/dispatch-api-rg

az ad app federated-credential create --id $APPID --parameters '{
  "name": "gh-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<GITHUB_USER>/dispatch-api:ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

Then in the GitHub repo, under **Settings → Secrets and variables → Actions**:

| Kind | Name | Value |
|---|---|---|
| Secret | `AZURE_CLIENT_ID` | the `$APPID` above |
| Secret | `AZURE_TENANT_ID` | `az account show --query tenantId -o tsv` |
| Secret | `AZURE_SUBSCRIPTION_ID` | `az account show --query id -o tsv` |
| Variable | `APP_URL` | `https://<fqdn from the last command>` |

Create an **Environment** named `production` under Settings → Environments, or
remove the `environment: production` line from the deploy job.

## Licence

MIT.

# dispatch-api

A containerised ASP.NET Core Web API that models a minimal slice of computer-aided
dispatch: incidents, responding units, and the assignment lifecycle between them.
Built to be small enough to read in one sitting and complete enough to run in the
cloud.

**Stack:** C# / .NET 8 · ASP.NET Core Minimal APIs · Entity Framework Core ·
Microsoft SQL Server · RabbitMQ · xUnit · Docker · GitHub Actions ·
Azure Container Apps

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
- **Asynchronous work that must not lose or double-apply a message.** Incident
  transitions are published to RabbitMQ and turned into notifications by a
  background consumer. The dead-letter exchange is fanout rather than topic,
  because a dead-lettered message keeps its original routing key and a topic DLX
  would silently drop anything it had no binding for. The queue is quorum rather
  than classic, because notifications have to survive the loss of the node
  holding them. The consumer's identity is a compile-time constant rather than
  configuration, because it is half of an idempotency key already in the
  database — a typo in `appsettings.json` would replay every message the service
  has ever handled.

## Running it locally

```bash
docker compose up --build
```

Then open <http://localhost:8080/swagger>.

The compose file starts SQL Server 2022 and RabbitMQ 3.12, and the API waits for
*both* to pass their health checks before it starts. First start takes a minute or
two while SQL initialises.

The broker's management UI is at <http://localhost:15672> (guest/guest), which is
where the exchange, the queue's quorum type and its dead-letter arguments can
actually be seen.

### Without Docker

```bash
dotnet restore
dotnet test
dotnet run --project src/DispatchApi
```

You will need a reachable SQL Server, set via `ConnectionStrings__Dispatch`, and a
reachable broker — see [Configuration](#configuration) for the `Messaging__*`
variables. If you have no broker, `Messaging__Enabled=false` is the supported
escape hatch: it swaps in `NullIncidentPublisher`, starts no consumer, and
registers no broker health check, so the HTTP API works and nothing is published.

## Tests

```bash
dotnet test
```

Twenty-six unit tests. Thirteen (`DispatchServiceTests.cs`) cover the assignment
rules, the response-time metric, and the queue ordering; thirteen
(`MessagingTests.cs`) cover which transitions publish which events, the topology
arguments, idempotent handling of a redelivered message, and poison versus
transient failure routing. No test touches a live broker — see
[Verifying the topology](#verifying-the-topology) for the half that needs one.
Time is injected through `IClock` so timing behaviour is tested
deterministically rather than by sleeping, and each test gets its own in-memory
store so they can run in parallel without leaking state.

## API

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/health` | Liveness only — deliberately asserts nothing |
| `GET` | `/health/ready` | Readiness; includes the broker when messaging is enabled |
| `GET` | `/api/units` | List units |
| `POST` | `/api/units` | Register a unit |
| `GET` | `/api/incidents` | Dispatcher queue, most urgent first |
| `GET` | `/api/incidents/{id}` | One incident with assigned units |
| `POST` | `/api/incidents` | Create an incident |
| `POST` | `/api/incidents/{id}/assign` | Assign a unit |
| `POST` | `/api/incidents/{id}/clear` | Clear a unit |
| `POST` | `/api/incidents/{id}/close` | Close and free all units |
| `GET` | `/api/incidents/{id}/notifications` | Notifications raised asynchronously by the consumer |

Expected refusals — assigning a committed unit, assigning to a closed call —
return `400` with a message rather than throwing. Only genuinely unexpected
conditions become exceptions.

## Messaging

Incident transitions are published to RabbitMQ; a background consumer turns the
ones that matter into notifications, readable at
`GET /api/incidents/{id}/notifications`. `Messaging:Enabled` gates all of it.

### Topology

Every name and argument below is a constant in
`src/DispatchApi/Messaging/DispatchTopology.cs`, declared at startup by
`TopologyInitializer`, so a fresh broker needs no manual setup.

| Object | Name | Arguments |
|---|---|---|
| Exchange | `dispatch.events` | `topic`, durable |
| Dead-letter exchange | `dispatch.events.dlx` | `fanout`, durable |
| Queue | `dispatch.notifications` | `x-queue-type: quorum`, `x-dead-letter-exchange: dispatch.events.dlx`, `x-delivery-limit: 5` |
| Dead-letter queue | `dispatch.notifications.dlq` | `x-queue-type: quorum`; no DLX of its own |
| Binding | `incident.*` | `dispatch.events` → `dispatch.notifications` |

Routing keys: `incident.created`, `incident.assigned`, `incident.cleared`,
`incident.closed`.

Why those choices:

- **The DLX is fanout, not topic.** A dead-lettered message keeps its original
  routing key, so a topic DLX would need a binding per key and would silently drop
  anything it had no binding for — the one thing a DLX must not do.
- **The DLQ has no dead-letter exchange of its own.** A DLQ that dead-letters is a
  loop.
- **Quorum, not classic.** Notifications have to survive the loss of the node
  holding them.
- **`x-delivery-limit: 5`** is enough requeues to ride out a database restart, few
  enough that a genuinely stuck message does not hold up the queue.
- **One `incident.*` binding**, so a new `incident.*` type reaches the consumer
  without a broker change; the consumer ignores the events it has no opinion about.

A poison message — unknown routing key, malformed body — is nacked with
`requeue: false` and dead-letters immediately rather than burning five deliveries.
A transient failure is nacked with `requeue: true` and rides the delivery limit.

### Configuration

Section `Messaging` of `appsettings.json`; each key is also settable as an
environment variable in the double-underscore form.

| Key | Environment variable | Default |
|---|---|---|
| `Enabled` | `Messaging__Enabled` | `true` |
| `Host` | `Messaging__Host` | `localhost` |
| `Port` | `Messaging__Port` | `5672` |
| `UserName` | `Messaging__UserName` | `guest` |
| `Password` | `Messaging__Password` | `guest` |
| `VirtualHost` | `Messaging__VirtualHost` | `/` |
| `ClientName` | `Messaging__ClientName` | `dispatch-api` |
| `PrefetchCount` | `Messaging__PrefetchCount` | `16` |

Compose overrides exactly one of them, `Messaging__Host: rabbitmq`; everything else
comes from `appsettings.json`.

### Verifying the topology

The xUnit suite deliberately never touches a broker. `tools/verify_topology.py`
covers the half it cannot: that RabbitMQ actually accepts these queue arguments,
that the topic binding routes what it should and refuses what it should not, and
that both routes into the dead-letter queue behave as the code comments claim.

```bash
pip install pika
docker compose up -d rabbitmq
python3 tools/verify_topology.py
```

It creates and destroys its own queues and exits non-zero on failure — safe
against a local broker, pointless against a shared one. Start the broker on its
own, as above: with the API running, its consumer competes for the messages the
script publishes and the routing checks fail on an empty queue.

## Pipeline

`.github/workflows/ci-cd.yml` runs on every push and pull request:

1. **build-and-test** — restore, build in Release, run tests, upload the `.trx`.
2. **publish-image** — on `master` only: build the image and push it to Azure
   Container Registry (`acrdispatchapi.azurecr.io`), tagged with the short SHA and
   `latest`. Layer cache is kept in the GitHub Actions cache; the `Set up Buildx`
   step exists solely because the default docker driver cannot export a
   `type=gha` cache.
3. **deploy** — on `master` only: authenticate to Azure by OIDC federated
   credential (no stored secret), roll the Container App to the new image, then
   poll `/health` until it returns 200 or fail the run. That smoke test hits
   liveness, which by design would pass with SQL and the broker both down.

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
- **The deployed Container App has no broker.** `Messaging:Enabled` defaults to
  `true` and `Messaging:Host` to `localhost`, and nothing provisions RabbitMQ in
  Azure — so a deploy today publishes into a connection that cannot open and
  `/health/ready` reports Unhealthy. `/health` (liveness) still passes, which is
  why the pipeline's smoke test goes green. Until a broker is provisioned, set
  `Messaging__Enabled=false` on the Container App.

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
take 30–60s, and `Program.cs:16` adds `EnableRetryOnFailure()` so transient
faults after that are retried rather than surfaced.

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

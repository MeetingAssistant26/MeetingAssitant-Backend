# Research: Infrastructure & Foundation

**Feature**: `000-infra-foundation` | **Date**: 2026-03-07

## Topic 1: Docker Compose Health Checks for PostgreSQL+pgvector, Redis, MinIO

### 1.1 Health Check Strategy Per Service

- **Decision**: Use native CLI health check commands for each service — `pg_isready` for PostgreSQL, `redis-cli ping` for Redis, `mc ready` (or `curl` against the MinIO health endpoint) for MinIO. Each service defines its own `healthcheck` block in `docker-compose.yml` with appropriate intervals, timeouts, and retry counts.
- **Rationale**: Native CLI checks are the most reliable because they exercise the actual service protocol rather than just checking if a port is open. `pg_isready` validates the PostgreSQL wire protocol, `redis-cli ping` validates Redis command processing, and MinIO's `/minio/health/live` endpoint confirms the object store is serving. TCP-only checks (e.g., `nc -z`) would pass even if the service process is mid-initialization.
- **Alternatives considered**: TCP socket checks via `netcat` (rejected — false positives during initialization), custom scripts (rejected — unnecessary complexity when native tools are available), application-level checks from the backend (rejected — the backend shouldn't start until dependencies are confirmed healthy by Docker itself).

### 1.2 pgvector Extension on First Boot

- **Decision**: Mount an initialization SQL script into PostgreSQL's `/docker-entrypoint-initdb.d/` directory. The script runs `CREATE EXTENSION IF NOT EXISTS vector;` on the application database. Use the official `pgvector/pgvector:pg17` image (or the `ankane/pgvector` image) which ships with the extension pre-installed as a shared library.
- **Rationale**: PostgreSQL's entrypoint convention (`/docker-entrypoint-initdb.d/`) guarantees the script runs exactly once on first boot (when the data directory is empty). Using `IF NOT EXISTS` makes it idempotent — safe across restarts. The pgvector-specific Docker image avoids the need to install the extension from source at build time. This approach is also compatible with EF Core migrations that reference vector columns — the extension must exist before migrations run.
- **Alternatives considered**: Running `CREATE EXTENSION` from an EF Core migration (rejected — migration runs too late if the backend depends on health checks that test vector support, and it couples infrastructure provisioning to application code), building a custom PostgreSQL Dockerfile that compiles pgvector (rejected — unnecessary when official images exist), running the extension creation from the backend startup code (rejected — requires elevated database privileges at runtime).

### 1.3 Startup Ordering and Backend Dependency Waiting

- **Decision**: Use Docker Compose `depends_on` with the `condition: service_healthy` form for all three infrastructure services. The backend API service depends on PostgreSQL, Redis, and MinIO each being `service_healthy` before Docker starts the backend container. Within the backend, the ASP.NET Core health check endpoint (`/healthz`) performs its own live validation of database connectivity, Redis ping, and Hangfire readiness.
- **Rationale**: `depends_on` with `condition: service_healthy` is the native Docker Compose v2+ mechanism for ordered startup. It replaces fragile workarounds like `wait-for-it.sh` or `dockerize`. The backend still performs its own health checks because Docker health checks only gate container start — they don't prevent transient disconnections during the container's lifetime. The dual-layer approach (Docker health checks for startup ordering, ASP.NET health checks for runtime monitoring) covers both concerns.
- **Alternatives considered**: `wait-for-it.sh` / `dockerize` entrypoint wrappers (rejected — redundant with `condition: service_healthy` in Compose v2+, adds shell script maintenance burden), application-level retry loops on startup (rejected — delays container readiness signal and complicates startup diagnostics), no ordering (rejected — the backend would crash-loop while PostgreSQL initializes).

### 1.4 Specific Health Check Commands

- **Decision**: Use the following health check configurations:

  **PostgreSQL (pgvector)**:
  - Command: `pg_isready -U <db_user> -d <db_name>`
  - Interval: 5s, Timeout: 5s, Retries: 5, Start period: 10s
  - Validates: PostgreSQL is accepting connections on the correct database with the correct user

  **Redis**:
  - Command: `redis-cli ping` (expects `PONG` response)
  - Interval: 5s, Timeout: 3s, Retries: 3, Start period: 5s
  - Validates: Redis is processing commands (not just listening on port)

  **MinIO**:
  - Command: `mc ready local` (using the MinIO Client built into the MinIO image) or `curl -f http://localhost:9000/minio/health/live`
  - Interval: 10s, Timeout: 5s, Retries: 5, Start period: 10s
  - Validates: MinIO's API is serving and the storage backend is operational

- **Rationale**: The start periods account for each service's typical cold-start duration (PostgreSQL needs ~5-10s for WAL recovery on first boot, MinIO needs ~5s for bucket initialization). Intervals and retries are tuned so a failing service is detected within 15-25 seconds while avoiding excessive CPU usage from polling. These values align with the spec requirement that all services report healthy within 60 seconds.
- **Alternatives considered**: Shorter intervals (rejected — creates excessive container runtime overhead), longer start periods (rejected — delays developer feedback loop), `wget` instead of `curl` for MinIO (rejected — `curl` is more universally available in container images and produces cleaner exit codes).

---

## Topic 2: EF Core Global Query Filters for Multi-Tenant OrganizationId

### 2.1 Global Query Filter Implementation

- **Decision**: Define a `HasQueryFilter` call in `OnModelCreating` for every entity type that implements `IHasOrganizationId`. Use EF Core's model-building loop to discover all such entities via reflection and apply `.HasQueryFilter(e => e.OrganizationId == _currentTenantId)` where `_currentTenantId` is a field on `AppDbContext` resolved from a scoped service. The filter expression must reference a field/property on the `DbContext` instance (not a captured variable) so EF Core parameterizes it correctly per-request.
- **Rationale**: EF Core's query filter mechanism is designed for exactly this use case — it injects a `WHERE` clause into every query without developer action. Referencing a `DbContext` field (rather than a closed-over variable) ensures EF Core treats it as a parameterized query, enabling query plan caching. The reflection-based discovery in `OnModelCreating` eliminates the risk of forgetting to register a filter when adding new entities — any entity implementing `IHasOrganizationId` is automatically filtered.
- **Alternatives considered**: Manual filter registration per entity (rejected — error-prone as entity count grows, violates DRY), interceptor-based approach using `IQueryExpressionInterceptor` (rejected — more complex, harder to reason about, not the idiomatic EF Core pattern), repository pattern with built-in filtering (rejected — constitution explicitly prohibits repository/UoW patterns over EF Core).

### 2.2 Tenant ID Resolution

- **Decision**: Create a scoped `ITenantProvider` service with a single `Guid? CurrentOrganizationId` property. For HTTP requests, implement an `ITenantProvider` that extracts the `OrganizationId` from the authenticated user's JWT claims (the `org` claim) or from the route (e.g., `/api/organizations/{orgId}/...`). `AppDbContext` receives `ITenantProvider` via constructor injection and reads `CurrentOrganizationId` into a backing field used by query filters.
- **Rationale**: A dedicated `ITenantProvider` interface decouples tenant resolution from `HttpContext`, making it testable and reusable in non-HTTP contexts (Hangfire jobs, domain event handlers). The scoped lifetime aligns with EF Core's scoped `DbContext` — each request gets its own tenant resolution. Reading from JWT claims is the primary path since the authenticated user's organization is the most authoritative source. Route-based fallback handles endpoints where the org is specified in the URL.
- **Alternatives considered**: Reading directly from `IHttpContextAccessor` inside `AppDbContext` (rejected — tightly couples the DbContext to HTTP, breaks in Hangfire/background contexts), ambient `AsyncLocal<T>` (rejected — error-prone lifetime management, invisible data flow), passing `OrganizationId` as a parameter to every query method (rejected — defeats the purpose of global filters and requires every developer to remember).

### 2.3 Non-Tenant-Scoped Entities

- **Decision**: Entities that are not organization-scoped (e.g., `ApplicationUser`, `RefreshToken`) simply do NOT implement `IHasOrganizationId`. The reflection-based filter registration in `OnModelCreating` skips them entirely. When `ITenantProvider.CurrentOrganizationId` is `null` (e.g., during login before org context is established), queries against tenant-scoped entities return no results — this is the safe default.
- **Rationale**: The interface-based opt-in pattern makes tenant scoping explicit at the entity level. Entities that exist before or outside organizational context (users, auth tokens) naturally bypass the filter. The `null` tenant returning empty results is a security-safe default — it prevents accidental data exposure when tenant context is missing, rather than silently returning all data. Specific queries that intentionally need cross-tenant access (e.g., admin operations) can use `IgnoreQueryFilters()`.
- **Alternatives considered**: A separate `DbContext` for non-tenant entities (rejected — adds complexity, breaks cross-entity queries and transactions), a boolean flag on a base class (rejected — less explicit than interface, harder to discover via reflection), disabling filters when tenant is null (rejected — security risk, could leak data if tenant resolution fails silently).

### 2.4 SaveChangesAsync Override for Audit Timestamps

- **Decision**: Override `SaveChangesAsync` in `AppDbContext`. Iterate over `ChangeTracker.Entries()` for entities inheriting from `BaseEntity`. For entries with state `Added`, set `CreatedAtUtc = DateTime.UtcNow` and `UpdatedAtUtc = DateTime.UtcNow`. For entries with state `Modified`, set `UpdatedAtUtc = DateTime.UtcNow`. Additionally, for `Added` entries implementing `IHasOrganizationId`, auto-set `OrganizationId` from `ITenantProvider.CurrentOrganizationId` if it is not already set — this acts as a safety net against accidentally saving an entity without a tenant.
- **Rationale**: Centralizing timestamp logic in `SaveChangesAsync` guarantees consistency across all features without requiring developers to remember to set these fields. `DateTime.UtcNow` (not `DateTimeOffset`) aligns with the constitution's UTC-only timestamp requirement. Auto-setting `OrganizationId` on insert provides defense-in-depth — even if a developer forgets, the entity gets the correct tenant. The override pattern is the standard EF Core approach and works with all entity types transactionally.
- **Alternatives considered**: EF Core interceptors (`ISaveChangesInterceptor`) (rejected — slightly more indirection with no benefit for a single-context application), value generators (rejected — can't access scoped services like `ITenantProvider`), requiring manual timestamp setting (rejected — inconsistent, error-prone).

### 2.5 Tenant-Scoped Interface Pattern

- **Decision**: Define `IHasOrganizationId` as a simple marker interface with a single property: `Guid OrganizationId { get; set; }`. All tenant-scoped entities implement this interface. `BaseEntity` does NOT include `OrganizationId` — it only contains `Id`, `CreatedAtUtc`, and `UpdatedAtUtc`. This keeps the base class clean for non-tenant entities like `ApplicationUser`.
- **Rationale**: Separating the tenant property into an interface follows the Interface Segregation Principle — `ApplicationUser` inherits `BaseEntity` for ID and timestamps without being forced into tenant scoping. The interface gives `OnModelCreating` and `SaveChangesAsync` a compile-time contract to discover and act on tenant-scoped entities. It also enables generic constraints in shared infrastructure code (e.g., `where T : BaseEntity, IHasOrganizationId`).
- **Alternatives considered**: Putting `OrganizationId` on `BaseEntity` with a nullable `Guid?` (rejected — every query would need to handle null, non-tenant entities would carry a meaningless column), a separate `TenantBaseEntity` subclass (rejected — C# single inheritance makes this rigid, can't combine with other base classes like `IdentityUser`), attribute-based marking (rejected — runtime reflection for attributes is slower and less discoverable than interface checks).

---

## Topic 3: Hangfire Custom Retry Policy with Entity Status Marking

### 3.1 Custom Filter for Exponential Backoff

- **Decision**: Implement a custom Hangfire attribute that combines `IElectStateFilter` and `IServerFilter`. The `IElectStateFilter` implementation intercepts the `FailedState` transition, checks the current retry count against the maximum (3), and either re-enqueues with exponential delay or allows the job to move to the `FailedState`. The delay formula is `TimeSpan.FromSeconds(Math.Pow(2, retryCount) * 15)` — yielding delays of 15s, 30s, 60s for retries 1-3. Apply this attribute as the default filter via `GlobalJobFilters` and disable Hangfire's built-in `AutomaticRetryAttribute` by setting `Attempts = 0`.
- **Rationale**: `IElectStateFilter` is the correct Hangfire extension point for intercepting state transitions — it fires when the job runtime decides the job should move to `FailedState`, giving the filter the opportunity to redirect to `ScheduledState` instead (for retry) or let it proceed to `FailedState` (when retries are exhausted). Disabling the built-in `AutomaticRetryAttribute` prevents double-retry behavior. Exponential backoff with a 15-second base gives transient issues (database locks, API rate limits, network blips) enough time to resolve without overwhelming the system.
- **Alternatives considered**: Using the built-in `AutomaticRetryAttribute` with `DelaysInSeconds` array (rejected — doesn't support the entity status marking requirement; the built-in filter only logs, it doesn't trigger custom domain logic), `IServerFilter` only (rejected — `OnPerformed` fires on every execution including successes, not just failures; `IElectStateFilter` is more precise for failure interception), Polly retry wrapping the job body (rejected — Hangfire would see the job as succeeded even if all Polly retries failed, breaking the dashboard and monitoring).

### 3.2 Automatic Entity Status Marking on Exhaustion

- **Decision**: When the `IElectStateFilter` determines retries are exhausted (retry count >= max retries), resolve a scoped `IServiceProvider` from `ElectStateContext.ServiceProvider` (available in Hangfire's activation context), retrieve `AppDbContext`, load the entity by ID and type, set `Status = Failed`, and save. The entity ID and entity type are passed as job parameters (see 3.3). A `try-catch` wraps the status-marking logic to prevent the filter itself from throwing and masking the original failure.
- **Rationale**: Performing the status update inside the filter ensures it happens exactly when retries are exhausted — not before (premature failure) and not after (missed update). Using `IServiceProvider` to resolve `AppDbContext` keeps the filter stateless and compatible with Hangfire's singleton filter lifecycle. The `try-catch` guard is critical because an exception in a Hangfire filter can cause the job to enter an unrecoverable state, hiding the original error.
- **Alternatives considered**: A continuation job that runs after failure (rejected — Hangfire continuations on failed jobs are unreliable and add cascading failure risk), an `IApplyStateFilter` that watches for `FailedState` (rejected — fires on every state change including intermediate retries, not just exhaustion), polling for stuck jobs via a scheduled sweeper (rejected — delays failure detection, adds another moving part).

### 3.3 Passing Entity Context to the Filter

- **Decision**: Define a custom job parameter convention — jobs that need entity status tracking include `EntityId` (Guid) and `EntityType` (string, the CLR type name) as explicit method parameters on the job method. The filter reads these from `ElectStateContext.BackgroundJob.Job.Args` by matching parameter names via reflection on the job method's `ParameterInfo`. If the parameters are not present (for jobs that don't track entity status), the filter skips the status-marking logic.
- **Rationale**: Using explicit method parameters is the simplest and most debuggable approach — the values are visible in the Hangfire dashboard, serialized with the job payload, and discoverable via standard reflection. The "skip if not present" behavior makes the filter safe to apply globally without breaking jobs that don't have entity status tracking. This avoids the need for a custom attribute per job or a separate registration mechanism.
- **Alternatives considered**: Hangfire job parameters via `PerformContext.SetJobParameter` (rejected — requires the job body to set them before failure, which doesn't work if the failure is in job setup), a dictionary argument (rejected — less discoverable, loses strong typing in the dashboard), a marker interface on the job class (rejected — Hangfire uses static methods by convention, not job classes with interfaces).

### 3.4 Hangfire.PostgreSql Configuration

- **Decision**: Use the `Hangfire.PostgreSql` package with connection string from configuration (same PostgreSQL instance as the application database, but Hangfire uses its own schema — `hangfire` — to avoid table conflicts). Configure with `PrepareSchemaIfNecessary = true` for automatic schema creation on startup. Set `QueuePollInterval = TimeSpan.FromSeconds(5)` for reasonable job pickup latency. Use the `InvisibilityTimeout` default (30 minutes) which defines how long a job remains invisible to other workers during processing.
- **Rationale**: Sharing the PostgreSQL instance simplifies infrastructure (one database to manage in Docker) while the separate schema prevents any naming or migration conflicts with the application's EF Core schema. `PrepareSchemaIfNecessary` eliminates a manual migration step — Hangfire creates its own tables on first run. The 5-second poll interval balances job pickup latency against database load (shorter intervals increase query frequency). All of this aligns with the spec's requirement that Hangfire is operational and the dashboard is accessible after boot.
- **Alternatives considered**: Separate PostgreSQL instance for Hangfire (rejected — unnecessary infrastructure overhead for a dev/small-scale deployment), Redis-backed Hangfire via `Hangfire.Redis.StackExchange` (rejected — adds Redis as a critical persistence dependency when it's only intended for caching in this architecture), `SchemaName` on the application's default schema (rejected — risks collision with EF Core managed tables).

---

## Topic 4: Correlation ID Middleware with Serilog Enrichment

### 4.1 ASP.NET Core Middleware Design

- **Decision**: Implement a custom ASP.NET Core middleware (`CorrelationIdMiddleware`) registered early in the pipeline (before routing, authentication, and other middleware). On each request, the middleware checks for an `X-Correlation-Id` header — if present and non-empty, it uses that value; if absent, it generates a new `Guid` (via `Guid.NewGuid().ToString()`). The resolved correlation ID is stored in `HttpContext.Items["CorrelationId"]` for immediate access and set on a scoped `ICorrelationIdProvider` service for DI-based access.
- **Rationale**: Accepting a client-provided correlation ID enables end-to-end tracing across distributed systems (e.g., a frontend or API gateway that starts the correlation chain). Generating a new one when absent ensures every request is traceable even from clients that don't provide one. `HttpContext.Items` is the fastest access path for middleware downstream in the HTTP pipeline. The middleware must be registered before authentication so that even auth failures are logged with a correlation ID.
- **Alternatives considered**: Using `Activity.Current.TraceId` from OpenTelemetry/DiagnosticSource (rejected — the project uses Serilog for structured logging, not OpenTelemetry; introducing `Activity` adds complexity without benefit unless full distributed tracing is planned), `IHttpContextAccessor` only (rejected — doesn't work in non-HTTP contexts like Hangfire), header-only approach without DI service (rejected — non-HTTP contexts can't access headers).

### 4.2 Serilog LogContext Enrichment

- **Decision**: Within the middleware, after resolving the correlation ID, push it into Serilog's `LogContext` using `LogContext.PushProperty("CorrelationId", correlationId)`. This must wrap the `await _next(context)` call in a `using` block so the property is present for all log entries within that request's scope. The Serilog configuration must include `.Enrich.FromLogContext()` in the logger setup and the output template (or structured sink) must include the `CorrelationId` property.
- **Rationale**: `LogContext.PushProperty` is Serilog's native mechanism for scoped enrichment — it uses `AsyncLocal<T>` internally, so the property flows correctly across async/await boundaries within the request. The `using` block ensures the property is cleaned up after the request completes, preventing leakage to subsequent requests on the same thread. This is the standard Serilog pattern endorsed by the library's documentation.
- **Alternatives considered**: A custom Serilog `ILogEventEnricher` that reads from `IHttpContextAccessor` (rejected — couples the enricher to HTTP context, doesn't work in Hangfire), global static property (rejected — no request isolation, thread-unsafe), middleware that sets a property on every `ILogger` call (rejected — impossible to inject into third-party code logging).

### 4.3 Response Header Propagation

- **Decision**: After calling `await _next(context)`, the middleware adds the correlation ID to the response headers as `X-Correlation-Id`. Use `context.Response.OnStarting()` callback to set the header before the response is flushed — this guarantees the header is present even if downstream middleware or the endpoint modifies the response.
- **Rationale**: `Response.OnStarting()` is the correct ASP.NET Core hook for adding response headers — it fires just before headers are written to the wire, after all middleware has executed. Setting the header after `_next()` (without `OnStarting`) risks missing cases where the response has already started streaming. Returning the correlation ID in the response allows clients to log it on their side for end-to-end correlation.
- **Alternatives considered**: Setting the header before `_next()` (rejected — the response object may not be ready, and middleware downstream could clear headers), a response filter/action filter (rejected — only works for MVC/Minimal API endpoints, not for middleware-level responses like 401s from auth), not returning it (rejected — breaks client-side correlation, spec requires it in response headers).

### 4.4 DI-Based Access for Non-HTTP Contexts (Hangfire Jobs)

- **Decision**: Define a scoped `ICorrelationIdProvider` service with `string CorrelationId { get; set; }`. In HTTP contexts, the middleware sets this from the resolved header value. For Hangfire jobs, implement a custom `IServerFilter` that generates a new correlation ID at job start (`OnPerforming`), sets it on the `ICorrelationIdProvider` resolved from the job's `ActivatedJob.Scope`, and pushes it into Serilog's `LogContext`. The Hangfire filter also stores the correlation ID as a Hangfire job parameter so it's visible in the dashboard.
- **Rationale**: The `ICorrelationIdProvider` abstraction decouples correlation ID access from `HttpContext`, making it injectable into any service regardless of host context. Hangfire creates a new DI scope per job execution, so the scoped `ICorrelationIdProvider` is naturally isolated per job. Generating a new correlation ID per job (rather than inheriting from the enqueueing request) is the correct behavior because Hangfire jobs execute asynchronously and potentially much later — the original request's correlation context is no longer meaningful. Pushing into `LogContext` inside the Hangfire filter ensures all log entries within the job carry the correlation ID.
- **Alternatives considered**: Passing the original request's correlation ID as a job parameter and reusing it (rejected — misleading, the job executes in a different temporal context; a new ID is more honest for debugging), `AsyncLocal<T>` without DI (rejected — Hangfire's thread pool management can break `AsyncLocal` if not handled carefully; the DI scope is the reliable boundary), no correlation ID for background jobs (rejected — makes debugging Hangfire failures impossible, spec requires correlation IDs in all log entries).

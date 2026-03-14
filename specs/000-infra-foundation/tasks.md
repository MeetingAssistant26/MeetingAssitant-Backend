# Tasks: Infrastructure & Foundation 

**Input**: Design documents from `/specs/000-infra-foundation/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Not explicitly requested in feature specification. Test tasks are omitted.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Single project**: `src/`, `tests/` at repository root
- Docker infrastructure at repository root (`docker-compose.yml`, `docker/`)

---

## Phase 1: Setup (Shared Infrastructure) **Implementation Done**

**Purpose**: .NET solution scaffold, NuGet packages, project structure

- [ ] T001 Create .NET 10 web API project with `src/Program.cs`, `src/appsettings.json`, and `src/appsettings.Development.json`
- [ ] T002 Install all required NuGet packages: `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Hangfire.PostgreSql`, `StackExchange.Redis`, `FluentValidation.DependencyInjectionExtensions`, `MediatR`, `Mapster`, `Serilog.AspNetCore`, `Microsoft.Extensions.Http.Polly`, `AspNetCore.HealthChecks.NpgSql`, `AspNetCore.HealthChecks.Redis`, LiveKit Server SDK (.NET)
- [ ] T003 [P] Create directory structure: `src/Features/`, `src/Infrastructure/Data/`, `src/Infrastructure/Middleware/`, `src/Infrastructure/Hangfire/`, `src/Infrastructure/SignalR/`, `src/Infrastructure/AI/`, `src/Infrastructure/AI/DTOs/`, `src/Infrastructure/Services/`, `src/Infrastructure/Configuration/`, `src/Shared/`

---

## Phase 2: Foundational (Blocking Prerequisites) **Implementation Done**

**Purpose**: Shared base types, configuration classes, and core DI registrations that ALL user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [ ] T004 [P] Create `BaseEntity` abstract class with `Id` (Guid), `CreatedAtUtc`, `UpdatedAtUtc` in `src/Shared/BaseEntity.cs`
- [ ] T005 [P] Create `IHasOrganizationId` marker interface with `OrganizationId` (Guid) property in `src/Shared/IHasOrganizationId.cs`
- [ ] T006 [P] Create `EntityStatus` enum (Pending=0, Processing=1, Completed=2, Failed=3) in `src/Shared/EntityStatus.cs`
- [ ] T007 [P] Create `StandardErrorResponse` DTO with `Type`, `Title`, `Status`, `Errors` (Dictionary<string, string[]>), `CorrelationId` in `src/Shared/StandardErrorResponse.cs`
- [ ] T007.1 [P] Create `ResultExtensions` static class in `src/Shared/ResultExtensions.cs` with `ToProblem(this Result result, ICorrelationIdProvider correlationIdProvider)` extension method: throw `InvalidOperationException` if `result.IsSuccess`, map `Result.Error` fields to `StandardErrorResponse` (`Code` → `Type`, `Description` → `Title`, `StatusCode` → `Status`, `correlationIdProvider.CorrelationId` → `CorrelationId`), return `ObjectResult` with `StatusCode` set from `result.Error.StatusCode`. This is the standard endpoint error response pattern — endpoints call `result.ToProblem(correlationIdProvider)` instead of manually constructing `StandardErrorResponse`.
- [ ] T008 [P] Create `IDomainEvent` marker interface extending MediatR `INotification` in `src/Shared/IDomainEvent.cs`
- [ ] T009 [P] Create `JwtSettings` configuration POCO in `src/Infrastructure/Configuration/JwtSettings.cs`
- [ ] T010 [P] Create `RedisSettings` configuration POCO in `src/Infrastructure/Configuration/RedisSettings.cs`
- [ ] T011 [P] Create `AiSettings` configuration POCO in `src/Infrastructure/Configuration/AiSettings.cs`
- [ ] T012 [P] Create `LiveKitSettings` configuration POCO in `src/Infrastructure/Configuration/LiveKitSettings.cs`
- [ ] T013 [P] Create `ITenantProvider` interface with `CurrentOrganizationId` (Guid?) property in `src/Infrastructure/Services/ITenantProvider.cs`
- [ ] T014 [P] Create `ICorrelationIdProvider` interface with `CorrelationId` (string, get/set) property in `src/Infrastructure/Services/ICorrelationIdProvider.cs`
- [ ] T015 [P] Create `TenantProvider` scoped implementation resolving from JWT `org` claim via `IHttpContextAccessor` in `src/Infrastructure/Services/TenantProvider.cs`
- [ ] T016 [P] Create `CorrelationIdProvider` scoped implementation in `src/Infrastructure/Services/CorrelationIdProvider.cs`
- [ ] T017 Register all foundational DI services in `src/Program.cs`: configuration bindings (`JwtSettings`, `RedisSettings`, `AiSettings`, `LiveKitSettings`), scoped `ITenantProvider`/`TenantProvider`, scoped `ICorrelationIdProvider`/`CorrelationIdProvider`, MediatR assembly scan, FluentValidation auto-discovery, Mapster configuration bootstrap
- [ ] T017.1 [P] Scaffold Partial Controller Pattern base conventions: create a conventions reference or example showing controller definition file structure (`[ApiController]`, `[Route]`, ctor, dependencies) and endpoint file structure (single action in partial class, no attributes/ctor/fields). Ensure namespace convention `MeetingAssistant.Features.{FeatureName}.Endpoints.{Group}` is documented for downstream features.

**Checkpoint**: Foundation ready — all shared types and DI registrations in place. User story implementation can now begin.

---

## Phase 3: User Story 1 — Developer Bootstraps Local Environment (Priority: P1) 🎯 MVP

**Goal**: Single `docker compose up` starts PostgreSQL+pgvector, Redis, MinIO, and Backend API with health checks passing within 60 seconds.

**Independent Test**: Run `docker compose up`, wait for health checks, verify each service responds (database query, Redis ping, MinIO endpoint, API `/healthz`).

### Implementation for User Story 1

- [ ] T018 [P] [US1] Create pgvector init script `docker/postgres/init-pgvector.sql` with `CREATE EXTENSION IF NOT EXISTS vector;`
- [ ] T019 [US1] Create `docker-compose.yml` with four services: PostgreSQL (`pgvector/pgvector:pg17`, port 5432, volume, health check `pg_isready -U <user> -d <db>`, interval 5s, retries 5, start_period 10s), Redis (`redis:7-alpine`, port 6379, health check `redis-cli ping`, interval 5s, retries 3, start_period 5s), MinIO (`minio/minio:latest`, ports 9000/9001, health check `mc ready local`, interval 10s, retries 5, start_period 10s), Backend (`depends_on` all three with `condition: service_healthy`)
- [ ] T020 [US1] Create `AppDbContext` with `ITenantProvider` constructor injection, `_currentTenantId` backing field, reflection-based `HasQueryFilter` for `IHasOrganizationId` entities in `OnModelCreating`, and `SaveChangesAsync` override for auto-stamping `CreatedAtUtc`/`UpdatedAtUtc`/`OrganizationId` in `src/Infrastructure/Data/AppDbContext.cs`
- [ ] T021 [US1] Register `AppDbContext` with Npgsql provider and connection string from configuration in `src/Program.cs`
- [ ] T022 [US1] Configure ASP.NET Core health checks for PostgreSQL (`AddNpgSql`), Redis (`AddRedis`), and Hangfire storage; map `GET /healthz` endpoint in `src/Program.cs`
- [ ] T023 [US1] Add initial EF Core migration for empty schema (pgvector extension already handled by Docker init script) via `dotnet ef migrations add InitialCreate`

**Checkpoint**: `docker compose up` brings all services to healthy. `GET /healthz` returns 200 with PostgreSQL, Redis, and Hangfire all healthy.

---

## Phase 4: User Story 2 — Backend API Boots with All Middleware (Priority: P1)

**Goal**: Every HTTP request gets a correlation ID in response headers and logs; unhandled exceptions return `StandardErrorResponse`; Hangfire dashboard is accessible; Serilog structured logging is active.

**Independent Test**: Send any HTTP request → verify `X-Correlation-Id` in response header + structured logs. Trigger an exception → verify `StandardErrorResponse` returned. Navigate to Hangfire dashboard → verify accessible.

### Implementation for User Story 2

- [ ] T024 [P] [US2] Implement `CorrelationIdMiddleware` in `src/Infrastructure/Middleware/CorrelationIdMiddleware.cs`: read `X-Correlation-Id` header (use if present, generate GUID if absent), set `ICorrelationIdProvider`, push into `Serilog.LogContext.PushProperty("CorrelationId", ...)` with `using` block wrapping `_next(context)`, add response header via `Response.OnStarting()`
- [ ] T025 [P] [US2] Implement `ExceptionHandlingMiddleware` in `src/Infrastructure/Middleware/ExceptionHandlingMiddleware.cs`: wrap `_next(context)` in try-catch, catch all unhandled exceptions, resolve `ICorrelationIdProvider` for correlation ID, return serialized `StandardErrorResponse` with `application/json` content type, log exception with correlation ID, never expose internal details
- [ ] T026 [US2] Configure Serilog in `src/Program.cs`: `UseSerilog()` with `.Enrich.FromLogContext()`, structured output template including `{CorrelationId}`, console sink for development, configure minimum log levels
- [ ] T027 [US2] Register middleware pipeline in correct order in `src/Program.cs`: `CorrelationIdMiddleware` → `ExceptionHandlingMiddleware` → `UseAuthentication()` → `UseAuthorization()` → routing → SignalR → Hangfire → health checks (per contracts/api.md pipeline order)
- [ ] T028 [US2] Configure Hangfire with PostgreSQL storage (`UsePostgreSqlStorage` on `hangfire` schema, `PrepareSchemaIfNecessary = true`, `QueuePollInterval = 5s`), map Hangfire dashboard endpoint, disable built-in `AutomaticRetryAttribute` (`Attempts = 0`) in `src/Program.cs`

**Checkpoint**: All requests return `X-Correlation-Id` header. Errors return `StandardErrorResponse`. Hangfire dashboard is accessible. Serilog structured logs include `CorrelationId`.

---

## Phase 5: User Story 3 — Database Context Enforces Tenant Isolation (Priority: P1)

**Goal**: Any entity implementing `IHasOrganizationId` is automatically filtered by `OrganizationId` at query time; `BaseEntity` timestamps auto-populate; `OrganizationId` auto-set on insert.

**Independent Test**: Seed data for two organizations, query via context with one tenant set — only that tenant's data is returned. Save a `BaseEntity` — timestamps are auto-populated in UTC.

### Implementation for User Story 3

> Note: `AppDbContext` was created in T020 with basic structure. This phase completes and validates the tenant isolation behavior.

- [ ] T029 [US3] Verify `AppDbContext.OnModelCreating` reflection loop: scan all registered entity types, find those implementing `IHasOrganizationId`, call `HasQueryFilter` with expression referencing `_currentTenantId` field — ensure EF Core parameterizes correctly in `src/Infrastructure/Data/AppDbContext.cs`
- [ ] T030 [US3] Verify `AppDbContext.SaveChangesAsync` override: iterate `ChangeTracker.Entries<BaseEntity>()` — `Added` → set `CreatedAtUtc = DateTime.UtcNow`, `UpdatedAtUtc = DateTime.UtcNow`; `Modified` → set `UpdatedAtUtc = DateTime.UtcNow`; for `Added` entries implementing `IHasOrganizationId` → auto-set `OrganizationId` from `ITenantProvider.CurrentOrganizationId` if `Guid.Empty` in `src/Infrastructure/Data/AppDbContext.cs`

**Checkpoint**: Tenant isolation is enforced by default. Timestamps are auto-populated. Cross-tenant queries return no results when `OrganizationId` doesn't match.

---

## Phase 6: User Story 4 — Domain Events Enable Feature Communication (Priority: P2)

**Goal**: MediatR `INotification`-based domain event dispatcher is registered and functional; events are logged with correlation IDs.

**Independent Test**: Publish a test `INotification` via MediatR → registered handler receives it. Verify event is logged with correlation ID.

### Implementation for User Story 4

- [ ] T031 [US4] Verify MediatR registration in `src/Program.cs` scans the assembly for `INotificationHandler<T>` implementations — confirm `AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))` is correctly configured
- [ ] T032 [US4] Create a sample/test domain event (e.g., `InfrastructureReadyEvent : IDomainEvent`) and handler that logs the event with correlation ID in `src/Infrastructure/Events/InfrastructureReadyEvent.cs` — to validate the dispatch pipeline works end-to-end (can be removed after validation or kept as a reference)

**Checkpoint**: Domain events can be published and handled across feature boundaries via MediatR. Events logged with correlation IDs.

---

## Phase 7: User Story 5 — AI Service Abstractions Are Callable (Priority: P2)

**Goal**: `ILLMService` and `IEmbeddingService` are resolvable from DI and callable with typed `HttpClient` and Polly policies. Provider is swappable via configuration.

**Independent Test**: Resolve `ILLMService`/`IEmbeddingService` from DI container, verify they are injectable. Verify `CancellationToken` is accepted. Verify Polly policies are attached to the typed `HttpClient`.

### Implementation for User Story 5

- [ ] T033 [P] [US5] Create `LLMRequest` and `LLMResponse` DTOs following OpenAI `/v1/chat/completions` contract in `src/Infrastructure/AI/DTOs/LLMRequest.cs` and `src/Infrastructure/AI/DTOs/LLMResponse.cs`
- [ ] T034 [P] [US5] Create `EmbeddingRequest` and `EmbeddingResponse` DTOs following OpenAI `/v1/embeddings` contract in `src/Infrastructure/AI/DTOs/EmbeddingRequest.cs` and `src/Infrastructure/AI/DTOs/EmbeddingResponse.cs`
- [ ] T035 [P] [US5] Create `ILLMService` interface with `CompleteAsync(LLMRequest, CancellationToken)` and `CompleteWithJsonAsync<T>(LLMRequest, CancellationToken)` in `src/Infrastructure/AI/ILLMService.cs`
- [ ] T036 [P] [US5] Create `IEmbeddingService` interface with `EmbedAsync(string, CancellationToken)` and `EmbedBatchAsync(IReadOnlyList<string>, CancellationToken)` in `src/Infrastructure/AI/IEmbeddingService.cs`
- [ ] T037 [US5] Implement `OpenAiLLMService` using typed `HttpClient` targeting `AiSettings.BaseUrl + "/v1/chat/completions"` with `Authorization: Bearer {ApiKey}` header in `src/Infrastructure/AI/OpenAiLLMService.cs`
- [ ] T038 [US5] Implement `OpenAiEmbeddingService` using typed `HttpClient` targeting `AiSettings.BaseUrl + "/v1/embeddings"` with model `text-embedding-3-small` (1536 dimensions) in `src/Infrastructure/AI/OpenAiEmbeddingService.cs`
- [ ] T039 [US5] Register AI services in `src/Program.cs`: typed `HttpClient` for `OpenAiLLMService` and `OpenAiEmbeddingService` with Polly policies (retry 3 attempts exponential backoff + circuit-breaker), bind `AiSettings` from configuration, register `ILLMService`/`IEmbeddingService` as scoped

**Checkpoint**: `ILLMService` and `IEmbeddingService` are resolvable from DI. Polly retry + circuit-breaker policies are active. Provider swappable via `AiSettings.BaseUrl`.

---

## Phase 8: User Story 6 — SignalR Hub Supports Tenant-Scoped Messaging (Priority: P2)

**Goal**: SignalR `NotificationHub` authenticates via JWT, resolves org memberships, adds connections to `org:{OrganizationId}` groups. No `Clients.All` usage.

**Independent Test**: Connect two clients from different orgs, send message to one org's group — only the correct client receives it.

### Implementation for User Story 6

- [x] T040 [US6] Implement `NotificationHub` in `src/Infrastructure/SignalR/NotificationHub.cs`: override `OnConnectedAsync` — authenticate user via JWT (already handled by ASP.NET Core auth), read `organizationId` claim from JWT, call `Groups.AddToGroupAsync(Context.ConnectionId, $"org:{organizationId}")` to add the connection to exactly one tenant group
- [x] T041 [US6] Map SignalR hub endpoint (`/hubs/notifications`) in `src/Program.cs` with JWT authentication enabled, register SignalR services (`AddSignalR()`)

**Checkpoint**: SignalR clients are added to tenant-scoped groups on connection. `Clients.All` is never used. Disconnection automatically removes from all groups.

---

## Phase 9: User Story 7 — Secrets Are Never Hardcoded (Priority: P3)

**Goal**: JWT signing keys, AI API keys, LiveKit credentials, Redis connection strings, and MinIO credentials are loaded from user-secrets (dev) or environment variables (Docker). No secrets in committed files.

**Independent Test**: Scan repo for hardcoded secret patterns — none found. Verify user-secrets work locally. Verify Docker env vars inject at runtime.

### Implementation for User Story 7

- [x] T042 [US7] Initialize .NET user-secrets for the project (`dotnet user-secrets init`) and document required secrets in `src/appsettings.Development.json` with placeholder descriptions (no actual values)
- [x] T043 [US7] Configure `docker-compose.yml` environment variables for Backend service: `ConnectionStrings__DefaultConnection`, `Jwt__SigningKey`, `Redis__ConnectionString`, `AI__ApiKey`, `AI__BaseUrl`, `LiveKit__ApiKey`, `LiveKit__ApiSecret` — referencing `.env` file (add `.env` to `.gitignore`)
- [x] T044 [US7] Configure JWT authentication in `src/Program.cs`: `AddAuthentication(JwtBearerDefaults)` → `AddJwtBearer()` with `IssuerSigningKey` loaded from `JwtSettings` (bound from configuration), set `ClockSkew = TimeSpan.Zero`, configure `TokenValidationParameters` (validate issuer, audience, lifetime)
- [x] T045 [US7] Configure Redis connection in `src/Program.cs`: `AddStackExchangeRedisCache()` with connection string from `RedisSettings`, add Redis startup health validation — if Redis unreachable, throw clear exception with message indicating mandatory dependency (fail fast per FR-013)
- [x] T046 [P] [US7] Create `.gitignore` entry for `.env` file and `src/appsettings.*.local.json` to prevent secret leakage
- [x] T047 [P] [US7] Configure LiveKit settings binding from configuration in `src/Program.cs` — bind `LiveKitSettings` POCO from `LiveKit` config section, ready for token generation by downstream features

**Checkpoint**: All secrets loaded from user-secrets or environment variables. No hardcoded secrets in repository. Redis fails fast if unreachable. JWT auth infrastructure ready.

---

## Phase 10: User Story 8 — Hangfire Retry Policy Protects Against Transient Failures (Priority: P3)

**Goal**: Custom `IElectStateFilter` retries failed jobs 3 times with exponential backoff (15s/30s/60s). On exhaustion, entity `Status` is marked `Failed`.

**Independent Test**: Create a test Hangfire job that deliberately fails → verify 3 retries with increasing delays → verify entity status transitions to `Failed`.

### Implementation for User Story 8

- [x] T048 [US8] Implement `HangfireRetryFilter` as custom attribute combining `IElectStateFilter` in `src/Infrastructure/Hangfire/HangfireRetryFilter.cs`: intercept `FailedState` transition, check retry count vs max (3), if retries remaining → redirect to `ScheduledState` with delay `TimeSpan.FromSeconds(Math.Pow(2, retryCount) * 15)`, if exhausted → allow `FailedState` + resolve `AppDbContext` from `ElectStateContext.ServiceProvider` → load entity by `EntityId`/`EntityType` job params → set `Status = Failed` → wrap in try-catch
- [x] T049 [US8] Implement `HangfireCorrelationFilter` as `IServerFilter` in `src/Infrastructure/Hangfire/HangfireCorrelationFilter.cs`: in `OnPerforming` → generate new correlation ID, set on `ICorrelationIdProvider` resolved from job scope, push into `LogContext.PushProperty("CorrelationId", ...)`, store as Hangfire job parameter for dashboard visibility
- [x] T050 [US8] Register `HangfireRetryFilter` and `HangfireCorrelationFilter` in `GlobalJobFilters` in `src/Program.cs`

**Checkpoint**: Failed Hangfire jobs retry 3 times with 15s/30s/60s delays. Entity status marked `Failed` on exhaustion. Correlation IDs present in all job logs and Hangfire dashboard.

---

## Phase 11: Polish & Cross-Cutting Concerns

**Purpose**: Final integration validation and cleanup

- [x] T051 Verify complete middleware pipeline order in `src/Program.cs` matches contracts/api.md: CorrelationIdMiddleware → ExceptionHandlingMiddleware → Authentication → Authorization → Routing (Controller endpoints) → SignalR → Hangfire → Health checks
- [x] T052 [P] Verify all async method signatures in `src/Infrastructure/` and `src/Shared/` include `CancellationToken` parameter (FR-018)
- [x] T052.1 [P] Verify Partial Controller Pattern conventions are scaffolded: Mapster configuration bootstrapped in `Program.cs`, namespace convention documented, controller definition and endpoint file structure conventions established (FR-018.1)
- [x] T053 [P] Verify `src/appsettings.json` contains non-secret configuration structure with placeholder sections for `Jwt`, `Redis`, `AI`, `LiveKit`, `ConnectionStrings` — no actual secret values
- [x] T054 Run `docker compose up` end-to-end validation: all services healthy, `GET /healthz` returns 200, pgvector queryable, Hangfire dashboard accessible, structured logs with correlation IDs visible
- [x] T055 Run quickstart.md validation: follow the 8-step getting started guide from `specs/000-infra-foundation/quickstart.md` and verify each step succeeds

**Checkpoint**: All 20 FRs and 10 SCs are met. Infrastructure is ready for downstream feature development.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **US1 — Docker + Health (Phase 3)**: Depends on Foundational (needs `BaseEntity`, `IHasOrganizationId`, `ITenantProvider` for `AppDbContext`)
- **US2 — Middleware (Phase 4)**: Depends on Foundational (needs `StandardErrorResponse`, `ICorrelationIdProvider`)
- **US3 — Tenant Isolation (Phase 5)**: Depends on US1 (needs `AppDbContext` from T020)
- **US4 — Domain Events (Phase 6)**: Depends on Foundational (needs `IDomainEvent`)
- **US5 — AI Abstractions (Phase 7)**: Depends on Foundational (needs `AiSettings`)
- **US6 — SignalR Hub (Phase 8)**: Depends on US2 (needs JWT auth configured) — reads `organizationId` from JWT claim (no DB query needed)
- **US7 — Secrets (Phase 9)**: Depends on US1 (needs `docker-compose.yml`) + US2 (needs Hangfire configured)
- **US8 — Hangfire Retry (Phase 10)**: Depends on US2 (needs Hangfire PostgreSql storage configured in T028)
- **Polish (Phase 11)**: Depends on all user stories

### User Story Independence

| Story | Can Start After | Independent of |
|-------|----------------|----------------|
| US1 (P1) | Foundational | — |
| US2 (P1) | Foundational | US1 (different files) |
| US3 (P1) | US1 (needs AppDbContext) | US2, US4–US8 |
| US4 (P2) | Foundational | US1–US3, US5–US8 |
| US5 (P2) | Foundational | US1–US4, US6–US8 |
| US6 (P2) | US1 + US2 | US3–US5, US7–US8 |
| US7 (P3) | US1 + US2 | US3–US6, US8 |
| US8 (P3) | US2 | US1, US3–US7 |

### Within Each User Story

- Configuration before implementation
- Interfaces before concrete classes
- Core implementation before DI registration
- Story complete before moving to next priority

### Parallel Opportunities

**After Foundational (Phase 2) completes:**
- US1 and US2 can run in parallel (different files entirely)
- US4 and US5 can run in parallel (different directories)
- Within US5: all DTOs (T033, T034) in parallel, both interfaces (T035, T036) in parallel

**After US1 + US2 complete:**
- US3, US6, US7, US8 can all start (US6/US7/US8 in parallel if staffed)

---

## Parallel Example: User Story 5

```bash
# Launch all DTOs together (different files, no dependencies):
Task T033: "Create LLMRequest/LLMResponse DTOs in src/Infrastructure/AI/DTOs/"
Task T034: "Create EmbeddingRequest/EmbeddingResponse DTOs in src/Infrastructure/AI/DTOs/"

# Launch both interfaces together (different files):
Task T035: "Create ILLMService in src/Infrastructure/AI/ILLMService.cs"
Task T036: "Create IEmbeddingService in src/Infrastructure/AI/IEmbeddingService.cs"

# Then implementations sequentially (depend on DTOs + interfaces):
Task T037: "Implement OpenAiLLMService"
Task T038: "Implement OpenAiEmbeddingService"

# Finally DI registration (depends on all above):
Task T039: "Register AI services in Program.cs"
```

---

## Implementation Strategy

### MVP First (User Stories 1–3 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: US1 — Docker + Health Check
4. Complete Phase 4: US2 — Middleware + Logging
5. Complete Phase 5: US3 — Tenant Isolation
6. **STOP and VALIDATE**: Docker Compose up, health check passing, correlation IDs flowing, tenant isolation enforced
7. Deploy/demo if ready — core infrastructure is operational

### Incremental Delivery

1. Setup + Foundational → Base types and DI ready
2. US1 (Docker) → All services running and healthy (MVP checkpoint 1)
3. US2 (Middleware) → Observability and error handling active (MVP checkpoint 2)
4. US3 (Tenant Isolation) → Security-critical filtering verified (MVP checkpoint 3)
5. US4 (Domain Events) → Cross-feature communication ready
6. US5 (AI Abstractions) → AI pipeline infrastructure ready
7. US6 (SignalR) → Real-time notification infrastructure ready
8. US7 (Secrets) → Security hardening complete
9. US8 (Hangfire Retry) → Background job resilience complete
10. Polish → Final validation

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: US1 (Docker) → US3 (Tenant Isolation) → US7 (Secrets)
   - Developer B: US2 (Middleware) → US8 (Hangfire Retry) → US6 (SignalR)
   - Developer C: US4 (Domain Events) → US5 (AI Abstractions)
3. Stories complete and integrate independently

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- US3 tasks (T029, T030) are verification/completion tasks — `AppDbContext` is initially created in T020 with the core logic, but tenant isolation behavior requires dedicated validation
- No test tasks generated — tests were not explicitly requested in the feature specification

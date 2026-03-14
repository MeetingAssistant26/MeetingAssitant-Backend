# Implementation Plan: Infrastructure & Foundation

**Branch**: `000-infra-foundation` | **Date**: 2026-03-07 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/000-infra-foundation/spec.md`

## Summary

Establish the complete infrastructure foundation for the AI-Powered Meeting Assistant: Docker Compose with PostgreSQL+pgvector, Redis, and MinIO; .NET 10 solution scaffold with feature-based modular monolith structure using the **Partial Controller Pattern** (one endpoint per file via partial classes); `AppDbContext` with reflection-based global multi-tenant query filters (`IHasOrganizationId`) and auto-stamped audit timestamps; correlation ID middleware with Serilog enrichment; global error handling returning `StandardErrorResponse`; `ResultExtensions.ToProblem()` helper for endpoint-level error responses; Hangfire with PostgreSQL storage and custom `IElectStateFilter` for exponential backoff retry (3 retries, entity `Status = Failed` on exhaustion); SignalR tenant-scoped notification hub; `ILLMService`/`IEmbeddingService` AI abstractions with Polly resilience; Redis (mandatory — fail fast); JWT configuration; secrets management via user-secrets/env vars; health check endpoint; FluentValidation + MediatR + Mapster DI registration; Partial Controller Pattern convention scaffolding.

## Technical Context

**Language/Version**: C# / .NET 10  
**Primary Dependencies**: ASP.NET Identity, JWT Bearer, EF Core (Npgsql), Hangfire (PostgreSql), StackExchange.Redis, MediatR, FluentValidation, Mapster, Serilog, Polly, SignalR, LiveKit Server SDK (.NET)  
**Storage**: PostgreSQL 17 with pgvector extension (shared database, Hangfire on `hangfire` schema)  
**Testing**: xUnit, `WebApplicationFactory<T>` for integration tests, Testcontainers (PostgreSQL, Redis)  
**Target Platform**: Linux container (Docker) / local dev on Windows/macOS  
**Project Type**: Web service (ASP.NET Controllers — Partial Controller Pattern, modular monolith)  
**Performance Goals**: API responses < 300ms (excluding background jobs), health check < 2s  
**Constraints**: All AI processing async via Hangfire; UTC-only timestamps; no synchronous AI calls in request pipeline; Redis mandatory  
**Scale/Scope**: Local/demo deployment, single PostgreSQL instance, single backend container

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Rule (from Constitution v1.3.5) | Status | Notes |
|---|------|--------|-------|
| 1 | Single deployable unit (modular monolith) | PASS | Single .NET project, `Program.cs` entry point |
| 2 | Single PostgreSQL database | PASS | One PostgreSQL instance; Hangfire uses separate `hangfire` schema |
| 3 | Event-driven internal design (MediatR `INotification`) | PASS | MediatR registered; domain event dispatcher ready for downstream features |
| 4 | Feature-based modular structure (`Features/`, `Infrastructure/`, `Shared/`, `Program.cs`) | PASS | Project structure follows constitution exactly |
| 5 | Non-negotiable technology stack | PASS | All specified technologies included (see Technical Context) |
| 6 | Multi-tenancy via `OrganizationId` with global EF Core query filters | PASS | `IHasOrganizationId` marker interface + reflection-based `HasQueryFilter` |
| 7 | Cross-feature communication via domain events only | PASS | No direct service calls; MediatR infrastructure in place |
| 8 | Hangfire for all long-running operations, max 3 retries, exponential backoff | PASS | Custom `IElectStateFilter`, 15s/30s/60s delays, entity `Failed` marking |
| 9 | LiveKit Cloud for realtime media (no self-hosted) | PASS | Configuration only; LiveKit SDK registered, endpoint for token generation deferred |
| 10 | SignalR tenant-scoped groups (`org:{OrganizationId}`), no `Clients.All` | PASS | `NotificationHub` authenticates via JWT, resolves org memberships, groups only |
| 11 | JWT access tokens (15-min) + refresh tokens (7-day, hashed) | PASS | JWT infrastructure configured; token logic deferred to Feature 001 |
| 12 | Structured logging mandatory, correlation ID on every request | PASS | Serilog + `CorrelationIdMiddleware` + `LogContext.PushProperty` |
| 13 | No synchronous AI calls in request pipeline | PASS | `ILLMService`/`IEmbeddingService` abstractions with Polly; actual calls via Hangfire |
| 14 | DI mandatory, no static services, no service locator | PASS | All services registered via DI; constructor injection throughout |
| 15 | FluentValidation for input validation | PASS | FluentValidation registered with auto-discovery |
| 16 | All async methods accept `CancellationToken` | PASS | Enforced in all interface contracts (`ILLMService`, `IEmbeddingService`, etc.) |
| 17 | ASP.NET Controllers with Partial Controller Pattern (no Minimal APIs) | PASS | Controller definition files contain `[ApiController]`, `[Route]`, ctor, dependencies; endpoint files contain exactly one action in a partial class. Convention scaffolded in Phase 0. |
| 18 | Mapster for object mapping | PASS | Mapster configuration bootstrapped for downstream feature use |
| 19 | Endpoints use `result.ToProblem(correlationIdProvider)` for error responses (no manual `StandardErrorResponse` construction) | PASS | `ResultExtensions.ToProblem()` in `Shared/ResultExtensions.cs` converts failed `Result` to `ObjectResult` with `StandardErrorResponse` including `CorrelationId` from `ICorrelationIdProvider` |

**Gate result**: PASS (0 violations)

## Project Structure

### Documentation (this feature)

```text
specs/000-infra-foundation/
├── plan.md              # This file
├── spec.md              # Feature specification (8 stories, 20 FRs, 10 SCs)
├── research.md          # Phase 0 research (4 topics, 14 decisions)
├── data-model.md        # Phase 1 data model (BaseEntity, IHasOrganizationId, EntityStatus, StandardErrorResponse, ResultExtensions)
├── quickstart.md        # Phase 1 getting started guide
├── contracts/
│   └── api.md           # Health endpoint, internal service interfaces, middleware contracts
├── checklists/
│   └── requirements.md  # Quality checklist (16/16 pass)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
docker-compose.yml
docker/
└── postgres/
    └── init-pgvector.sql
src/
├── Program.cs
├── appsettings.json
├── appsettings.Development.json
├── Infrastructure/
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   └── Migrations/
│   ├── Middleware/
│   │   ├── CorrelationIdMiddleware.cs
│   │   └── ExceptionHandlingMiddleware.cs
│   ├── Hangfire/
│   │   ├── HangfireRetryFilter.cs
│   │   └── HangfireCorrelationFilter.cs
│   ├── SignalR/
│   │   └── NotificationHub.cs
│   ├── AI/
│   │   ├── ILLMService.cs
│   │   ├── OpenAiLLMService.cs
│   │   ├── IEmbeddingService.cs
│   │   ├── OpenAiEmbeddingService.cs
│   │   └── DTOs/
│   │       ├── LLMRequest.cs
│   │       ├── LLMResponse.cs
│   │       ├── EmbeddingRequest.cs
│   │       └── EmbeddingResponse.cs
│   ├── Services/
│   │   ├── ITenantProvider.cs
│   │   ├── TenantProvider.cs
│   │   ├── ICorrelationIdProvider.cs
│   │   └── CorrelationIdProvider.cs
│   └── Configuration/
│       ├── JwtSettings.cs
│       ├── RedisSettings.cs
│       ├── AiSettings.cs
│       └── LiveKitSettings.cs
├── Shared/
│   ├── BaseEntity.cs
│   ├── IHasOrganizationId.cs
│   ├── EntityStatus.cs
│   ├── StandardErrorResponse.cs
│   ├── ResultExtensions.cs
│   └── IDomainEvent.cs
└── Features/
    └── (empty — populated by downstream features)
        # Convention: Each feature follows Partial Controller Pattern:
        # Features/{FeatureName}/Endpoints/{Group}/{Controller}Controller.cs  (definition)
        # Features/{FeatureName}/Endpoints/{Group}/{Action}Endpoint.cs        (one action per file)
        # Features/{FeatureName}/Models/Requests/  +  Responses/
        # Features/{FeatureName}/Services/
        # Features/{FeatureName}/Validators/
tests/
├── Infrastructure.Tests/
│   ├── CorrelationIdMiddlewareTests.cs
│   ├── ExceptionHandlingMiddlewareTests.cs
│   ├── AppDbContextTenantFilterTests.cs
│   ├── HangfireRetryFilterTests.cs
│   ├── LLMServiceTests.cs
│   └── EmbeddingServiceTests.cs
└── Integration.Tests/
    ├── HealthCheckTests.cs
    └── DockerComposeBootstrapTests.cs
```

**Structure Decision**: Feature-based modular monolith as mandated by Constitution §I. Single `src/` project with `Infrastructure/`, `Shared/`, and `Features/` directories. Infrastructure code lives under `src/Infrastructure/` organized by concern (Data, Middleware, Hangfire, SignalR, AI, Services, Configuration). Shared base types live under `src/Shared/`. Tests mirror the source structure with unit tests for individual components and integration tests for end-to-end scenarios.

## Complexity Tracking

> No violations — Constitution Check passed with 0 violations. No justifications required.

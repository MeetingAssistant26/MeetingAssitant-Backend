# Quickstart: Infrastructure & Foundation

**Feature**: `000-infra-foundation` | **Branch**: `000-infra-foundation`

## Prerequisites

- .NET 10 SDK installed
- Docker Desktop running (Docker Compose V2)
- LiveKit Cloud account with API key and secret
- OpenAI API key (or compatible provider) — optional for dev (mock implementations available)

## Key Decisions (from research)

| Decision | Choice |
|----------|--------|
| Docker health checks | Native CLI per service (`pg_isready`, `redis-cli ping`, `mc ready local`) |
| Container startup ordering | `depends_on: condition: service_healthy` |
| pgvector init | `CREATE EXTENSION IF NOT EXISTS vector;` in `/docker-entrypoint-initdb.d/` |
| Multi-tenant filter | `IHasOrganizationId` marker interface + reflection-based `HasQueryFilter` in `OnModelCreating` |
| Tenant resolution | Scoped `ITenantProvider` resolved from JWT `org` claim |
| Auto-stamping | `SaveChangesAsync` override sets `CreatedAtUtc`/`UpdatedAtUtc` |
| Hangfire retry | `IElectStateFilter` with exponential backoff (15s × 2^attempt, max 3) |
| Entity failure marking | Filter resolves `AppDbContext` via `IServiceProvider`, sets `Status = Failed` |
| Correlation ID | Middleware reads `X-Correlation-Id` (uses if present, generates if absent) |
| Correlation ID logging | `Serilog.LogContext.PushProperty` + `Response.OnStarting()` for header |
| Redis policy | Mandatory — fail fast at startup if unreachable |
| AI abstractions | OpenAI API standard, typed `HttpClient`, Polly retry + circuit-breaker |

## Data Model (summary)

See [data-model.md](data-model.md) for full details:

- **`BaseEntity`** — abstract class: `Id` (Guid), `CreatedAtUtc`, `UpdatedAtUtc` (auto-set in `SaveChangesAsync`)
- **`IHasOrganizationId`** — marker interface: `OrganizationId` (Guid), triggers global query filter
- **`EntityStatus`** — enum: `Pending(0)`, `Processing(1)`, `Completed(2)`, `Failed(3)`
- **`StandardErrorResponse`** — DTO: `Type`, `Title`, `Status`, `Errors`, `CorrelationId`
- **`ResultExtensions`** — static class: `ToProblem(this Result, ICorrelationIdProvider)` extension method converts failed results to `ObjectResult` with `StandardErrorResponse` including `CorrelationId` (standard endpoint error pattern)

## Contracts (summary)

See [contracts/api.md](contracts/api.md) for full details:

| Contract | Type | Purpose |
|----------|------|---------|
| `GET /healthz` | HTTP endpoint | Reports PostgreSQL, Redis, Hangfire status |
| `ITenantProvider` | Interface | Exposes `CurrentOrganizationId` from JWT or job context |
| `ICorrelationIdProvider` | Interface | Exposes current correlation ID for logging/Hangfire |
| `ILLMService` | Interface | Provider-agnostic text completion (OpenAI standard) |
| `IEmbeddingService` | Interface | Provider-agnostic vector embeddings (OpenAI standard) |
| `StandardErrorResponse` | DTO | Unified error format for all API errors |
| `/hubs/notifications` | SignalR hub | Tenant-scoped real-time messaging via `org:{id}` groups |

## Project Structure

```text
docker-compose.yml
docker/
├── postgres/
│   └── init-pgvector.sql          # CREATE EXTENSION IF NOT EXISTS vector
src/
├── Program.cs                      # Middleware pipeline, DI registration
├── appsettings.json                # Non-secret configuration
├── appsettings.Development.json    # Dev overrides (logging, etc.)
├── Infrastructure/
│   ├── Data/
│   │   ├── AppDbContext.cs         # EF Core context, global query filters, SaveChangesAsync override
│   │   └── Migrations/            # EF Core migrations
│   ├── Middleware/
│   │   ├── CorrelationIdMiddleware.cs
│   │   └── ExceptionHandlingMiddleware.cs
│   ├── Hangfire/
│   │   ├── HangfireRetryFilter.cs  # IElectStateFilter — exponential backoff
│   │   └── HangfireCorrelationFilter.cs  # IServerFilter — correlation ID
│   ├── SignalR/
│   │   └── NotificationHub.cs      # Tenant-scoped hub
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
│   └── IDomainEvent.cs             # marker for MediatR INotification
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

## Getting Started

1. **Create the Docker Compose file** with four services:
   - `postgres`: `pgvector/pgvector:pg17`, port `5432`, volume for data, health check via `pg_isready`
   - `redis`: `redis:7-alpine`, port `6379`, health check via `redis-cli ping`
   - `minio`: `minio/minio:latest`, port `9000`/`9001`, health check via `mc ready local`
   - `backend`: .NET 10 app, `depends_on` all three with `condition: service_healthy`

2. **Initialize the pgvector extension** by mounting `init-pgvector.sql` into `/docker-entrypoint-initdb.d/`:
   ```sql
   CREATE EXTENSION IF NOT EXISTS vector;
   ```

3. **Scaffold the .NET solution** with feature-based structure:
   ```bash
   dotnet new webapi -n Backend
   # Create folder structure: Infrastructure/, Shared/, Features/
   ```

4. **Install NuGet packages**:
   ```bash
   dotnet add package Microsoft.AspNetCore.Identity.EntityFrameworkCore
   dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
   dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
   dotnet add package Hangfire.PostgreSql
   dotnet add package StackExchange.Redis
   dotnet add package FluentValidation.DependencyInjectionExtensions
   dotnet add package MediatR
   dotnet add package Mapster
   dotnet add package Serilog.AspNetCore
   dotnet add package Microsoft.Extensions.Http.Polly
   ```

5. **Configure `AppDbContext`**:
   - Override `OnModelCreating` → scan for `IHasOrganizationId` via reflection → apply `HasQueryFilter`
   - Override `SaveChangesAsync` → auto-set `CreatedAtUtc` / `UpdatedAtUtc` / `OrganizationId`
   - Register as scoped service with Npgsql provider

6. **Register middleware** in `Program.cs` (order matters):
   ```csharp
   app.UseMiddleware<CorrelationIdMiddleware>();
   app.UseMiddleware<ExceptionHandlingMiddleware>();
   app.UseAuthentication();
   app.UseAuthorization();
   // ... endpoints, SignalR, Hangfire, health checks
   ```

7. **Configure secrets** (development):
   ```bash
   dotnet user-secrets init
   dotnet user-secrets set "Jwt:SigningKey" "<your-key>"
   dotnet user-secrets set "Redis:ConnectionString" "localhost:6379"
   dotnet user-secrets set "AI:ApiKey" "<your-openai-key>"
   dotnet user-secrets set "LiveKit:ApiKey" "<your-key>"
   dotnet user-secrets set "LiveKit:ApiSecret" "<your-secret>"
   ```

8. **Start local environment**:
   ```bash
   docker compose up -d
   # Wait for health checks, then:
   dotnet run
   # Verify: GET http://localhost:5000/healthz
   ```

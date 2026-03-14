# Feature Specification: Infrastructure & Foundation

**Feature Branch**: `000-infra-foundation`
**Created**: 2026-03-07
**Status**: Draft
**Input**: User description: "Phase 0 - Infrastructure and Foundation: Docker Compose setup, .NET solution scaffolding, core infrastructure including AppDbContext with tenant isolation, base entity, domain event dispatcher, correlation ID middleware, global error handling, Hangfire retry policy, secrets management, JWT configuration, Redis connection, SignalR tenant-scoped hub, ILLMService and IEmbeddingService abstractions, and convention scaffolding"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Developer Bootstraps Local Environment (Priority: P1)

A developer clones the repository and runs a single command to start all required infrastructure services (PostgreSQL with pgvector, Redis, MinIO) and the backend API. The system starts cleanly, all services are healthy, and the developer can begin building features immediately.

**Why this priority**: Without a working local environment, no development can happen. This is the absolute prerequisite for all subsequent work.

**Independent Test**: Can be fully tested by running `docker compose up`, waiting for all health checks to pass, and verifying each service responds to a basic connectivity check (database query, Redis ping, MinIO endpoint, API health endpoint).

**Acceptance Scenarios**:

1. **Given** a fresh clone of the repository, **When** the developer runs `docker compose up`, **Then** PostgreSQL, Redis, MinIO, and the Backend API all start and report healthy status within 60 seconds.
2. **Given** all services are running, **When** the developer hits the backend API health check endpoint, **Then** the system returns a success response confirming database connectivity, Redis connectivity, and Hangfire readiness.
3. **Given** the PostgreSQL instance is running, **When** the developer queries for the pgvector extension, **Then** the extension is loaded and available for use.
4. **Given** services are running, **When** the developer shuts down and restarts Docker Compose, **Then** data persisted in PostgreSQL and MinIO survives the restart (volumes are configured).

---

### User Story 2 - Backend API Boots with All Middleware (Priority: P1)

A developer starts the backend API and the application initializes with all foundational middleware — structured logging, correlation ID tracking, global error handling, and Hangfire background job processing. Every incoming request is assigned a correlation ID that flows through all log entries for that request.

**Why this priority**: Middleware and observability are cross-cutting concerns required by every feature. Without them, debugging and auditing are impossible.

**Independent Test**: Can be fully tested by sending any HTTP request to the API, verifying the response includes a correlation ID header, checking structured logs contain that same correlation ID, and confirming the global error handler returns a standardized error DTO for invalid requests.

**Acceptance Scenarios**:

1. **Given** the backend API is running, **When** any HTTP request is made, **Then** the response includes a correlation ID in both the response headers and structured log output.
2. **Given** the backend API is running, **When** a request causes an unhandled exception, **Then** the global error handler catches it and returns a standardized error response with a correlation ID, without exposing internal details.
3. **Given** the backend API is running, **When** a request is processed, **Then** all log entries for that request share the same correlation ID and use structured logging format (Serilog).
4. **Given** the backend API is running, **When** the developer navigates to the Hangfire dashboard, **Then** the dashboard is accessible and shows the job processing infrastructure is ready.

---

### User Story 3 - Database Context Enforces Tenant Isolation (Priority: P1)

A developer building any feature relies on the `AppDbContext` to automatically filter all organization-scoped queries by `OrganizationId`. The developer does not need to manually add tenant filters — the global query filter ensures no cross-organization data leakage by default.

**Why this priority**: Tenant isolation is a security-critical foundation. Every subsequent feature that stores organization-scoped data depends on this being correct from day one.

**Independent Test**: Can be fully tested by seeding data for two different organizations, querying through the context, and verifying that only the current tenant's data is returned — without any explicit filter in the query.

**Acceptance Scenarios**:

1. **Given** data exists for Organization A and Organization B, **When** a query runs in the context of Organization A, **Then** only Organization A's data is returned.
2. **Given** the global query filter is active, **When** a developer writes a LINQ query without specifying `OrganizationId`, **Then** the filter is automatically applied and cross-tenant data is excluded.
3. **Given** the base entity class is used for a new entity, **When** the entity is saved, **Then** `CreatedAtUtc` and `UpdatedAtUtc` are automatically set to the current UTC time.
4. **Given** an entity has a `Status` field, **When** a background job fails after 3 retries, **Then** the entity status is automatically marked as `Failed`.

---

### User Story 4 - Domain Events Enable Feature Communication (Priority: P2)

A developer publishes a domain event from one feature (e.g., `UserRegisteredEvent`) and another feature handles it without any direct service coupling. The domain event dispatcher (MediatR) ensures loose coupling between features as required by the constitution.

**Why this priority**: Domain events are the only permitted cross-feature communication mechanism. Without this infrastructure, features cannot interact safely.

**Independent Test**: Can be fully tested by publishing a test domain event and verifying the registered handler receives it, without any direct reference between the publisher and subscriber.

**Acceptance Scenarios**:

1. **Given** a domain event class implements `INotification`, **When** it is published via MediatR, **Then** all registered handlers for that event type are invoked.
2. **Given** two features need to communicate, **When** Feature A publishes an event, **Then** Feature B handles it without any compile-time dependency on Feature A's internal services.
3. **Given** a domain event is published, **When** the event is processed, **Then** the event and its outcome are logged with the request's correlation ID.

---

### User Story 5 - AI Service Abstractions Are Callable (Priority: P2)

A developer working on any AI-powered feature injects `ILLMService` or `IEmbeddingService` and calls them without knowing or caring about the underlying AI provider. The abstractions follow the OpenAI API standard, and the default implementations can be swapped via configuration alone.

**Why this priority**: AI abstractions are consumed by multiple downstream features (summarization, task extraction, RAG). Setting them up early avoids repeated integration work.

**Independent Test**: Can be fully tested by resolving `ILLMService` and `IEmbeddingService` from the DI container, calling them with mock inputs, and verifying the responses conform to the expected contract (typed response objects, cancellation token support).

**Acceptance Scenarios**:

1. **Given** the DI container is configured, **When** a service requests `ILLMService`, **Then** the concrete implementation is injected and callable.
2. **Given** the DI container is configured, **When** a service requests `IEmbeddingService`, **Then** the concrete implementation is injected and callable.
3. **Given** the `ILLMService` is called, **When** a `CancellationToken` is cancelled, **Then** the operation terminates gracefully without hanging.
4. **Given** the AI provider is temporarily unavailable, **When** a call is made through the abstraction, **Then** Polly retry and circuit-breaker policies handle the failure transparently.

---

### User Story 6 - SignalR Hub Supports Tenant-Scoped Messaging (Priority: P2)

A developer sends a real-time notification (e.g., AI status update, reminder) through SignalR and it is delivered only to users who belong to the target organization. No user from another organization receives the message. The hub uses `org:{OrganizationId}` groups to enforce this.

**Why this priority**: SignalR tenant isolation is security-critical and underpins all real-time notifications in later phases. Getting it wrong would cause cross-tenant data leakage.

**Independent Test**: Can be fully tested by connecting two clients from different organizations, sending a message to one organization's group, and verifying only the correct client receives it.

**Acceptance Scenarios**:

1. **Given** a user connects to the SignalR hub with a valid JWT, **When** the connection is established, **Then** the hub reads the `organizationId` claim from the JWT and adds the connection to exactly one group: `org:{organizationId}`.
2. **Given** a message is sent to `Clients.Group("org:{OrgA}")`, **When** User A (member of OrgA) and User B (member of OrgB) are connected, **Then** only User A receives the message.
3. **Given** a user disconnects, **When** the connection is dropped, **Then** the connection is automatically removed from all groups.
4. **Given** a background job needs to notify users, **When** it sends via SignalR, **Then** it uses `Clients.Group(...)` and never `Clients.All`.

---

### User Story 7 - Secrets Are Never Hardcoded (Priority: P3)

A developer configures sensitive values (JWT signing keys, LiveKit Cloud API credentials, Redis connection strings, MinIO credentials) through secure configuration channels. In development, .NET user-secrets are used. In Docker, environment variables are injected. No secret appears in source code or configuration files committed to the repository.

**Why this priority**: Important for security compliance but less likely to block feature development. The mechanism is straightforward once configured.

**Independent Test**: Can be fully tested by scanning the repository for hardcoded secrets, verifying user-secrets work in dev, and confirming Docker environment variables inject correctly at runtime.

**Acceptance Scenarios**:

1. **Given** the development environment, **When** a developer needs to configure a JWT signing key, **Then** they use `dotnet user-secrets set` and the application reads it from the user-secrets store.
2. **Given** the Docker environment, **When** the application starts, **Then** secrets are read from environment variables defined in Docker Compose (not from committed config files).
3. **Given** the codebase, **When** scanned for known secret patterns (API keys, connection strings, passwords), **Then** no hardcoded secrets are found.

---

### User Story 8 - Hangfire Retry Policy Protects Against Transient Failures (Priority: P3)

A background job (e.g., AI processing, file download) fails due to a transient error. The system automatically retries with exponential backoff up to 3 times. If all retries are exhausted, the related entity's status is marked as `Failed` and the failure is logged for investigation.

**Why this priority**: Retry infrastructure is consumed by later phases (AI pipeline, recording download, Trello sync). The mechanism is generic and can be validated early.

**Independent Test**: Can be fully tested by creating a test Hangfire job that deliberately fails, verifying it retries 3 times with increasing delays, and confirming the entity status transitions to `Failed` after exhaustion.

**Acceptance Scenarios**:

1. **Given** a Hangfire job fails on first attempt, **When** the retry policy is active, **Then** the job is retried up to 3 times with exponential backoff.
2. **Given** a Hangfire job fails on all 3 retries, **When** retry exhaustion occurs, **Then** the associated entity's `Status` is set to `Failed`.
3. **Given** a Hangfire job fails, **When** each retry is attempted, **Then** the failure and retry attempt are logged with structured logging and a correlation ID.
4. **Given** a Hangfire job succeeds on a retry attempt, **When** the retry is successful, **Then** processing continues normally and no `Failed` status is set.

---

### Edge Cases

- What happens if Docker Compose starts but PostgreSQL takes longer than expected to initialize? (Backend health check should retry until database is ready.)
- What happens if Redis is unavailable when the backend starts? → **Resolved**: Application fails fast with a clear startup error. Redis is a mandatory dependency; a missing Redis indicates a broken environment.
- How does the system handle concurrent requests that arrive before the correlation ID middleware is fully initialized? → **Resolved**: Middleware uses client-provided correlation ID if present in the request header, otherwise generates a new one. The middleware is registered early in the pipeline, so all requests are covered.
- What happens when a SignalR client sends a connection request with an expired or invalid JWT?
- What happens if the pgvector extension is not installed in the PostgreSQL instance?
- How does the system behave when the `ILLMService` provider endpoint is misconfigured (wrong URL or invalid API key)?

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST provide a Docker Compose configuration that starts PostgreSQL (with pgvector), Redis, MinIO, and the Backend API with health checks for all services.
- **FR-002**: System MUST provide a .NET solution with a feature-based modular monolith structure: `src/Features/`, `src/Infrastructure/`, `src/Shared/`, and `Program.cs`. Each feature folder MUST include an `Endpoints/` directory with subfolders per controller group, following the Partial Controller Pattern (one endpoint per file via partial classes).
- **FR-003**: System MUST include all required NuGet packages on project creation: ASP.NET Identity, JWT Bearer, EF Core (Npgsql), Hangfire, StackExchange.Redis, LiveKit Server SDK (.NET), FluentValidation, Mapster, MediatR, Serilog, and Polly.
- **FR-004**: System MUST implement `AppDbContext` with a global query filter that automatically filters all organization-scoped entities by `OrganizationId`.
- **FR-005**: System MUST provide a base entity class with `CreatedAtUtc` and `UpdatedAtUtc` fields (UTC-only timestamps, automatically populated on save).
- **FR-006**: System MUST define a `Status` enum that includes a `Failed` state for use by entities that participate in background processing.
- **FR-007**: System MUST implement a domain event dispatcher using MediatR `INotification` for cross-feature communication.
- **FR-008**: System MUST implement correlation ID middleware that assigns a unique correlation ID to every incoming request, includes it in the response headers, and ensures all log entries for that request share the same ID. If the incoming request already includes a correlation ID header (e.g., `X-Correlation-Id`), the middleware MUST use the client-provided value; otherwise, it MUST generate a new unique ID.
- **FR-009**: System MUST implement global error handling middleware that catches unhandled exceptions and returns a standardized error response DTO containing the error type, title, HTTP status, field-level errors (if applicable), and correlation ID — without exposing internal details.
- **FR-010**: System MUST configure Hangfire with PostgreSQL storage, including a job filter for exponential backoff retry (max 3 retries) that marks the associated entity's `Status` as `Failed` on retry exhaustion.
- **FR-011**: System MUST configure secrets management using .NET user-secrets for development and environment variables for Docker deployment. No secrets may be hardcoded or committed to the repository.
- **FR-012**: System MUST configure JWT authentication infrastructure (signing key loading from secure configuration, token validation parameters) ready for consumption by the Identity feature.
- **FR-013**: System MUST configure a Redis connection registered in DI for caching use by downstream features. Redis is a mandatory dependency — the application MUST fail fast with a clear error at startup if Redis is unreachable.
- **FR-014**: System MUST implement a SignalR base hub that authenticates users via JWT on connection, reads the `organizationId` claim from the token, and adds the connection to exactly one group: `org:{organizationId}`. `Clients.All` usage is prohibited.
- **FR-015**: System MUST implement `ILLMService` and `IEmbeddingService` abstractions following the OpenAI API standard, with default implementations using typed `HttpClient` and Polly resiliency policies (retry + circuit-breaker).
- **FR-016**: System MUST configure structured logging using Serilog with correlation ID enrichment, ensuring all domain events and request lifecycle stages are logged.
- **FR-017**: System MUST scaffold FluentValidation registration so downstream features can register validators via DI without additional configuration.
- **FR-018**: System MUST enforce `CancellationToken` as a required parameter on all async method signatures in shared and infrastructure code.
- **FR-018.1**: System MUST scaffold Partial Controller Pattern base conventions: controller definition files contain `[ApiController]`, `[Route]`, base class, constructor, and shared dependencies; endpoint files contain exactly one action method in a `partial class` with no constructor, attributes, or field declarations. Mapster object mapping configuration MUST be bootstrapped for use by downstream features.
- **FR-019**: System MUST provide a backend API health check endpoint that verifies connectivity to PostgreSQL, Redis, and reports Hangfire readiness.
- **FR-020**: System MUST configure the LiveKit Cloud project with API key and secret stored in secure configuration, ready for token generation by downstream features.

### Key Entities

- **Base Entity**: A shared base class for all domain entities. Provides `Id` (Guid), `CreatedAtUtc`, and `UpdatedAtUtc` (UTC timestamps, automatically managed). All domain entities inherit from this.
- **Status Enum**: A shared enumeration for entities that undergo background processing. Values include at minimum: `Pending`, `Processing`, `Completed`, `Failed`. Used across features for consistent lifecycle tracking.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Developer can go from fresh clone to running system in under 5 minutes with a single `docker compose up` command.
- **SC-002**: 100% of HTTP requests include a correlation ID in both response headers and all associated log entries.
- **SC-003**: Global error handler returns a standardized error response for 100% of unhandled exceptions, with no internal details leaked.
- **SC-004**: Hangfire job retry exhaustion marks entity status as `Failed` within 30 seconds of the final retry failure.
- **SC-005**: SignalR messages sent to an organization group are received only by users belonging to that organization — zero cross-tenant leakage.
- **SC-006**: `ILLMService` and `IEmbeddingService` are resolvable from the DI container and respond to test calls within 5 seconds (with mock provider).
- **SC-007**: All timestamps stored in the database are in UTC, verified by automated tests that check database values match UTC expectations.
- **SC-008**: Backend API health check confirms PostgreSQL, Redis, and Hangfire connectivity within 2 seconds.
- **SC-009**: pgvector extension is loaded and queryable in PostgreSQL on first boot.
- **SC-010**: No hardcoded secrets found in any committed file (verified by scanning the repository).

## Clarifications

### Session 2026-03-07

- Q: When the backend API starts and Redis is unreachable, should the application fail fast or start in degraded mode with caching disabled? → A: Mandatory — fail fast with a clear error if Redis is unreachable at startup. Docker Compose starts Redis alongside all services, so an unreachable Redis indicates a broken environment that must be fixed.
- Q: When an incoming HTTP request already includes a correlation ID header, should the middleware use the client-provided ID or always generate a new one? → A: Prefer client-provided — use the existing header if present, generate a new unique ID otherwise. This enables end-to-end distributed tracing when an upstream proxy or gateway sets the header.

## Assumptions

- LiveKit Cloud account is pre-created and API credentials are available before development begins.
- OpenAI API (or compatible provider) credentials are available for the default `ILLMService` and `IEmbeddingService` implementations, but the system operates with mock implementations during development and testing.
- PostgreSQL version supports pgvector extension (PostgreSQL 15+ with pgvector 0.5+).
- Developer machines have Docker and .NET 10 SDK installed as prerequisites.
- The MinIO instance is used strictly for local/demo deployment; cloud object storage is out of scope.
- Redis is used for distributed caching only in this phase; pub/sub or other advanced patterns are deferred to features that need them.
- The SignalR hub in this phase is the base infrastructure setup; actual notification events are implemented in downstream features.
- EF Core migrations are the primary database schema management strategy.

## Dependencies

- **Docker**: Required for local infrastructure orchestration (PostgreSQL, Redis, MinIO).
- **LiveKit Cloud**: External managed service — account and API credentials required.
- **NuGet packages**: All listed packages must be available from the NuGet registry.
- **Constitution v1.4.0**: All infrastructure decisions must comply with the project constitution (including the Partial Controller Pattern endpoint architecture rule added in v1.3.3, and the simplified membership model added in v1.4.0).

## Out of Scope

- Feature-specific business logic (Identity, Organizations, Meetings, etc.)
- Frontend or UI of any kind
- Cloud deployment or CI/CD pipeline
- Production-grade monitoring or alerting (APM, Prometheus, Grafana)
- Load balancing or horizontal scaling configuration
- Database backup or disaster recovery procedures
- Custom Hangfire dashboard authentication (basic access for local dev only)
- LiveKit Cloud room management or webhook processing (deferred to Phase 4)
- AI model selection, prompt engineering, or fine-tuning

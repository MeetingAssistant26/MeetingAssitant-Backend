# AI-Powered Meeting Assistant

## Revised Implementation Plan (v3.3 — Partial Controller Pattern)

### .NET 10 + LiveKit Cloud + LLM Abstraction + Meeting Memory (RAG)

**Constitution**: v1.3.2 → v1.4.0 (amendments applied) | **Author**: Solo Developer | **Date**: 2026-03-13

> This plan supersedes v3.2 (2026-03-06). v3.3 applies the **Partial Controller Pattern**
> across all phases, replacing Minimal API endpoints with ASP.NET Controllers using
> vertical slice architecture — one endpoint per file via partial classes.
> See "Decisions & Changes" at the end for the full change log across all revisions.

### Terminology: Two Kinds of Context

| Term | What It Is | Where It Lives | Used For |
|------|-----------|----------------|----------|
| **Member Context** | Human-authored descriptions of each member's role, expertise, and responsibilities within the organization | `UserOrgMembership.Context` (text column) | Injected into LLM prompts during task extraction so the model can suggest appropriate assignees |
| **Meeting Memory** | Vectorized embeddings of past meeting summaries and key transcript segments | `MeetingEmbedding` table (pgvector `vector(1536)`) | RAG retrieval — enables queries like *"What did we say about Flutter last time?"* by finding semantically similar past content |

> These serve different purposes and MUST NOT be confused. Member Context is
> relational text managed by humans. Meeting Memory is AI-generated vector
> data managed by the post-meeting pipeline.

### Key Architectural Decisions (v3.3)

| Decision | Detail |
|----------|--------|
| **LiveKit hosting** | LiveKit Cloud (no self-hosted server, egress, or TURN/STUN) |
| **LLM coupling** | Provider-agnostic via `ILLMService` + `IEmbeddingService` abstractions (OpenAI-compatible API standard) |
| **Frontend** | Out of scope — API-first approach only |
| **Pipelines** | Realtime pipeline (live transcription) separated from post-meeting AI pipeline |
| **Task sync** | Human-in-the-loop Review Queue before any external sync |
| **Member Context** | `UserOrgMembership.Context` text field — human-managed role/expertise descriptions |
| **Meeting Memory** | pgvector RAG — vectorized summaries & transcript segments for cross-meeting recall (UC-M3.3-003) |
| **RAG query pattern** | Hybrid sync/async — attempt synchronous with 3s timeout, fall back to Hangfire + SignalR (constitution §9.2 compliant) |
| **Recording pipeline** | LiveKit Cloud Egress → Cloud storage → Hangfire download job → local MinIO |
| **SignalR tenant safety** | All notifications scoped to `OrganizationId` SignalR Groups — no broadcast to all clients (v3.1) |
| **Membership model** | Simplified: one active organization per user. `UserOrgMembership` preserved for metadata. Registration requires organization (v3.4) |
| **Transcription flush** | 10-second ready-check delay before `SummarizeTranscriptJob` to ensure all webhook segments are persisted (v3.1) |
| **Endpoint architecture** | Partial Controller Pattern — one endpoint per file, controller definition separated (v3.3) |

---

## Endpoint Architecture: Partial Controller Pattern (v3.3)

### Pattern Overview

All API endpoints use **ASP.NET Controllers** (not Minimal APIs) with a **Partial Controller Pattern**:

- Each controller is split into a **definition file** and multiple **endpoint files**
- The definition file contains: namespace, `[ApiController]`, `[Route]`, base class, constructor, shared dependencies
- Each endpoint file contains exactly **one action method** in a `partial class`
- This enables vertical slice architecture while maintaining controller-based routing

### Controller Definition File: `{ControllerName}Controller.cs`

```csharp
namespace MeetingAssistant.Features.{FeatureName}.Endpoints.{Group};

[ApiController]
[Route("api/...")]
public partial class {Name}Controller : ControllerBase
{
    private readonly I{Service} _{service};

    public {Name}Controller(I{Service} service)
    {
        _service = service;
    }
}
```

### Endpoint File: `{ActionName}Endpoint.cs`

```csharp
namespace MeetingAssistant.Features.{FeatureName}.Endpoints.{Group};

public partial class {Name}Controller
{
    [Http{Verb}("route")]
    public async Task<IActionResult> {ActionName}(
        ...,
        CancellationToken cancellationToken)
    {
        var result = await _service.DoSomethingAsync(..., cancellationToken);

        if (result.IsSuccess)
            return Ok(result.Value);

        return result.ToProblem(correlationIdProvider);
    }
}
```

### Rules

1. **Namespace must match folder path**: `MeetingAssistant.Features.{FeatureName}.Endpoints.{Group}`
2. Each endpoint file contains exactly **ONE** action method
3. Endpoint files must **NOT** contain: constructor, `[ApiController]`, `[Route]`, base class declaration, dependency fields
4. All async actions must accept `CancellationToken` as the last parameter
5. Controllers must stay thin — delegate to Services
6. Business logic must stay inside Services
7. Mapping uses **Mapster**
8. Validation uses **FluentValidation**
9. Avoid large controllers — max **4–5 endpoints per controller**. Group by domain responsibility
10. Use `[FromBody]`, `[FromRoute]`, `[FromQuery]` explicitly on all parameters

### File Naming Conventions

```
✔ {ControllerName}Controller.cs    → definition only (attributes, ctor, fields)
✔ {ActionName}Endpoint.cs          → exactly one [Http*] method
✔ {RequestName}Request.cs          → sealed record in Models/Requests/
✔ {ResponseName}Response.cs        → sealed record in Models/Responses/
✔ {RequestName}Validator.cs        → FluentValidation in Validators/
✔ I{ServiceName}Service.cs         → interface in Services/
✔ {ServiceName}Service.cs          → implementation in Services/
```

---

# Phase 0 — Infrastructure & Foundation (Weeks 1–2)

## 0.1 Infrastructure Setup

Docker Compose services (local dev & demo):

- PostgreSQL (with pgvector extension — **active use** for Meeting Memory RAG)
- Redis
- MinIO
- Backend API

External managed services:

- **LiveKit Cloud** — rooms, media transport, egress/recording, webhooks
  - No local LiveKit Server, Egress, or TURN/STUN configuration required
  - Backend connects via LiveKit Cloud API keys

> **Change from v1**: Removed self-hosted LiveKit Server, LiveKit Egress, and LiveKit .NET Agent
> from Docker Compose. All WebRTC infrastructure is offloaded to LiveKit Cloud.

Deliverables:
- `docker-compose.yml` (PostgreSQL, Redis, MinIO, Backend API)
- LiveKit Cloud project created with API key + secret
- All local services running with health checks
- Health check endpoints on Backend API

---

## 0.2 .NET Solution Setup

Project structure:

```text
src/
├── Features/
│   ├── Identity/
│   │   ├── Endpoints/
│   │   │   ├── Auth/
│   │   │   ├── Token/
│   │   │   └── Profile/
│   │   ├── Models/
│   │   ├── Services/
│   │   └── Validators/
│   ├── Organizations/
│   │   ├── Endpoints/
│   │   │   ├── Organization/
│   │   │   ├── Member/
│   │   │   └── Invitation/
│   │   ├── Models/
│   │   ├── Services/
│   │   └── Validators/
│   ├── Meetings/
│   │   ├── Endpoints/
│   │   │   ├── Meeting/
│   │   │   ├── Recurring/
│   │   │   ├── Participant/
│   │   │   └── Calendar/
│   │   ├── Models/
│   │   ├── Services/
│   │   └── Validators/
│   ├── LiveSession/
│   │   ├── Endpoints/
│   │   │   ├── Session/
│   │   │   ├── Webhook/
│   │   │   └── Transcript/
│   │   ├── Models/
│   │   ├── Services/
│   │   └── Validators/
│   ├── Recordings/
│   │   ├── Endpoints/
│   │   │   └── Recording/
│   │   ├── Models/
│   │   ├── Services/
│   │   └── Validators/
│   ├── AiPipeline/
│   │   ├── Endpoints/
│   │   │   └── MeetingMemory/
│   │   ├── Models/
│   │   ├── Services/
│   │   └── Jobs/
│   ├── Tasks/
│   │   ├── Endpoints/
│   │   │   ├── ReviewQueue/
│   │   │   ├── Task/
│   │   │   └── Reminder/
│   │   ├── Models/
│   │   ├── Services/
│   │   └── Validators/
│   └── Integrations/
│       ├── Endpoints/
│       │   └── Trello/
│       ├── Models/
│       ├── Services/
│       └── Validators/
├── Infrastructure/
│   ├── LLM/              ← ILLMService abstraction
│   ├── Embedding/        ← IEmbeddingService abstraction
│   ├── Persistence/
│   ├── Caching/
│   └── Storage/
├── Shared/
└── Program.cs
```

Required packages:

- ASP.NET Identity
- JWT Bearer
- EF Core (Npgsql)
- Hangfire + Hangfire.PostgreSql
- StackExchange.Redis
- LiveKit Server SDK (.NET) — for token generation & Cloud API calls
- FluentValidation
- Mapster (object mapping)
- MediatR (domain event dispatch)
- Serilog
- Polly (resiliency)

Deliverables:
- Modular project bootstrapped with Partial Controller Pattern structure
- Structured logging configured (Serilog)
- Database migrations working

---

## 0.3 Core Infrastructure

Implement:

- `AppDbContext` with global query filter for `OrganizationId` (tenant isolation)
- Base entity with `CreatedAtUtc`, `UpdatedAtUtc` (UTC-only timestamps)
- Entity `Status` enum including `Failed` state (constitution §3.2)
- Domain event dispatcher (MediatR `INotification`)
- **Correlation ID middleware** (constitution §9.1)
- Global error handling middleware + `StandardErrorResponse` DTO
- **`ResultExtensions.ToProblem()` helper** (`Shared/ResultExtensions.cs`) — extension method on `Result` that converts a failed result into an `ObjectResult` containing a `StandardErrorResponse`, setting the HTTP status code from `result.Error.StatusCode` and attaching the `CorrelationId` from `ICorrelationIdProvider`. Throws `InvalidOperationException` if called on a successful result. Endpoints use `return result.ToProblem(correlationIdProvider);` instead of manually constructing error responses
- **Hangfire retry policy infrastructure**: job filter for exponential backoff, max 3 retries, mark entity `Status = Failed` on exhaustion (constitution §3.2)
- **Secrets management**: .NET user-secrets for dev, environment variables in Docker (constitution §5.3)
- JWT configuration (signing keys from config — never hardcoded)
- Redis connection
- Hangfire configuration
- SignalR base hub with **tenant-scoped groups** (v3.4 simplified):
  - On connection, authenticate the user via JWT, then read the `organizationId` claim from the token
  - Add the connection to exactly **one** SignalR Group: `org:{organizationId}`
  - On disconnect, ASP.NET Core automatically removes the connection from all groups
  - **All background job notifications** (AI status updates, task events, RAG answers, reminders) MUST be sent to `Clients.Group($"org:{organizationId}")`, NEVER to `Clients.All`
  - This prevents tenant data leaks in the shared multi-tenant SignalR hub (constitution §2.1)

### LLM & Embedding Abstraction Layers

```csharp
// Text generation (completions)
public interface ILLMService
{
    Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct);
    Task<LLMResponse> CompleteWithJsonAsync<T>(LLMRequest request, CancellationToken ct);
}

// Vector embedding generation (new in v3)
public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
```

**`ILLMService`**:
- Follows the **OpenAI API standard** (`/v1/chat/completions` contract)
- Default implementation: `OpenAiLLMService` using typed `HttpClient`
- Any OpenAI-compatible provider (Azure OpenAI, Ollama, LM Studio, Groq, etc.) can be swapped via configuration — no code changes

**`IEmbeddingService`** (new in v3):
- Follows the **OpenAI API standard** (`/v1/embeddings` contract)
- Default implementation: `OpenAiEmbeddingService` (model: `text-embedding-3-small`, 1536 dimensions)
- Any OpenAI-compatible embedding provider can be swapped via configuration
- Used by the post-meeting pipeline to generate Meeting Memory vectors

Both services:
- Polly resiliency policies (retry, circuit-breaker) at `HttpClient` level
- Registered via DI; all features depend on interfaces, never on concrete providers

### ResultExtensions.ToProblem() Helper

```csharp
// Shared/ResultExtensions.cs
public static class ResultExtensions
{
    public static ObjectResult ToProblem(
        this Result result,
        ICorrelationIdProvider correlationIdProvider)
    {
        if (result.IsSuccess)
            throw new InvalidOperationException(
                "Cannot convert a successful result to a problem.");

        var response = new StandardErrorResponse
        {
            Type = result.Error.Code,
            Title = result.Error.Description,
            Status = result.Error.Statuscode,
            CorrelationId = correlationIdProvider.CorrelationId
        };

        return new ObjectResult(response)
        {
            StatusCode = result.Error.Statuscode
        };
    }
}
```

**Usage in endpoints:**

```csharp
var result = await service.DoSomething(request, cancellationToken);

if (result.IsSuccess)
    return Ok(result.Value);

return result.ToProblem(correlationIdProvider);
```

- Maps `Result.Error` fields to `StandardErrorResponse` (the official error DTO defined in this phase)
- Sets the HTTP status code from `result.Error.Statuscode`
- Attaches the `CorrelationId` from `ICorrelationIdProvider` (generated by `CorrelationIdMiddleware`)
- Throws `InvalidOperationException` if called on a successful result (programming error guard)
- Global exception middleware remains unchanged — this helper handles expected business failures, the middleware handles unexpected exceptions

Convention scaffolding:
- `CancellationToken` on all async signatures
- Explicit DTO base classes (sealed records)
- FluentValidation registration
- Mapster configuration bootstrapping
- **Partial Controller Pattern** base conventions established

Deliverables:
- System boots clean
- Hangfire dashboard accessible
- Correlation IDs visible in logs
- `ILLMService` and `IEmbeddingService` registered and callable
- Partial Controller folder structure scaffolded for all features

**Tests (Phase 0)**:
- Smoke test: DB connection, Redis ping, Hangfire dashboard, SignalR connect, pgvector extension loaded
- Unit: `ILLMService` mock verifies contract compliance
- Unit: `IEmbeddingService` mock verifies contract compliance

---

# Phase 1 — Identity (Weeks 2–3)

## Entities

- `ApplicationUser` (extends `IdentityUser`)
- `RefreshToken` (hash, expiry, rotation tracking)

> **Note**: Registration requires an organization. Two flows are supported:
> - **Scenario A**: Register + Create Organization → user becomes Admin
> - **Scenario B**: Register via Invitation → user becomes Member
> In both cases, a `UserOrgMembership` record is created, and the JWT
> includes `userId` + `organizationId`.

## Folder Structure

```text
Features/
└── Identity/
    ├── Endpoints/
    │   ├── Auth/
    │   │   ├── AuthController.cs
    │   │   ├── RegisterEndpoint.cs
    │   │   ├── LoginEndpoint.cs
    │   │   └── LogoutEndpoint.cs
    │   │
    │   ├── Token/
    │   │   ├── TokenController.cs
    │   │   └── RefreshTokenEndpoint.cs
    │   │
    │   └── Profile/
    │       ├── ProfileController.cs
    │       ├── GetCurrentUserEndpoint.cs
    │       ├── UpdateProfileEndpoint.cs
    │       └── ChangePasswordEndpoint.cs
    │
    ├── Models/
    │   ├── Requests/
    │   │   ├── RegisterRequest.cs
    │   │   ├── LoginRequest.cs
    │   │   ├── RefreshTokenRequest.cs
    │   │   ├── UpdateProfileRequest.cs
    │   │   └── ChangePasswordRequest.cs
    │   └── Responses/
    │       ├── AuthResponse.cs
    │       ├── TokenResponse.cs
    │       └── UserProfileResponse.cs
    │
    ├── Services/
    │   ├── IAuthService.cs
    │   ├── AuthService.cs
    │   ├── ITokenService.cs
    │   ├── TokenService.cs
    │   ├── IProfileService.cs
    │   └── ProfileService.cs
    │
    └── Validators/
        ├── RegisterRequestValidator.cs
        ├── LoginRequestValidator.cs
        ├── RefreshTokenRequestValidator.cs
        ├── UpdateProfileRequestValidator.cs
        └── ChangePasswordRequestValidator.cs
```

## Controller Definitions

### AuthController — `api/auth`

```csharp
namespace MeetingAssistant.Features.Identity.Endpoints.Auth;

[ApiController]
[Route("api/auth")]
public partial class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }
}
```

**Endpoints:**
- `POST /api/auth/register` → `RegisterEndpoint.cs` (Scenario A: register + create org)
- `POST /api/auth/register/invite` → `RegisterWithInviteEndpoint.cs` (Scenario B: register via invitation)
- `POST /api/auth/login` → `LoginEndpoint.cs`
- `POST /api/auth/logout` → `LogoutEndpoint.cs`

### TokenController — `api/auth/tokens`

```csharp
namespace MeetingAssistant.Features.Identity.Endpoints.Token;

[ApiController]
[Route("api/auth/tokens")]
public partial class TokenController : ControllerBase
{
    private readonly ITokenService _tokenService;

    public TokenController(ITokenService tokenService)
    {
        _tokenService = tokenService;
    }
}
```

**Endpoints:**
- `POST /api/auth/tokens/refresh` → `RefreshTokenEndpoint.cs`

### ProfileController — `api/auth/profile`

```csharp
namespace MeetingAssistant.Features.Identity.Endpoints.Profile;

[ApiController]
[Route("api/auth/profile")]
public partial class ProfileController : ControllerBase
{
    private readonly IProfileService _profileService;

    public ProfileController(IProfileService profileService)
    {
        _profileService = profileService;
    }
}
```

**Endpoints:**
- `GET /api/auth/profile` → `GetCurrentUserEndpoint.cs`
- `PUT /api/auth/profile` → `UpdateProfileEndpoint.cs`
- `POST /api/auth/profile/change-password` → `ChangePasswordEndpoint.cs`

## Example: LoginEndpoint Implementation

```csharp
// LoginEndpoint.cs
namespace MeetingAssistant.Features.Identity.Endpoints.Auth;

public partial class AuthController
{
    /// <summary>
    /// Authenticates a user and returns access + refresh tokens.
    /// JWT includes userId and organizationId (from active UserOrgMembership).
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        // AuthService validates credentials, queries UserOrgMembership
        // for the user's active membership, and embeds organizationId in JWT
        var result = await _authService.LoginAsync(request, cancellationToken);

        if (result.IsSuccess)
            return Ok(result.Value);

        return result.ToProblem(correlationIdProvider);
    }
}
```

## Security

- JWT Access Token (15-minute expiry) — includes `userId` + `organizationId` claims
- Refresh token rotation with hashed storage (7-day expiry)
- Policy-based authorization scaffolding
- `organizationId` resolved from user's active `UserOrgMembership` during login
- Token refresh carries `organizationId` from existing token; refresh fails if membership deactivated

## Domain Events

- `UserRegisteredEvent`
- `UserLoggedInEvent`

## Tests (Phase 1)

- Unit: token rotation logic, refresh-token hash verification, policy evaluation
- Integration: full register → login → refresh → logout flow

Deliverable:
- Full authentication working with two registration flows (create org / join via invite)
- JWT tokens include `organizationId` from active membership
- Token refresh tested
- Partial Controller Pattern established with 3 controllers, 8 endpoint files

---

# Phase 2 — Organizations (Weeks 3–4)

## Entities

- **`Organization`**:
  - `Id`, `Name`, `Slug` (unique URL-safe identifier)
  - `CreatedAtUtc`
- `UserOrgMembership` (with `OrganizationRole` enum: Admin / Member / Guest)
  - `UserId`, `OrganizationId`
  - `OrgRole` (OrganizationRole enum)
  - `JobRole` (text, nullable — user's job title within the org)
  - **`Context`** field (text) — stores **Member Context**: the member's organizational role, expertise, and responsibilities
    - Examples: *"Ahmed works as a backend engineer"*, *"Sara is responsible for UI/UX"*, *"Ali manages DevOps and infrastructure"*
    - This is **human-managed** data, not AI-generated
    - Retrieved during AI task extraction and injected into LLM prompts so the model can suggest appropriate assignees
  - `ContextStatus` (text, nullable)
  - `IsEnabled` (bool, default true)
  - **DB Constraint**: `UNIQUE(user_id) WHERE is_enabled = true` — enforces one active membership per user

> **Simplified Membership Model (v3.4)**: Each user may have only one active
> `UserOrgMembership` at a time. The membership entity is preserved because
> it stores metadata required by AI features (context injection for task
> extraction). A user joining a new org must first leave their current org.
- `Invitation` (JSONB for email whitelist)
  - `Id`, `OrganizationId`, `InvitedByUserId`
  - `EmailWhitelist` (JSONB — array of allowed emails)
  - `Token` (unique invite link token)
  - `ExpiresAtUtc`, `CreatedAtUtc`

> **Terminology note**: `UserOrgMembership.Context` stores **Member Context** (people knowledge).
> This is distinct from **Meeting Memory** (vectorized past meeting content in Phase 6).
> See the terminology table at the top of this document.

## Folder Structure

```text
Features/
└── Organizations/
    ├── Endpoints/
    │   ├── Organization/
    │   │   ├── OrganizationController.cs
    │   │   └── CreateOrganizationEndpoint.cs
    │   │
    │   ├── Member/
    │   │   ├── MemberController.cs
    │   │   ├── ListMembersEndpoint.cs
    │   │   ├── UpdateMemberRoleEndpoint.cs
    │   │   └── UpdateMemberContextEndpoint.cs
    │   │
    │   └── Invitation/
    │       ├── InvitationController.cs
    │       ├── CreateInvitationEndpoint.cs
    │       └── JoinOrganizationEndpoint.cs
    │
    ├── Models/
    │   ├── Requests/
    │   │   ├── CreateOrganizationRequest.cs
    │   │   ├── CreateInvitationRequest.cs
    │   │   ├── JoinOrganizationRequest.cs
    │   │   ├── UpdateMemberRoleRequest.cs
    │   │   └── UpdateMemberContextRequest.cs
    │   └── Responses/
    │       ├── OrganizationResponse.cs
    │       ├── MemberResponse.cs
    │       └── InvitationResponse.cs
    │
    ├── Services/
    │   ├── IOrganizationService.cs
    │   ├── OrganizationService.cs
    │   ├── IMemberService.cs
    │   ├── MemberService.cs
    │   ├── IInvitationService.cs
    │   └── InvitationService.cs
    │
    └── Validators/
        ├── CreateOrganizationRequestValidator.cs
        ├── CreateInvitationRequestValidator.cs
        ├── JoinOrganizationRequestValidator.cs
        ├── UpdateMemberRoleRequestValidator.cs
        └── UpdateMemberContextRequestValidator.cs
```

## Controller Definitions

### OrganizationController — `api/organizations`

```csharp
namespace MeetingAssistant.Features.Organizations.Endpoints.Organization;

[ApiController]
[Route("api/organizations")]
public partial class OrganizationController : ControllerBase
{
    private readonly IOrganizationService _organizationService;

    public OrganizationController(IOrganizationService organizationService)
    {
        _organizationService = organizationService;
    }
}
```

**Endpoints:**
- `POST /api/organizations` → `CreateOrganizationEndpoint.cs`

### MemberController — `api/organizations/{orgId}/members`

```csharp
namespace MeetingAssistant.Features.Organizations.Endpoints.Member;

[ApiController]
[Route("api/organizations/{orgId:guid}/members")]
public partial class MemberController : ControllerBase
{
    private readonly IMemberService _memberService;

    public MemberController(IMemberService memberService)
    {
        _memberService = memberService;
    }
}
```

**Endpoints:**
- `GET /api/organizations/{orgId}/members` → `ListMembersEndpoint.cs`
- `PUT /api/organizations/{orgId}/members/{userId}/role` → `UpdateMemberRoleEndpoint.cs`
- `PUT /api/organizations/{orgId}/members/{userId}/context` → `UpdateMemberContextEndpoint.cs`
- `POST /api/organizations/{orgId}/members/leave` → `LeaveOrganizationEndpoint.cs`

> **Leave flow**: Deactivates the user's `UserOrgMembership` (`is_enabled = false`).
> The user's JWT remains valid until expiry but cannot access org-scoped resources.
> Token refresh will fail after leaving.

### InvitationController — `api/organizations/{orgId}/invitations`

```csharp
namespace MeetingAssistant.Features.Organizations.Endpoints.Invitation;

[ApiController]
[Route("api/organizations")]
public partial class InvitationController : ControllerBase
{
    private readonly IInvitationService _invitationService;

    public InvitationController(IInvitationService invitationService)
    {
        _invitationService = invitationService;
    }
}
```

**Endpoints:**
- `POST /api/organizations/{orgId}/invitations` → `CreateInvitationEndpoint.cs`
- `POST /api/organizations/join` → `JoinOrganizationEndpoint.cs`

## Invitation Flow (v3.4 — simplified membership)

When a user accepts an invitation:
1. System checks if the user has an existing active `UserOrgMembership`
2. If yes → reject with error: "You already belong to an organization"
3. If no → create `UserOrgMembership` (org_role = Member, is_enabled = true)

## Domain Events

- `OrganizationCreatedEvent`
- `MemberJoinedEvent`
- `RoleChangedEvent`
- `MemberContextUpdatedEvent`

## Tests (Phase 2)

- **Integration (critical)**: tenant isolation — prove cross-org data leakage is impossible
- Unit: invitation validation, role assignment logic, context update

Deliverable:
- Multi-tenant isolation working with single-membership constraint
- Org-scoped queries enforced
- Member context stored and retrievable
- Leave organization flow functional
- 3 controllers, 7 endpoint files

---

# Phase 3 — Meetings (Weeks 4–6)

## Entities

- **`Meeting`**:
  - `Id`, `OrganizationId`
  - `Title`, `Description`
  - `ScheduledStartUtc`, `ScheduledEndUtc`
  - `Status` (enum: Scheduled / InProgress / Completed / Cancelled / Failed)
  - `RecurrenceConfig` (JSONB, nullable)
  - `CreatedAtUtc`
- `MeetingParticipant` (with `MeetingRole` enum: Host / CoHost / Participant / Observer)
  - `Id`, `MeetingId`, `OrganizationId`, `UserId`, `MeetingRole`, `CreatedAtUtc`
- `RecurrenceConfig` (JSONB)
  - Stored as JSONB on `Meeting` entity: `{ "frequency": "weekly", "interval": 1, "daysOfWeek": [...], "endsAtUtc": "..." }`
- `MeetingTag`
  - `Id`, `MeetingId`, `OrganizationId`, `Name`, `CreatedAtUtc`

## Folder Structure

```text
Features/
└── Meetings/
    ├── Endpoints/
    │   ├── Meeting/
    │   │   ├── MeetingController.cs
    │   │   ├── CreateMeetingEndpoint.cs
    │   │   ├── UpdateMeetingEndpoint.cs
    │   │   ├── CancelMeetingEndpoint.cs
    │   │   └── ListMeetingsEndpoint.cs
    │   │
    │   ├── Recurring/
    │   │   ├── RecurringMeetingController.cs
    │   │   └── CreateRecurringMeetingEndpoint.cs
    │   │
    │   ├── Participant/
    │   │   ├── ParticipantController.cs
    │   │   ├── AddParticipantEndpoint.cs
    │   │   └── CheckConflictsEndpoint.cs
    │   │
    │   └── Calendar/
    │       ├── CalendarController.cs
    │       └── GetCalendarDataEndpoint.cs
    │
    ├── Models/
    │   ├── Requests/
    │   │   ├── CreateMeetingRequest.cs
    │   │   ├── UpdateMeetingRequest.cs
    │   │   ├── CreateRecurringMeetingRequest.cs
    │   │   └── AddParticipantRequest.cs
    │   └── Responses/
    │       ├── MeetingResponse.cs
    │       ├── MeetingListResponse.cs
    │       ├── ConflictResponse.cs
    │       └── CalendarDataResponse.cs
    │
    ├── Services/
    │   ├── IMeetingService.cs
    │   ├── MeetingService.cs
    │   ├── IRecurrenceService.cs
    │   ├── RecurrenceService.cs
    │   ├── IParticipantService.cs
    │   ├── ParticipantService.cs
    │   ├── ICalendarService.cs
    │   └── CalendarService.cs
    │
    └── Validators/
        ├── CreateMeetingRequestValidator.cs
        ├── UpdateMeetingRequestValidator.cs
        ├── CreateRecurringMeetingRequestValidator.cs
        └── AddParticipantRequestValidator.cs
```

## Controller Definitions

### MeetingController — `api/organizations/{orgId}/meetings`

```csharp
namespace MeetingAssistant.Features.Meetings.Endpoints.Meeting;

[ApiController]
[Route("api/organizations/{orgId:guid}/meetings")]
public partial class MeetingController : ControllerBase
{
    private readonly IMeetingService _meetingService;

    public MeetingController(IMeetingService meetingService)
    {
        _meetingService = meetingService;
    }
}
```

**Endpoints:**
- `POST /api/organizations/{orgId}/meetings` → `CreateMeetingEndpoint.cs`
- `PUT /api/organizations/{orgId}/meetings/{id}` → `UpdateMeetingEndpoint.cs` (route: `{id:guid}`)
- `DELETE /api/organizations/{orgId}/meetings/{id}` → `CancelMeetingEndpoint.cs` (route: `{id:guid}`)
- `GET /api/organizations/{orgId}/meetings?filter=upcoming|past` → `ListMeetingsEndpoint.cs`

### RecurringMeetingController — `api/organizations/{orgId}/meetings/recurring`

```csharp
namespace MeetingAssistant.Features.Meetings.Endpoints.Recurring;

[ApiController]
[Route("api/organizations/{orgId:guid}/meetings/recurring")]
public partial class RecurringMeetingController : ControllerBase
{
    private readonly IRecurrenceService _recurrenceService;

    public RecurringMeetingController(IRecurrenceService recurrenceService)
    {
        _recurrenceService = recurrenceService;
    }
}
```

**Endpoints:**
- `POST /api/organizations/{orgId}/meetings/recurring` → `CreateRecurringMeetingEndpoint.cs`

### ParticipantController — `api/meetings/{meetingId}/participants`

```csharp
namespace MeetingAssistant.Features.Meetings.Endpoints.Participant;

[ApiController]
[Route("api/meetings/{meetingId:guid}/participants")]
public partial class ParticipantController : ControllerBase
{
    private readonly IParticipantService _participantService;

    public ParticipantController(IParticipantService participantService)
    {
        _participantService = participantService;
    }
}
```

**Endpoints:**
- `POST /api/meetings/{meetingId}/participants` → `AddParticipantEndpoint.cs`
- `GET /api/meetings/{meetingId}/participants/conflicts` → `CheckConflictsEndpoint.cs` (route: `conflicts`)

### CalendarController — `api/organizations/{orgId}/calendar`

```csharp
namespace MeetingAssistant.Features.Meetings.Endpoints.Calendar;

[ApiController]
[Route("api/organizations/{orgId:guid}/calendar")]
public partial class CalendarController : ControllerBase
{
    private readonly ICalendarService _calendarService;

    public CalendarController(ICalendarService calendarService)
    {
        _calendarService = calendarService;
    }
}
```

**Endpoints:**
- `GET /api/organizations/{orgId}/calendar?week=...` → `GetCalendarDataEndpoint.cs`

## Domain Events

- `MeetingCreatedEvent`
- `MeetingUpdatedEvent`
- `MeetingCancelledEvent`
- `MeetingStartedEvent`
- `MeetingEndedEvent`

## Tests (Phase 3)

- Unit: conflict detection, recurrence logic, meeting state transitions
- Integration: full meeting lifecycle

Deliverable:
- Full meeting lifecycle implemented
- 4 controllers, 7 endpoint files

---

# Phase 4 — Realtime Pipeline: LiveKit Cloud & Live Transcription (Weeks 6–7.5) (refactor require by chat gpt)

> **Architecture: Two distinct pipelines**
>
> | Pipeline | Scope | Runs |
> |----------|-------|------|
> | **Realtime** (this phase) | Live audio/video, live transcription, UI captions | During the meeting |
> | **Post-meeting AI** (Phase 6) | Summarization, task extraction, insights | After meeting ends, async via Hangfire |

## LiveKit Cloud Integration

- Backend generates LiveKit access tokens via LiveKit Cloud API
- Map `MeetingRole` → LiveKit room permissions:
  - **Host**: full publish/subscribe/admin
  - **CoHost**: publish/subscribe + moderate
  - **Participant**: publish/subscribe
  - **Observer**: subscribe-only
- Permissions enforced server-side via token grants
- **LiveKit Cloud webhooks** for room lifecycle events (room started, participant joined/left, room finished)

## Live Transcription

- Use **LiveKit Cloud's built-in STT/transcription agent** (or a LiveKit Agents Framework instance hosted on LiveKit Cloud)
- Transcription results delivered via LiveKit DataChannels for real-time UI captions
- Backend receives transcript segments via webhook or DataChannel relay
- Transcript chunks stored in `TranscriptSegment` table for post-meeting processing

> **Change from v1**: No self-hosted .NET agent. LiveKit Cloud handles the live
> transcription infrastructure. The "single deployable unit" constitution constraint
> is no longer violated — only the .NET backend is deployed.

## Entities

- `TranscriptSegment` (MeetingId, OrganizationId, SpeakerUserId, Text, StartTime, EndTime, SequenceNumber, CreatedAtUtc)

## Folder Structure

```text
Features/
└── LiveSession/
    ├── Endpoints/
    │   ├── Session/
    │   │   ├── SessionController.cs
    │   │   └── GetJoinTokenEndpoint.cs
    │   │
    │   ├── Webhook/
    │   │   ├── WebhookController.cs
    │   │   └── LiveKitWebhookEndpoint.cs
    │   │
    │   └── Transcript/
    │       ├── TranscriptController.cs
    │       └── GetTranscriptEndpoint.cs
    │
    ├── Models/
    │   ├── Requests/
    │   │   └── JoinTokenRequest.cs
    │   └── Responses/
    │       ├── JoinTokenResponse.cs
    │       └── TranscriptResponse.cs
    │
    ├── Services/
    │   ├── ISessionService.cs
    │   ├── SessionService.cs
    │   ├── IWebhookService.cs
    │   ├── WebhookService.cs
    │   ├── ITranscriptService.cs
    │   └── TranscriptService.cs
    │
    └── Validators/
        └── JoinTokenRequestValidator.cs
```

## Controller Definitions

### SessionController — `api/meetings/{meetingId}/session`

```csharp
namespace MeetingAssistant.Features.LiveSession.Endpoints.Session;

[ApiController]
[Route("api/meetings/{meetingId:guid}/session")]
public partial class SessionController : ControllerBase
{
    private readonly ISessionService _sessionService;

    public SessionController(ISessionService sessionService)
    {
        _sessionService = sessionService;
    }
}
```

**Endpoints:**
- `POST /api/meetings/{meetingId}/session/join-token` → `GetJoinTokenEndpoint.cs`

### WebhookController — `api/webhooks`

```csharp
namespace MeetingAssistant.Features.LiveSession.Endpoints.Webhook;

[ApiController]
[Route("api/webhooks")]
public partial class WebhookController : ControllerBase
{
    private readonly IWebhookService _webhookService;

    public WebhookController(IWebhookService webhookService)
    {
        _webhookService = webhookService;
    }
}
```

**Endpoints:**
- `POST /api/webhooks/livekit` → `LiveKitWebhookEndpoint.cs`

### TranscriptController — `api/meetings/{meetingId}/transcript`

```csharp
namespace MeetingAssistant.Features.LiveSession.Endpoints.Transcript;

[ApiController]
[Route("api/meetings/{meetingId:guid}/transcript")]
public partial class TranscriptController : ControllerBase
{
    private readonly ITranscriptService _transcriptService;

    public TranscriptController(ITranscriptService transcriptService)
    {
        _transcriptService = transcriptService;
    }
}
```

**Endpoints:**
- `GET /api/meetings/{meetingId}/transcript` → `GetTranscriptEndpoint.cs`

> **Note**: The Meeting Memory query endpoint (RAG) has been moved to
> Phase 6.5, after the embedding pipeline is built in Phase 6.

## SignalR Notifications (tenant-scoped — v3.1)

All SignalR messages in this phase MUST be sent to `Clients.Group($"org:{organizationId}")`.
Never broadcast to `Clients.All`.

- Meeting start/end signals → sent to org group
- Participant join/leave notifications → sent to org group
- Live transcription status (active / paused / error) → sent to org group

## Tests (Phase 4)

- Unit: token permission mapping per role
- Unit: webhook event processing
- Integration: token generation → LiveKit Cloud room creation

Deliverable:
- API endpoints for LiveKit Cloud join tokens
- Live transcription flowing and stored
- Permissions enforced per meeting role
- No self-hosted WebRTC infrastructure to manage
- 3 controllers, 3 endpoint files

---

# Phase 5 — Recording & Storage (Weeks 7.5–8.5)

## LiveKit Cloud Egress

- Configure **LiveKit Cloud Egress** to export recordings to LiveKit Cloud's managed storage (S3-compatible)
- Room composite or track-based recording configuration via LiveKit Cloud dashboard / API
- No local Egress service needed
- Backend runs a **Hangfire download job** to transfer recordings from LiveKit Cloud storage to local MinIO

> **Why not direct Egress → MinIO?** LiveKit Cloud Egress runs in the cloud
> and cannot reach a Docker-local MinIO instance without public exposure.
> The two-step approach (Cloud storage → download to MinIO) works for both
> local demo and future cloud deployments.

## Recording Download Pipeline

```
LiveKit Cloud Egress completes recording
  → Webhook: egress_ended event received by backend
  → Hangfire job: DownloadRecordingJob
    → Download recording from LiveKit Cloud storage URL
    → Upload to local MinIO
    → Create/update Recording entity with local FilePath
    → Emit RecordingAvailableEvent
```

## Entities

- **`Recording`**:
  - `Id`, `MeetingId`, `OrganizationId`
  - `CloudStorageUrl` (LiveKit Cloud storage URL — source)
  - `FilePath` (local MinIO path — populated after download)
  - `Duration`, `SizeBytes`
  - `Status` (Pending / Downloading / Available / Failed)
  - `CreatedAtUtc`

## Folder Structure

```text
Features/
└── Recordings/
    ├── Endpoints/
    │   └── Recording/
    │       ├── RecordingController.cs
    │       ├── GetRecordingEndpoint.cs
    │       └── ListRecordingsEndpoint.cs
    │
    ├── Models/
    │   └── Responses/
    │       ├── RecordingResponse.cs
    │       └── RecordingListResponse.cs
    │
    ├── Services/
    │   ├── IRecordingService.cs
    │   ├── RecordingService.cs
    │   ├── IStorageService.cs
    │   └── StorageService.cs
    │
    ├── Jobs/
    │   └── DownloadRecordingJob.cs
    │
    └── Validators/
```

## Controller Definitions

### RecordingController — `api/recordings`

```csharp
namespace MeetingAssistant.Features.Recordings.Endpoints.Recording;

[ApiController]
[Route("api")]
public partial class RecordingController : ControllerBase
{
    private readonly IRecordingService _recordingService;

    public RecordingController(IRecordingService recordingService)
    {
        _recordingService = recordingService;
    }
}
```

**Endpoints:**
- `GET /api/meetings/{meetingId}/recording` → `GetRecordingEndpoint.cs` (route: `meetings/{meetingId:guid}/recording`)
- `GET /api/organizations/{orgId}/recordings` → `ListRecordingsEndpoint.cs` (route: `organizations/{orgId:guid}/recordings`)

## Storage Service

- MinIO typed client
- Upload service (for any non-Egress uploads)
- Presigned URL generation (time-limited secure access)
- Recording metadata stored in DB
- Egress completion webhook → enqueue `DownloadRecordingJob`

## Domain Events

- `RecordingAvailableEvent`

## Tests (Phase 5)

- Unit: download job with mocked LiveKit Cloud storage
- Integration: presigned URL generation and expiry
- Integration: webhook → download → MinIO storage flow

Deliverable:
- Meeting recording stored in MinIO (via LiveKit Cloud Egress → download pipeline)
- Retrievable via secure presigned URL
- Works with both local Docker and future cloud deployments
- 1 controller, 2 endpoint files

---

# Phase 6 — Post-Meeting AI Pipeline + Meeting Memory (Weeks 8.5–11) (refactor require by chat gpt)

> This is the **asynchronous post-meeting pipeline**. It runs entirely via
> Hangfire background jobs after a meeting ends. No AI processing occurs
> during the request pipeline (constitution §9.2).
>
> **New in v3**: This phase now includes the **Meeting Memory** embedding
> pipeline that powers the RAG query endpoint in Phase 6.5.

## AI Pipeline Architecture

```
MeetingEndedEvent
  │
  ├──→ [Hangfire] SummarizeTranscriptJob
  │       → ⏱ Transcription Flush Ready-Check (v3.1 — see below)
  │       → Collect TranscriptSegments from DB
  │       → Retrieve Member Context from UserOrgMembership.Context
  │       → Call ILLMService for summarization
  │       → Store Summary entity
  │       → Emit SummaryGeneratedEvent
  │
  └──→ [Hangfire] (triggered by SummaryGeneratedEvent)
          │
          ├──→ ExtractTasksJob
          │       → Call ILLMService with extraction prompt
          │       → Include Member Context for assignee suggestions
          │       → Store TaskItem entities (Status = PendingReview)
          │       → Emit TasksExtractedEvent
          │
          ├──→ GenerateMeetingMemoryJob (NEW in v3)
          │       → Chunk summary content into segments
          │       → Select key transcript segments (speaker changes, decisions, action items)
          │       → Call IEmbeddingService.EmbedBatchAsync(chunks)
          │       → Store MeetingEmbedding entities (pgvector)
          │       → Emit MeetingMemoryGeneratedEvent
          │
          └──→ GenerateInsightsJob (future — placeholder)
                  → Action item trends, participation patterns, etc.
```

All LLM calls go through `ILLMService`, all embedding calls through `IEmbeddingService` — both provider-agnostic.

## Transcription Flush Ready-Check (v3.1 hardening)

**Risk**: When a meeting ends, `MeetingEndedEvent` fires immediately. However,
LiveKit Cloud may still be delivering the final transcription segments via
webhooks. If `SummarizeTranscriptJob` starts before all segments are persisted,
the summary will be generated from incomplete data.

**Mitigation**: The `SummarizeTranscriptJob` MUST perform a ready-check before
proceeding with summarization:

```csharp
// Inside SummarizeTranscriptJob.ExecuteAsync()
async Task WaitForTranscriptionFlush(Guid meetingId, CancellationToken ct)
{
    // Step 1: Initial 10-second delay — allows in-flight webhooks to arrive
    await Task.Delay(TimeSpan.FromSeconds(10), ct);

    // Step 2: Stability check — verify no new segments arrived in last 5 seconds
    var lastSegment = await _db.TranscriptSegments
        .Where(s => s.MeetingId == meetingId)
        .OrderByDescending(s => s.CreatedAtUtc)
        .Select(s => s.CreatedAtUtc)
        .FirstOrDefaultAsync(ct);

    if (lastSegment > DateTime.UtcNow.AddSeconds(-5))
    {
        // Segments still arriving — wait another 5 seconds
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }
}
```

> **Why this matters**: Without the flush delay, the LLM may summarize only
> 90% of the transcript, missing final decisions or action items spoken in the
> last seconds of the meeting. The 10-second initial delay covers typical
> LiveKit Cloud webhook delivery latency. The stability check handles edge
> cases where delivery is slower than expected.

## Member Context in Prompts (unchanged from v2)

During task extraction, the system:

1. Loads all `UserOrgMembership` records for the meeting's org (including the `Context` field)
2. Constructs a **Member Context** roster block for the LLM prompt:
   ```
   Organization Members:
   - Ahmed (backend engineer)
   - Sara (UI/UX lead)
   - Ali (DevOps and infrastructure)
   ```
3. The LLM uses this Member Context to suggest assignees for extracted tasks

> **Terminology**: This is **Member Context** — human-managed role descriptions.
> It is NOT the same as Meeting Memory (see below).

## Meeting Memory — Embedding Pipeline (new in v3)

After summarization completes, the `GenerateMeetingMemoryJob` builds the
vector store that powers cross-meeting RAG queries (UC-M3.3-003).

### What gets embedded

| Source | Chunking Strategy | Purpose |
|--------|------------------|---------|
| Summary content | One embedding per summary section (key decisions, action items, discussion points) | High-level meeting recall |
| Key transcript segments | Speaker-change boundaries + decision/action markers | Granular *"What did X say about Y?"* queries |

### Embedding flow

1. Chunk the `Summary.Content` JSONB into logical sections
2. Select significant `TranscriptSegment` runs (filtered by length, speaker changes, keywords)
3. Call `IEmbeddingService.EmbedBatchAsync(allChunks)`
4. Store each vector as a `MeetingEmbedding` row with metadata

### How RAG queries use Meeting Memory

When the Phase 6.5 endpoint `POST /api/organizations/{orgId}/meetings/ask` is called:

1. Embed the user's question via `IEmbeddingService.EmbedAsync(question)`
2. Query `MeetingEmbedding` with pgvector cosine similarity (`<=>` operator)
   - Filtered by `OrganizationId` (tenant isolation)
   - Optional filter by date range, meeting ID, or tags
   - Returns top-K results (default K=5)
3. Assemble retrieved chunks + user question into an LLM prompt
4. Call `ILLMService.CompleteAsync(ragPrompt)` for a context-aware answer

## Entities

- **`Summary`**:
  - `Id`, `MeetingId`, `OrganizationId`
  - `Content` (JSONB — structured: key decisions, action items, discussion points)
  - `Status` (Pending / Processing / Completed / Failed)
  - `CreatedAtUtc`

- **`MeetingEmbedding`** (new in v3):
  - `Id` (Guid)
  - `MeetingId`, `OrganizationId`
  - `SourceType` (enum: Summary / TranscriptSegment)
  - `SourceId` (Guid — references either Summary.Id or TranscriptSegment.Id)
  - `ChunkText` (text — the original text that was embedded, for display in RAG results)
  - `Embedding` (pgvector `vector(1536)`)
  - `CreatedAtUtc`
  - **Index**: IVFFlat or HNSW index on `Embedding` column, partitioned by `OrganizationId`

> **Base entity note**: All entities above inherit the base entity defined in
> Phase 0.3 (`CreatedAtUtc`, `UpdatedAtUtc`). `MeetingEmbedding` rows are
> write-once/immutable — `UpdatedAtUtc` is inherited but never modified.

- **`TaskItem`**:
  - `Id`, `MeetingId`, `OrganizationId`
  - `Title`, `Description`
  - `SuggestedAssigneeUserId` — LLM-suggested assignee (not confirmed)
  - `AssigneeUserId` — confirmed assignee (set after human review)
  - `DueDate`
  - `ReviewStatus` (PendingReview / Approved / Rejected / Edited)
  - `Status` (Draft / Open / InProgress / Completed / Failed)
  - `SourceSummaryId`
  - `ExternalId` (for Trello sync — populated only after approval + sync)
  - `CreatedAtUtc`

## Folder Structure

```text
Features/
└── AiPipeline/
    ├── Jobs/
    │   ├── SummarizeTranscriptJob.cs
    │   ├── ExtractTasksJob.cs
    │   ├── GenerateMeetingMemoryJob.cs
    │   └── GenerateInsightsJob.cs
    │
    ├── Models/
    │   └── Responses/
    │       └── SummaryResponse.cs
    │
    ├── Services/
    │   ├── ISummarizationService.cs
    │   ├── SummarizationService.cs
    │   ├── ITaskExtractionService.cs
    │   ├── TaskExtractionService.cs
    │   ├── IEmbeddingPipelineService.cs
    │   └── EmbeddingPipelineService.cs
    │
    └── Prompts/
        ├── SummarizationPrompt.cs
        └── TaskExtractionPrompt.cs
```

> **Note**: Phase 6 has no REST endpoints — it is purely background jobs.
> The query endpoint (RAG) is in Phase 6.5.

## SignalR AI Status Updates (constitution §IV — tenant-scoped per v3.1)

All events below MUST be sent to `Clients.Group($"org:{organizationId}")` only.

- `SummarizationStarted`
- `SummarizationCompleted`
- `SummarizationFailed`
- `TasksExtracted` (with count of items pending review)
- `MeetingMemoryGenerated` (new in v3 — embeddings stored, RAG queries now include this meeting)

## Tests (Phase 6)

- Unit: summarization job with mocked `ILLMService`
- Unit: task extraction with Member Context injection
- Unit: embedding job with mocked `IEmbeddingService`
- Unit: retry/failure state transitions
- Unit: transcription flush ready-check waits correctly and detects late segments
- Integration: full MeetingEnded → Summary → Tasks → Embeddings flow (mocked LLM + embeddings)
- Integration: verify newly embedded meeting is discoverable via pgvector similarity search
- Integration: SignalR events delivered only to correct org group (tenant isolation)

Deliverable:
- Summary generated and stored after meeting ends
- Tasks extracted with LLM-suggested assignees
- All tasks enter Review Queue (PendingReview status)
- Meeting Memory embeddings stored in pgvector
- Real-time status updates via SignalR
- No endpoint files (background jobs only)

---

# Phase 6.5 — Meeting Memory Query: RAG Endpoint (Week 11–11.5) (refactor require by chat gpt)

> This phase implements the **query side** of Meeting Memory. The embedding
> pipeline (write side) was built in Phase 6. This endpoint uses the **hybrid
> sync/async pattern** to comply with constitution §9.2.

## Folder Structure

```text
Features/
└── AiPipeline/
    ├── Endpoints/
    │   └── MeetingMemory/
    │       ├── MeetingMemoryController.cs
    │       └── AskMeetingMemoryEndpoint.cs
    │
    ├── Models/
    │   ├── Requests/
    │   │   └── AskMeetingMemoryRequest.cs
    │   └── Responses/
    │       └── AskMeetingMemoryResponse.cs
    │
    ├── Services/
    │   ├── IRagQueryService.cs
    │   └── RagQueryService.cs
    │
    ├── Jobs/
    │   └── RagQueryJob.cs
    │
    └── Validators/
        └── AskMeetingMemoryRequestValidator.cs
```

## Controller Definition

### MeetingMemoryController — `api/organizations/{orgId}/meetings`

```csharp
namespace MeetingAssistant.Features.AiPipeline.Endpoints.MeetingMemory;

[ApiController]
[Route("api/organizations/{orgId:guid}/meetings")]
public partial class MeetingMemoryController : ControllerBase
{
    private readonly IRagQueryService _ragQueryService;

    public MeetingMemoryController(IRagQueryService ragQueryService)
    {
        _ragQueryService = ragQueryService;
    }
}
```

**Endpoints:**
- `POST /api/organizations/{orgId}/meetings/ask` → `AskMeetingMemoryEndpoint.cs`

## Hybrid Sync/Async Pattern (refined in v3.1)

The RAG query involves AI calls (`IEmbeddingService` + `ILLMService`), which
normally must be async per constitution §9.2. This endpoint uses a hybrid
approach with an **explicit `CancellationTokenSource` timeout**:

### AskMeetingMemoryEndpoint Implementation

```csharp
// AskMeetingMemoryEndpoint.cs
namespace MeetingAssistant.Features.AiPipeline.Endpoints.MeetingMemory;

public partial class MeetingMemoryController
{
    /// <summary>
    /// Query Meeting Memory using RAG. Attempts synchronous response within 3s,
    /// falls back to async delivery via Hangfire + SignalR.
    /// </summary>
    [HttpPost("ask")]
    [ProducesResponseType(typeof(AskMeetingMemoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AskMeetingMemoryResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> AskMeetingMemory(
        [FromRoute] Guid orgId,
        [FromBody] AskMeetingMemoryRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _ragQueryService.QueryAsync(
            orgId, request, cancellationToken);

        return result.Match<IActionResult>(
            sync => Ok(sync),
            async => Accepted(value: async)
        );
    }
}
```

### RagQueryService Implementation Detail (v3.1)

```csharp
// Inside RagQueryService.QueryAsync()
public async Task<OneOf<AskMeetingMemoryResponse, AskMeetingMemoryResponse>> QueryAsync(
    Guid orgId, AskMeetingMemoryRequest request, CancellationToken requestCt)
{
    // 3-second timeout — races against the AI pipeline
    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    using var linkedCts = CancellationTokenSource
        .CreateLinkedTokenSource(requestCt, timeoutCts.Token);

    try
    {
        // Step 1: Embed the question
        var questionVector = await _embeddingService.EmbedAsync(
            request.Question, linkedCts.Token);

        // Step 2: pgvector similarity search (scoped by OrganizationId)
        var chunks = await SearchSimilarChunks(
            orgId, questionVector, topK: 5, linkedCts.Token);

        // Step 3: LLM completion with retrieved context
        var answer = await _llmService.CompleteAsync(
            BuildRagPrompt(request.Question, chunks), linkedCts.Token);

        // Completed within 3 seconds — return synchronously
        return new AskMeetingMemoryResponse(answer, chunks, Mode: "sync");
    }
    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
    {
        // Timeout hit — fall back to async delivery
        var jobId = _backgroundJobClient.Enqueue<RagQueryJob>(
            job => job.ExecuteAsync(orgId, request, CancellationToken.None));

        return new AskMeetingMemoryResponse(null, null, Mode: "async", JobId: jobId);
    }
}
```

### RagQueryJob (Hangfire)

```csharp
// RagQueryJob — completes the query and delivers via SignalR
public class RagQueryJob(IEmbeddingService embedding, ILLMService llm,
    IHubContext<MeetingHub> hub)
{
    public async Task ExecuteAsync(Guid orgId, AskMeetingMemoryRequest request, CancellationToken ct)
    {
        var vector = await embedding.EmbedAsync(request.Question, ct);
        var chunks = await SearchSimilarChunks(orgId, vector, topK: 5, ct);
        var answer = await llm.CompleteAsync(BuildRagPrompt(request.Question, chunks), ct);

        // Deliver ONLY to the user's organization group — never broadcast
        await hub.Clients.Group($"org:{orgId}")
            .SendAsync("MeetingMemoryQueryCompleted",
                new { JobId, Answer = answer, Sources = chunks }, ct);
    }
}
```

### Flow Summary

```
User asks question via POST /api/organizations/{orgId}/meetings/ask
  → Controller delegates to RagQueryService
  → Create CancellationTokenSource with 3-second timeout
  → Link with request CancellationToken
  → TRY:
      → Service calls IEmbeddingService.EmbedAsync(question)
      → pgvector similarity search (scoped by OrganizationId)
      → Returns top-K relevant chunks
      → Call ILLMService.CompleteAsync(prompt + retrieved chunks)
      → Return 200 OK with answer (synchronous) ✓
  → CATCH OperationCanceledException (timeout):
      → Enqueue RagQueryJob in Hangfire
      → Return 202 Accepted with jobId
      → Hangfire job completes query
      → Deliver answer via SignalR to org:{OrganizationId} group ONLY
```

> **Constitution §9.2 compliance**: Heavy AI processing (summarization, task
> extraction, batch embedding generation) remains strictly async via Hangfire.
> The RAG query uses the hybrid exception because it is a lightweight read
> (single embedding + short completion). The `CancellationTokenSource` with
> 3-second timeout ensures the endpoint never blocks indefinitely. The linked
> token also respects client disconnect (`requestCt`).

## RAG Flow Detail

1. Embed the user's question via `IEmbeddingService.EmbedAsync(question)`
2. Query `MeetingEmbedding` with pgvector cosine similarity (`<=>` operator)
   - Filtered by `OrganizationId` (tenant isolation)
   - Optional filters: date range, meeting ID, tags
   - Returns top-K results (default K=5)
3. Assemble retrieved chunks + user question into an LLM prompt
4. Call `ILLMService.CompleteAsync(ragPrompt)` for a context-aware answer
5. If within timeout → return inline. If not → enqueue remainder as Hangfire job

## SignalR Events (tenant-scoped per v3.1)

- `MeetingMemoryQueryCompleted` → sent to `Clients.Group($"org:{organizationId}")` only (never broadcast)
  - Delivered only when async fallback is triggered (timeout exceeded)

## Tests (Phase 6.5)

- Unit: RAG flow with mocked `IEmbeddingService` + `ILLMService`
- Unit: hybrid timeout — `CancellationTokenSource(3s)` triggers async fallback correctly
- Unit: linked token respects client disconnect (`requestCt` cancellation)
- Unit: `RagQueryJob` sends SignalR event to correct org group
- Integration: similarity search returns relevant chunks from Phase 6 data
- Integration: async fallback delivers result via SignalR to org group only
- Integration: verify no SignalR leakage — other org connections do NOT receive the event

Deliverable:
- Meeting Memory RAG endpoint functional
- Hybrid sync/async pattern respects constitution §9.2
- Query scoped by OrganizationId (tenant isolation)
- Filters supported (date range, meeting, tags)
- 1 controller, 1 endpoint file

---

# Phase 7 — Task Review Queue & Management (Weeks 11.5–13)

> **Human-in-the-loop**: AI-generated tasks are NOT automatically synced.
> Users review, edit, approve, or reject tasks before any external integration.

## Folder Structure

```text
Features/
└── Tasks/
    ├── Endpoints/
    │   ├── ReviewQueue/
    │   │   ├── ReviewQueueController.cs
    │   │   ├── ListPendingReviewEndpoint.cs
    │   │   ├── ApproveTaskEndpoint.cs
    │   │   ├── RejectTaskEndpoint.cs
    │   │   ├── EditAndApproveTaskEndpoint.cs
    │   │   └── BulkApproveEndpoint.cs
    │   │
    │   ├── Task/
    │   │   ├── TaskController.cs
    │   │   ├── ListTasksEndpoint.cs
    │   │   ├── GetTaskEndpoint.cs
    │   │   ├── UpdateTaskEndpoint.cs
    │   │   ├── CompleteTaskEndpoint.cs
    │   │   └── DeleteTaskEndpoint.cs
    │   │
    │   └── Reminder/
    │       ├── ReminderController.cs
    │       └── CreateReminderEndpoint.cs
    │
    ├── Models/
    │   ├── Requests/
    │   │   ├── ApproveTaskRequest.cs
    │   │   ├── RejectTaskRequest.cs
    │   │   ├── EditAndApproveTaskRequest.cs
    │   │   ├── UpdateTaskRequest.cs
    │   │   └── CreateReminderRequest.cs
    │   └── Responses/
    │       ├── TaskResponse.cs
    │       ├── TaskListResponse.cs
    │       ├── ReviewQueueResponse.cs
    │       └── ReminderResponse.cs
    │
    ├── Services/
    │   ├── IReviewQueueService.cs
    │   ├── ReviewQueueService.cs
    │   ├── ITaskService.cs
    │   ├── TaskService.cs
    │   ├── IReminderService.cs
    │   └── ReminderService.cs
    │
    ├── Jobs/
    │   └── TriggerReminderJob.cs
    │
    └── Validators/
        ├── ApproveTaskRequestValidator.cs
        ├── RejectTaskRequestValidator.cs
        ├── EditAndApproveTaskRequestValidator.cs
        ├── UpdateTaskRequestValidator.cs
        └── CreateReminderRequestValidator.cs
```

## Controller Definitions

### ReviewQueueController — `api/organizations/{orgId}/tasks/review`

```csharp
namespace MeetingAssistant.Features.Tasks.Endpoints.ReviewQueue;

[ApiController]
[Route("api")]
public partial class ReviewQueueController : ControllerBase
{
    private readonly IReviewQueueService _reviewQueueService;

    public ReviewQueueController(IReviewQueueService reviewQueueService)
    {
        _reviewQueueService = reviewQueueService;
    }
}
```

**Endpoints:**
- `GET /api/organizations/{orgId}/tasks/review` → `ListPendingReviewEndpoint.cs` (route: `organizations/{orgId:guid}/tasks/review`)
- `POST /api/tasks/{id}/approve` → `ApproveTaskEndpoint.cs` (route: `tasks/{id:guid}/approve`)
- `POST /api/tasks/{id}/reject` → `RejectTaskEndpoint.cs` (route: `tasks/{id:guid}/reject`)
- `POST /api/tasks/{id}/edit` → `EditAndApproveTaskEndpoint.cs` (route: `tasks/{id:guid}/edit`)
- `POST /api/meetings/{meetingId}/tasks/approve-all` → `BulkApproveEndpoint.cs` (route: `meetings/{meetingId:guid}/tasks/approve-all`)

### TaskController — `api/tasks`

```csharp
namespace MeetingAssistant.Features.Tasks.Endpoints.Task;

[ApiController]
[Route("api")]
public partial class TaskController : ControllerBase
{
    private readonly ITaskService _taskService;

    public TaskController(ITaskService taskService)
    {
        _taskService = taskService;
    }
}
```

**Endpoints:**
- `GET /api/organizations/{orgId}/tasks` → `ListTasksEndpoint.cs` (route: `organizations/{orgId:guid}/tasks`)
- `GET /api/tasks/{id}` → `GetTaskEndpoint.cs` (route: `tasks/{id:guid}`)
- `PUT /api/tasks/{id}` → `UpdateTaskEndpoint.cs` (route: `tasks/{id:guid}`)
- `POST /api/tasks/{id}/complete` → `CompleteTaskEndpoint.cs` (route: `tasks/{id:guid}/complete`)
- `DELETE /api/tasks/{id}` → `DeleteTaskEndpoint.cs` (route: `tasks/{id:guid}`)

### ReminderController — `api/tasks/{taskId}/reminders`

```csharp
namespace MeetingAssistant.Features.Tasks.Endpoints.Reminder;

[ApiController]
[Route("api/tasks/{taskId:guid}/reminders")]
public partial class ReminderController : ControllerBase
{
    private readonly IReminderService _reminderService;

    public ReminderController(IReminderService reminderService)
    {
        _reminderService = reminderService;
    }
}
```

**Endpoints:**
- `POST /api/tasks/{taskId}/reminders` → `CreateReminderEndpoint.cs`

## Review Flow

```
TaskItem created (Status=Draft, ReviewStatus=PendingReview)
  │
  ├──→ User approves → ReviewStatus=Approved, Status=Open
  │       → AssigneeUserId set (from suggestion or override)
  │       → Emit TaskApprovedEvent (triggers sync if integration exists)
  │
  ├──→ User edits → ReviewStatus=Edited, Status=Open
  │       → Updated fields saved
  │       → Emit TaskApprovedEvent
  │
  └──→ User rejects → ReviewStatus=Rejected, Status=Rejected
          → Emit TaskRejectedEvent
          → Task retained for audit but excluded from active lists
```

## Entities

- **`Reminder`**:
  - `Id`, `TaskItemId`, `OrganizationId`, `UserId`
  - `ReminderAtUtc`
  - `Status` (Pending / Sent / Failed)
  - `CreatedAtUtc`

## Reminder System

- Hangfire delayed job fires at `ReminderAtUtc`
- Push via SignalR — MUST use `Clients.Group($"org:{organizationId}")` (tenant-scoped, never `Clients.All`)

## Domain Events

- `TaskApprovedEvent`
- `TaskRejectedEvent`
- `TaskCompletedEvent`
- `ReminderCreatedEvent`
- `ReminderTriggeredEvent`

## Tests (Phase 7)

- Unit: review state machine (Draft → Approved/Rejected/Edited)
- Unit: bulk approve logic
- Unit: reminder scheduling
- Integration: full review flow, reminder firing

Deliverable:
- Review Queue functional
- Tasks only become active after human approval
- Reminders work for approved tasks
- 3 controllers, 11 endpoint files

---

# Phase 8 — Trello Integration (Weeks 13–14)

> Sync only triggers for **approved** tasks. Draft/PendingReview tasks are never
> pushed to external platforms.

## Entities

- **`PlatformIntegration`** (org-level: OAuth tokens for Trello)
  - `Id`, `OrganizationId`
  - `Platform` (enum: Trello — extensible)
  - `AccessToken` (encrypted), `RefreshToken` (encrypted)
  - `ExternalWorkspaceId` (Trello board ID)
  - `Status` (Active / Revoked / Failed)
  - `CreatedAtUtc`
- **`IntegrationMapping`** (TaskItem ↔ Trello card ID)
  - `Id`, `TaskItemId`, `OrganizationId`, `PlatformIntegrationId`
  - `ExternalId` (Trello card ID)
  - `LastSyncedAtUtc`, `CreatedAtUtc`

## Folder Structure

```text
Features/
└── Integrations/
    ├── Endpoints/
    │   └── Trello/
    │       ├── TrelloController.cs
    │       ├── TrelloOAuthEndpoint.cs
    │       ├── TrelloOAuthCallbackEndpoint.cs
    │       ├── ManualSyncEndpoint.cs
    │       └── TrelloWebhookEndpoint.cs
    │
    ├── Models/
    │   ├── Requests/
    │   │   └── ManualSyncRequest.cs
    │   └── Responses/
    │       ├── TrelloOAuthResponse.cs
    │       └── SyncStatusResponse.cs
    │
    ├── Services/
    │   ├── IExternalTaskSyncService.cs
    │   ├── TrelloSyncService.cs
    │   ├── ITrelloApiClient.cs
    │   └── TrelloApiClient.cs
    │
    ├── Jobs/
    │   ├── SyncTaskToTrelloJob.cs
    │   └── UpdateTrelloCardJob.cs
    │
    └── Validators/
        └── ManualSyncRequestValidator.cs
```

## Controller Definitions

### TrelloController — `api/organizations/{orgId}/integrations/trello`

```csharp
namespace MeetingAssistant.Features.Integrations.Endpoints.Trello;

[ApiController]
[Route("api/organizations/{orgId:guid}/integrations/trello")]
public partial class TrelloController : ControllerBase
{
    private readonly IExternalTaskSyncService _syncService;

    public TrelloController(IExternalTaskSyncService syncService)
    {
        _syncService = syncService;
    }
}
```

**Endpoints:**
- `GET /api/organizations/{orgId}/integrations/trello/oauth` → `TrelloOAuthEndpoint.cs` (route: `oauth`)
- `GET /api/organizations/{orgId}/integrations/trello/oauth/callback` → `TrelloOAuthCallbackEndpoint.cs` (route: `oauth/callback`)
- `POST /api/organizations/{orgId}/integrations/trello/sync` → `ManualSyncEndpoint.cs` (route: `sync`)
- `POST /api/organizations/{orgId}/integrations/trello/webhook` → `TrelloWebhookEndpoint.cs` (route: `webhook`)

## Integration Architecture

- `IExternalTaskSyncService` interface (adapter pattern)
- `TrelloSyncService : IExternalTaskSyncService` — concrete implementation
- Any future integration (ClickUp, Jira, etc.) implements the same interface

## Trello Integration

- OAuth flow endpoints for Trello authorization
- `TaskApprovedEvent` → Hangfire job → create Trello card (if org has Trello integration)
- `TaskCompletedEvent` → Hangfire job → update Trello card status
- Manual resync endpoint
- Webhook receiver for Trello card updates (bidirectional sync)

## Domain Events

- `TaskSyncedEvent`
- `TaskSyncFailedEvent`

## SignalR Notifications (tenant-scoped — v3.2)

- `TaskSyncCompleted` → sent to `Clients.Group($"org:{organizationId}")` when Trello card created/updated successfully
- `TaskSyncFailed` → sent to `Clients.Group($"org:{organizationId}")` when Trello sync fails (with error detail)

## Tests (Phase 8)

- Unit: Trello sync with mocked API
- Unit: adapter pattern contract tests
- Integration: approve task → Trello card created
- Integration: sync status delivered via SignalR to correct org group only

Deliverable:
- Trello integration working for approved tasks only
- Adapter pattern ready for future integrations
- 1 controller, 4 endpoint files

---

# Phase 9 — Integration Testing & Hardening (Weeks 14–15)

## End-to-End Flow Tests

1. Register → create org → set Member Context → create meeting → start meeting
2. Live transcription flows via LiveKit Cloud → end meeting
3. Post-meeting pipeline: summary generated → tasks extracted (with assignee suggestions) → Meeting Memory embeddings stored
4. RAG query: ask *"What did we discuss about Flutter?"* → retrieve relevant past meeting content → LLM-generated answer
5. User reviews tasks in Review Queue → approves some, rejects others
6. Approved task synced to Trello
7. Reminder scheduled → reminder triggers (SignalR)

## Security & Compliance Audit

- Tenant isolation audit: verify every query filtered by `OrganizationId`
- **SignalR tenant safety audit** (v3.4): verify no `Clients.All` usage anywhere in codebase; all hub sends use `Clients.Group($"org:{orgId}")`
- **SignalR group membership audit**: verify on-connect logic reads `organizationId` from JWT and adds to exactly one group
- **Membership constraint audit**: verify `UNIQUE(user_id) WHERE is_enabled = true` on `UserOrgMembership`
- Secrets audit: no hardcoded keys (JWT, LiveKit Cloud, Trello OAuth, MinIO)
- JWT key rotation verification
- Refresh token rotation verification
- LiveKit Cloud API key rotation verification

## Partial Controller Pattern Audit (v3.3)

- Verify every endpoint file contains exactly ONE action method
- Verify no endpoint file contains constructor, `[ApiController]`, `[Route]`, base class, or field declarations
- Verify namespace matches folder path for all partial classes
- Verify all async actions accept `CancellationToken` as last parameter
- Verify all controllers stay thin — no business logic in controller methods
- Verify controller grouping: no controller exceeds 5 endpoints

## Performance

- Load test key endpoints (target: <300ms p95, excluding background jobs)
- Verify no blocking calls in async methods
- Verify no synchronous LLM calls in request pipeline (except hybrid RAG endpoint — see Phase 6.5)
- Redis distributed locking for concurrent Hangfire job safety

## Logging Verification

- Correlation IDs present on all requests
- All domain events logged
- AI job lifecycle logged (start/complete/fail for each pipeline stage)
- No sensitive data in logs

Deliverable:
- Stable system with tested core flows
- Constitution compliance verified
- Partial Controller Pattern compliance verified

---

# Phase 10 — Demo Preparation (Weeks 15–16)

## Demo Flow (API-only — Postman/HTTP collection)

1. Register user
2. Create organization + set Member Context ("Ahmed — backend engineer", etc.)
3. Invite member
4. Create meeting
5. Get LiveKit Cloud join token → join meeting
6. Live transcription active (via LiveKit Cloud)
7. End meeting
8. Post-meeting AI pipeline runs (SignalR status updates)
9. Summary generated with structured content
10. Meeting Memory embeddings stored (pgvector)
11. Tasks extracted with LLM-suggested assignees (based on Member Context)
12. **Meeting Memory query**: ask *"What did we say about Flutter?"* → RAG answer
13. Review Queue: approve/edit/reject tasks
14. Approved task synced to Trello
15. Reminder scheduled → reminder triggers (SignalR)

## Documentation

- Architecture diagram (showing LiveKit Cloud boundary vs local Docker services)
- **Pipeline diagram** (realtime pipeline vs post-meeting AI pipeline, including RAG flow)
- Event flow diagram
- ERD (all entities including ReviewStatus, MeetingEmbedding, Member Context fields)
- Deployment diagram (Docker Compose + LiveKit Cloud)
- AI orchestration flow (transcript → summarization → extraction → embedding)
- **RAG architecture diagram** (Meeting Memory query flow)
- **Hybrid RAG Sequence diagram** (v3.1) — shows the 3-second `CancellationTokenSource` timeout logic: sync path (200 OK) vs async fallback (202 Accepted → Hangfire → SignalR delivery)
- **SignalR Tenant Safety diagram** (v3.1) — shows how users are added to `org:{OrganizationId}` groups on connection, and how all background job notifications (AI status, task events, RAG answers) are scoped to groups, preventing cross-tenant data leaks
- **Transcription Flush Sequence diagram** (v3.1) — shows the 10-second delay + stability check in `SummarizeTranscriptJob` before LLM processing begins
- **Partial Controller Pattern reference** (v3.3) — shows controller grouping rules, file naming conventions, and endpoint-per-file examples. Includes anti-pattern list
- LLM + Embedding abstraction documentation (`ILLMService` + `IEmbeddingService` provider swap guide)
- API collection (Postman / `.http` files)

## Endpoint Summary (v3.4)

| Phase | Feature | Controllers | Endpoint Files | Total Actions |
|-------|---------|-------------|----------------|---------------|
| 1 | Identity | 3 (Auth, Token, Profile) | 8 | 8 |
| 2 | Organizations | 3 (Organization, Member, Invitation) | 7 | 7 |
| 3 | Meetings | 4 (Meeting, Recurring, Participant, Calendar) | 7 | 7 |
| 4 | LiveSession | 3 (Session, Webhook, Transcript) | 3 | 3 |
| 5 | Recordings | 1 (Recording) | 2 | 2 |
| 6 | AI Pipeline | 0 (background jobs only) | 0 | 0 |
| 6.5 | Meeting Memory | 1 (MeetingMemory) | 1 | 1 |
| 7 | Tasks | 3 (ReviewQueue, Task, Reminder) | 11 | 11 |
| 8 | Integrations | 1 (Trello) | 4 | 4 |
| **Total** | | **19 controllers** | **43 endpoint files** | **43 actions** |

Deliverable:
- Complete runnable demo via API
- Full documentation package

---

# Constitution Compliance Check

This section verifies the plan's compliance with constitution v1.3.5.

| Constitution Rule | Status | Notes |
|-------------------|--------|-------|
| §I Single deployable unit | ✅ Compliant | Only .NET backend deployed. LiveKit Cloud is external managed service. |
| §I Feature-based modular structure | ✅ Compliant | `src/Features/`, `src/Infrastructure/`, `src/Shared/`, `Program.cs` |
| §I Tech stack (non-negotiable) | ✅ Compliant | All technologies match constitution. pgvector active for Meeting Memory. |
| §I Endpoint architecture | ✅ Compliant | v3.3: ASP.NET Controllers with Partial Controller Pattern. One endpoint per file via partial classes. No Minimal APIs. |
| §II Multi-tenancy (`OrganizationId`) | ✅ Compliant | All org-scoped entities include `OrganizationId` (v3.2: `IntegrationMapping`, `Meeting`, `MeetingParticipant` fixed). Global query filter in `AppDbContext`. |
| §II Multi-tenancy (SignalR) | ✅ Compliant | v3.2: All SignalR events scoped to `org:{OrganizationId}` Groups across all phases (incl. Phase 7 reminders, Phase 8 sync status). No `Clients.All` usage. |
| §II Two-level role model | ✅ Compliant | Org roles (Admin/Member/Guest) + Meeting roles (Host/CoHost/Participant/Observer) |
| §II State separation (persistent vs ephemeral) | ✅ Compliant | No media state in PostgreSQL/Redis. LiveKit manages ephemeral state. |
| §III Domain events for cross-feature | ✅ Compliant | Events defined in every phase. No direct cross-feature service calls. |
| §III Hangfire for long-running ops | ✅ Compliant | Summarization, task extraction, embedding, recording download, Trello sync, RAG fallback all via Hangfire. |
| §III Retry policy (3 retries, Failed state) | ✅ Compliant | Phase 0.3 scaffolding. `Failed` state on all relevant entities. |
| §IV LiveKit Cloud responsibilities | ✅ Compliant | Backend does not proxy media. Token-based auth with role permissions. |
| §IV SignalR for notifications only | ✅ Compliant | No media over SignalR. Used for status updates, reminders, sync status, async RAG delivery. |
| §IV SignalR group membership | ✅ Compliant | v3.4: Hub reads `organizationId` from JWT on connect, adds to one group. No multi-org DB query needed. |
| §V JWT 15-min / refresh 7-day | ✅ Compliant | Phase 1 implements exact spec. |
| §V Secrets management | ✅ Compliant | user-secrets (dev), env vars (Docker). No hardcoded keys. |
| Operational — Correlation IDs | ✅ Compliant | Phase 0.3 middleware. Verified in Phase 9. |
| Operational — No sync AI in request pipeline | ✅ Compliant | Heavy AI strictly async. RAG uses `CancellationTokenSource(3s)` hybrid with Hangfire fallback. |
| Operational — <300ms API responses | ✅ Compliant | Phase 9 load tests target <300ms p95. RAG hybrid has explicit 3s max. |
| Operational — DI, FluentValidation, CancellationToken, UTC | ✅ Compliant | Phase 0.3 scaffolding enforces all coding standards. |
| Operational — Partial Controller Pattern | ✅ Compliant | v3.3: All endpoints follow one-file-per-action pattern. Phase 9 includes pattern audit. |
| Data pipeline integrity | ✅ Compliant | v3.1: Transcription flush ready-check ensures complete data before AI processing. |

> **No violations found**. v3.3 applies the Partial Controller Pattern across all phases.

---

# Estimated Timeline (Solo Developer)

| Phase | Duration | Weeks |
|-------|----------|-------|
| 0. Infrastructure & Foundation | 2 weeks | 1–2 |
| 1. Identity | 1 week | 2–3 |
| 2. Organizations (+ Member Context) | 1 week | 3–4 |
| 3. Meetings | 2 weeks | 4–6 |
| 4. LiveKit Cloud & Realtime Pipeline (+ Live Transcription) | 1.5 weeks | 6–7.5 |
| 5. Recording & Storage | 1 week | 7.5–8.5 |
| 6. Post-Meeting AI Pipeline + Meeting Memory | 2.5 weeks | 8.5–11 |
| 6.5. Meeting Memory Query (RAG) | 0.5 weeks | 11–11.5 |
| 7. Task Review Queue & Management | 1.5 weeks | 11.5–13 |
| 8. Trello Integration | 1 week | 13–14 |
| 9. Testing & Hardening | 1 week | 14–15 |
| 10. Demo Preparation | 1 week | 15–16 |

**Total estimated duration: 15–16 weeks** (unchanged — Partial Controller Pattern is a structural change, not a scope change)

---

# Partial Controller Pattern — Best Practices Reference (v3.3)

## ✅ DO

| Practice | Why |
|----------|-----|
| **One action per file** | Maximum readability; git diffs show only the changed endpoint |
| **Match namespace to folder path** | Required for partial classes to compile; keeps navigation predictable |
| **Keep controllers thin** | Delegate to services immediately. No business logic in controllers |
| **Use `CancellationToken` as the last parameter** | Enables graceful request cancellation on all async paths |
| **Use `[ProducesResponseType]`** | Accurate Swagger/OpenAPI documentation |
| **Use `sealed record` for requests/responses** | Immutable, concise, good for serialization |
| **Place the controller definition file first alphabetically** | Convention: `AuthController.cs` sorts before `LoginEndpoint.cs` |
| **Use `[FromBody]`, `[FromRoute]`, `[FromQuery]` explicitly** | Eliminates model binding ambiguity |
| **Split when a controller exceeds ~5 endpoints** | Keeps cognitive load low per file group |
| **One primary service per controller (preferred)** | If a controller needs two services, consider splitting |
| **Use subfolder per controller** | Place definition and endpoint files in the same subfolder |
| **Use `result.ToProblem(correlationIdProvider)` for error responses** | Converts a failed `Result` into an `ObjectResult` with `StandardErrorResponse` including `CorrelationId`. Avoids repetitive error construction in every endpoint |

## ❌ DON'T

| Anti-pattern | Problem |
|--------------|---------|
| **Constructor in endpoint files** | Won't compile — partial class can only have one constructor definition |
| **`[ApiController]` or `[Route]` in endpoint files** | Duplicates attributes; causes route conflicts |
| **Field declarations in endpoint files** | Dependencies belong in the controller definition only |
| **Multiple actions in one endpoint file** | Defeats the purpose of vertical slicing |
| **Business logic in controllers** | Violates separation of concerns; makes testing harder |
| **Catching exceptions in controllers** | Use middleware or filters for cross-cutting error handling |
| **Manually constructing `StandardErrorResponse` in endpoints** | Use `result.ToProblem(correlationIdProvider)` instead — keeps endpoints clean and error mapping consistent |
| **Mixing Minimal API and Controller patterns** | Pick one for consistency — this project uses Controllers |

## 🧪 Testing Strategy

Each endpoint file maps to one test class:

```text
Tests/
└── Features/
    └── Identity/
        └── Endpoints/
            ├── Auth/
            │   ├── LoginEndpointTests.cs
            │   └── RegisterEndpointTests.cs
            └── Token/
                └── RefreshTokenEndpointTests.cs
```

---

# Decisions & Changes

## v3.4 Changes — Simplified Membership Model (2026-03-13)

| # | Change | Detail |
|---|--------|--------|
| 81 | Simplified membership model | Each user may have only one active `UserOrgMembership` at a time. DB enforces `UNIQUE(user_id) WHERE is_enabled = true`. Replaces multi-org model. |
| 82 | `UserOrganization` renamed to `UserOrgMembership` | All references updated across the plan. Entity now includes: `UserId`, `OrganizationId`, `OrgRole`, `JobRole`, `Context`, `ContextStatus`, `IsEnabled`. |
| 83 | Registration requires organization | Two flows: (A) register + create org (Admin), (B) register via invitation (Member). No standalone registration allowed. |
| 84 | JWT includes `organizationId` | Access tokens now include `userId` + `organizationId` claims. `organizationId` resolved from active `UserOrgMembership` during login. |
| 85 | SignalR hub simplified | Hub reads `organizationId` from JWT on connect, adds to exactly one group. No DB query for multiple memberships. |
| 86 | `RegisterWithInviteEndpoint` added | New endpoint: `POST /api/auth/register/invite` for Scenario B registration. Phase 1 now has 8 endpoint files. |
| 87 | `LeaveOrganizationEndpoint` added | New endpoint: `POST /api/organizations/{orgId}/members/leave`. Deactivates `UserOrgMembership`. Phase 2 now has 7 endpoint files. |
| 88 | Invitation flow: membership check | Invitations now block users who already have an active membership. Error: "You already belong to an organization." |
| 89 | `ITenantProvider` resolved from JWT | Tenant resolution reads `organizationId` directly from JWT claim. No request-time DB query needed. |
| 90 | Constitution bumped to v1.4.0 | Added simplified membership model, registration model, JWT `organizationId` requirement, and JWT-based SignalR resolution. |

## v3.3 Changes — Partial Controller Pattern (2026-03-09)

| # | Change | Detail |
|---|--------|--------|
| 80 | `ToProblem()` now includes `CorrelationId` | `ResultExtensions.ToProblem()` updated to accept `ICorrelationIdProvider` parameter. All endpoint error responses now include `CorrelationId` from middleware. Constitution bumped to v1.3.5. |
| 79 | Constitution bumped to v1.3.4 | Added `result.ToProblem()` error response rule to §I Endpoint Architecture. Endpoints MUST use `ToProblem()` for business failures; MUST NOT manually construct `StandardErrorResponse`. |
| 77 | `ResultExtensions.ToProblem()` helper added | New extension method in `Shared/ResultExtensions.cs` converts failed `Result` objects into `ObjectResult` containing `StandardErrorResponse` with `CorrelationId`. Endpoints use `return result.ToProblem(correlationIdProvider);` instead of manually constructing error responses. Throws `InvalidOperationException` if called on a successful result. Global exception middleware unchanged. |
| 78 | Endpoint error pattern updated | Login endpoint example and best practices updated to use `result.ToProblem(correlationIdProvider)` pattern. DO: use `result.ToProblem(correlationIdProvider)`. DON'T: manually construct `StandardErrorResponse` in endpoints. |
| 59 | Minimal API → Partial Controller Pattern | All endpoints across all phases converted from Minimal APIs to ASP.NET Controllers using the Partial Controller Pattern. One endpoint per file via partial classes. |
| 60 | Endpoint architecture section added | New top-level section documenting the Partial Controller Pattern: rules, naming conventions, DO/DON'T lists. |
| 61 | Phase 0.2: folder structure updated | Project structure now shows feature-based `Endpoints/` subfolders with controller groupings for all features. |
| 62 | Phase 0.2: Mapster added | Object mapping package added to support controller-based DTO mapping. |
| 63 | Phase 1: Identity controllers defined | 3 controllers (Auth, Token, Profile) replacing 7 Minimal API endpoints. 7 endpoint files. Full folder structure documented. |
| 64 | Phase 2: Organization controllers defined | 3 controllers (Organization, Member, Invitation) replacing 6 Minimal API endpoints. 6 endpoint files. Full folder structure documented. |
| 65 | Phase 3: Meeting controllers defined | 4 controllers (Meeting, Recurring, Participant, Calendar) replacing 8 Minimal API endpoints. 7 endpoint files. Full folder structure documented. |
| 66 | Phase 4: LiveSession controllers defined | 3 controllers (Session, Webhook, Transcript) replacing 3 Minimal API endpoints. 3 endpoint files. Full folder structure documented. |
| 67 | Phase 5: Recording controller defined | 1 controller (Recording) replacing 2 Minimal API endpoints. 2 endpoint files. Full folder structure documented. |
| 68 | Phase 6.5: MeetingMemory controller defined | 1 controller (MeetingMemory) replacing 1 Minimal API endpoint. RAG hybrid logic moved from endpoint to service layer. |
| 69 | Phase 7: Task controllers defined | 3 controllers (ReviewQueue, Task, Reminder) replacing 12 Minimal API endpoints. 11 endpoint files. Full folder structure documented. |
| 70 | Phase 8: Trello controller defined | 1 controller (Trello) replacing 4 Minimal API endpoints. 4 endpoint files. Full folder structure documented. |
| 71 | Phase 9: Partial Controller audit added | New audit checklist verifying pattern compliance: one action per file, no duplicate attributes, namespace matching, controller size limits. |
| 72 | Phase 10: endpoint summary table added | Summary table showing 19 controllers and 41 endpoint files across all phases. |
| 73 | Phase 10: pattern documentation added | Partial Controller Pattern reference added to Phase 10 documentation deliverables. |
| 74 | Best practices reference section added | Full DO/DON'T table and testing strategy for the Partial Controller Pattern. |
| 75 | Constitution bumped to v1.3.3 | Added endpoint architecture rule: ASP.NET Controllers with Partial Controller Pattern. |
| 76 | Compliance table: endpoint architecture row added | New row verifying Partial Controller Pattern compliance across all phases. |

## v3.2 Changes — Consistency Pass (2026-03-06)

| # | Change | Detail |
|---|--------|--------|
| 45 | `IntegrationMapping`: added `OrganizationId` | Required for `AppDbContext` global query filter tenant isolation (constitution §II). Without it, direct queries bypass tenant isolation. |
| 46 | Phase 7 Reminder: explicit SignalR tenant scoping | Reminder push notifications MUST use `Clients.Group($"org:{organizationId}")`. Made explicit for consistency with v3.1 mandate across all phases. |
| 47 | Phase 6.5 header: week range corrected | `(Week 11)` → `(Week 11–11.5)` to match timeline table. |
| 48 | Phase 8: SignalR sync status notifications | Added `TaskSyncCompleted` and `TaskSyncFailed` SignalR events (tenant-scoped) so users are notified of Trello sync outcomes. |
| 49 | `Organization` entity: fields documented | Added `Id`, `Name`, `Slug`, `CreatedAtUtc` to Phase 2 entity definition. Previously the only undocumented entity. |
| 50 | SignalR group membership: JWT-based resolution | On connect, hub reads `organizationId` from JWT claim. v3.4 simplified to single group per user (single active membership model). Constitution §IV updated to v1.4.0. |
| 51 | `Meeting` entity: field list documented | Added `Id`, `OrganizationId`, `Title`, `Description`, `ScheduledStartUtc`, `ScheduledEndUtc`, `Status`, `RecurrenceConfig`, `CreatedAtUtc` to Phase 3. Previously undocumented. |
| 52 | `MeetingParticipant`: added `OrganizationId` | Same class of fix as `IntegrationMapping` (#45). Required for `AppDbContext` global query filter tenant isolation. |
| 53 | Phase 6 stale reference corrected | "RAG query endpoint added in Phase 4" → "in Phase 6.5" (endpoint moved in decision #31). |
| 54 | Phase 6 SignalR heading: section reference fixed | "§4.3" → "§IV" — constitution uses Roman numerals, not decimal sub-numbering. |
| 55 | Compliance table: legacy section numbering fixed | "§9.1/§9.2/§9.3" → "Operational — …" labels matching constitution section names. |
| 56 | Phase 5: recording endpoints added | `GET /api/meetings/{id}/recording` and `GET /api/organizations/{orgId}/recordings` — previously the only phase without an `## Endpoints` section. |
| 57 | Phase 4 timeline name aligned | Timeline table now includes "(+ Live Transcription)" to match phase header. |
| 58 | Constitution Amendments header range corrected | "v1.1.0 → v1.3.1" → "v1.1.0 → v1.3.2" to include v1.3.2 amendment. |

## v3.1 Changes — Hardening Pass (2026-03-06)

| # | Change | Detail |
|---|--------|--------|
| 39 | RAG hybrid timeout: explicit `CancellationTokenSource` | Phase 6.5 now specifies `new CancellationTokenSource(TimeSpan.FromSeconds(3))` linked with `requestCt`. On timeout, returns 202 + enqueues `RagQueryJob` in Hangfire. Full C# implementation included. |
| 40 | SignalR tenant safety: `org:{OrganizationId}` groups | Phase 0.3 hub setup adds connections to `org:{OrganizationId}` groups on connect. All `SendAsync` calls in Phases 4, 6, 6.5, 7, 8 use `Clients.Group(...)`, never `Clients.All`. |
| 41 | Transcription flush ready-check | Phase 6 `SummarizeTranscriptJob` now performs a 10-second `Task.Delay` + stability check (verify no new segments in last 5s) before collecting transcript data. Prevents incomplete summaries from late-arriving LiveKit webhooks. |
| 42 | Phase 10 documentation: 3 new diagrams | Added **Hybrid RAG Sequence diagram**, **SignalR Tenant Safety diagram**, and **Transcription Flush Sequence diagram** to Phase 10 deliverables. |
| 43 | Phase 9 security audit expanded | Added SignalR tenant safety audit (no `Clients.All` usage) and group membership verification to Phase 9 hardening checks. |
| 44 | Constitution bumped to v1.3.1 | Added SignalR tenant-scoped group requirement to §IV. |

## v3 Changes (2026-03-06, retained)

| # | Change | Detail |
|---|--------|--------|
| 27 | pgvector reinstated as **active** | Moved from "reserved for future use" to active use for Meeting Memory RAG pipeline (UC-M3.3-003). |
| 28 | `IEmbeddingService` abstraction added | Provider-agnostic embedding interface (`/v1/embeddings` standard). Registered alongside `ILLMService` in Phase 0. |
| 29 | `MeetingEmbedding` entity added | Stores vector representations of summary chunks and key transcript segments. pgvector `vector(1536)` with HNSW/IVFFlat index. |
| 30 | `GenerateMeetingMemoryJob` added (Phase 6) | Post-meeting Hangfire job that chunks summaries + transcript segments, calls `IEmbeddingService`, stores vectors. Emits `MeetingMemoryGeneratedEvent`. |
| 31 | RAG query endpoint moved to **Phase 6.5** | `POST /api/organizations/{orgId}/meetings/ask` — moved from Phase 4 to new Phase 6.5 to resolve dependency on `MeetingEmbedding` (created in Phase 6). |
| 32 | **Hybrid sync/async pattern** for RAG | Attempt sync with 3s timeout; fall back to Hangfire + SignalR. Resolves constitution §9.2 violation (no sync AI calls in request pipeline). |
| 33 | Terminology clarified | **Member Context** = `UserOrgMembership.Context` (human-managed, Phase 2). **Meeting Memory** = `MeetingEmbedding` vectors (AI-generated, Phase 6). |
| 34 | LiveKit Cloud Egress → **download pipeline** | Egress writes to LiveKit Cloud storage (not local MinIO). Hangfire `DownloadRecordingJob` transfers to MinIO. Resolves cloud→local connectivity issue. |
| 35 | `Recording` entity expanded | Added `CloudStorageUrl`, `Status` enum (Pending/Downloading/Available/Failed). Added `RecordingAvailableEvent`. |
| 36 | Phase headers aligned with timeline | Fixed week ranges for Phases 4–10 to match timeline table (previously off by 0.5 weeks). |
| 37 | Constitution Check gate added | Formal compliance section added per governance requirement. |
| 38 | Missing entity fields documented | `Invitation`, `MeetingTag`, `RecurrenceConfig`, `PlatformIntegration`, `IntegrationMapping` field lists added. |

## v2 Changes (2026-03-06, retained)

| # | Change | Detail |
|---|--------|--------|
| 19 | Self-hosted LiveKit → LiveKit Cloud | Removed LiveKit Server, Egress, and .NET Agent from Docker Compose. All WebRTC infra offloaded to LiveKit Cloud. |
| 20 | Hardcoded OpenAI → `ILLMService` abstraction | Provider-agnostic LLM interface following OpenAI API standard. Any compatible provider can be swapped via config. |
| 21 | Frontend references removed | Fully API-first. No frontend assumptions in any phase or deliverable. |
| 22 | Single AI pipeline → Realtime + Post-meeting split | Phase 4 = realtime (live transcription via LiveKit Cloud). Phase 6 = post-meeting (summarization, task extraction via Hangfire). |
| 23 | Direct task sync → Review Queue | Tasks enter `PendingReview` status. Users approve/edit/reject before any Trello sync. Added `ReviewStatus`, `SuggestedAssigneeUserId` to `TaskItem`. |
| 24 | ~~pgvector removed~~ → **Partially reversed in v3** | Member Context (`UserOrgMembership.Context`) remains for people knowledge. Meeting Memory (pgvector) re-added in v3 for past meeting recall. |
| 25 | `TranscriptSegment` entity added | Stores live transcript chunks from LiveKit Cloud for post-meeting processing. |
| 26 | Constitution "single deployable" conflict resolved | LiveKit Cloud means no second deployable. Only the .NET backend is deployed locally. |

## v1 Changes (2026-03-03, retained)

| # | Issue | Resolution |
|---|-------|-----------|
| 1 | `MeetingTag` in Organizations phase | Moved to Meetings (Phase 3) — feature ownership rule |
| 2 | "Frontend connects to LiveKit" | Removed — API-first approach |
| 3 | "Weekly calendar view" | Changed to calendar data endpoint |
| 4 | No retry policy | Added Hangfire retry infrastructure + `Failed` state in Phase 0 |
| 5 | No correlation ID middleware | Added to Phase 0.3 |
| 6 | No secrets management | Added user-secrets / env-var strategy in Phase 0.3 |
| 7 | Testing deferred to Phase 8 | Distributed across every phase |
| 8 | Missing `Summary` entity | Added to Phase 6 |
| 9 | Missing `Reminder` entity | Added to Phase 7 |
| 10 | pgvector unused | ~~Replaced with org context in v2~~ → Re-added as Meeting Memory in v3 |
| 11 | Domain events only for Meetings | Added events to all features |
| 12 | No `CancellationToken` / DTO conventions | Added to Phase 0.3 scaffolding |
| 13 | No AI status updates via SignalR | Added to Phase 6 |
| 14 | No `Failed` state on entities | Added `Status` enum with `Failed` to all relevant entities |
| 15 | AI agent undefined | ~~LiveKit .NET Agent~~ → LiveKit Cloud transcription in v2 |
| 16 | Trello + ClickUp both required | Scoped to Trello-only with adapter pattern |
| 17 | Timeline 9–10 weeks | Revised to 15–16 weeks for solo developer |
| 18 | LiveKit Egress not addressed | ~~Local Egress service~~ → LiveKit Cloud Egress in v2 |

## Constitution Amendments Applied (v1.1.0 → v1.4.0)

| Section | Was | Now |
|---------|-----|-----|
| §II Multi-Tenancy — Membership Model | Implicit multi-org (user could belong to many orgs) | "Each user may have only one active `UserOrgMembership`. DB enforces `UNIQUE(user_id) WHERE is_enabled = true`." |
| §II Registration Model | Not specified | "Registration requires organization. Two flows: create org (Admin) or join via invitation (Member)." |
| §V Authentication — JWT Claims | "JWT Access Tokens (15-minute expiry)" | "JWT MUST include `userId` and `organizationId` claims. `organizationId` resolved from active `UserOrgMembership`." |
| §IV SignalR Group Membership | "query `UserOrganization` to resolve all org memberships on connect" | "read `organizationId` from JWT claim, add to exactly one group: `org:{organizationId}` (single active membership model)" |

| Section | Was | Now |
|---------|-----|-----|
| §I Endpoint Architecture — Error Responses | Not specified | "Endpoints MUST use `result.ToProblem(correlationIdProvider)` for business failures. All error responses include `CorrelationId` from `ICorrelationIdProvider`. MUST NOT manually construct `StandardErrorResponse`." |

| Section | Was | Now |
|---------|-----|-----|
| §I Tech Stack — Database | "pgvector (reserved for future use)" | "pgvector for Meeting Memory embeddings (RAG)" |
| §I Tech Stack — AI | "`ILLMService` abstraction" | "`ILLMService` + `IEmbeddingService` abstractions" |
| §I Endpoint Architecture | Not specified | "ASP.NET Controllers with Partial Controller Pattern. One endpoint per file via partial classes. No Minimal APIs." |
| § Performance Constraints | "No synchronous AI calls" (absolute) | Hybrid exception added for lightweight AI reads with timeout |
| §IV SignalR Usage | No tenant scoping specified | "SignalR messages MUST be scoped to `org:{OrganizationId}` Groups. `Clients.All` MUST NOT be used." |
| §IV — LiveKit Cloud | Already updated in v1.1.0 | No change |
| §I — Single deployable | Already resolved in v1.1.0 | No change |

---

# End of Revised Implementation Plan (v3.4)

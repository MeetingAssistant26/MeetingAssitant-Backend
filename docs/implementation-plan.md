# AI-Powered Meeting Assistant

## Revised Implementation Plan (v3.3 — Partial Controller Pattern)

### .NET 10 + LiveKit Cloud + LLM Abstraction + Action Items & Trello Integration

**Constitution**: v1.3.2 → v1.4.0 (amendments applied) | **Author**: Solo Developer | **Date**: 2026-03-13

> This plan supersedes v3.2 (2026-03-06). v3.3 applies the **Partial Controller Pattern**
> across all phases, replacing Minimal API endpoints with ASP.NET Controllers using
> vertical slice architecture — one endpoint per file via partial classes.
> See "Decisions & Changes" at the end for the full change log across all revisions.

### Terminology: Two Kinds of Context

| Term | What It Is | Where It Lives | Used For |
|------|-----------|----------------|----------|
| **Member Context** | Human-authored descriptions of each member's role, expertise, and responsibilities within the organization | `UserOrgMembership.Context` (text column) | Injected into LLM prompts during action item extraction so the model can resolve assignees from the participant roster |

> Member Context is relational text managed by humans. It is injected into the
> LLM prompt alongside the participant roster to improve action-item assignee
> resolution.

### Key Architectural Decisions (v3.3)

| Decision | Detail |
|----------|--------|
| **LiveKit hosting** | LiveKit Cloud (no self-hosted server, egress, or TURN/STUN) |
| **LLM coupling** | Provider-agnostic via `ILLMService` + `IEmbeddingService` abstractions (OpenAI-compatible API standard) |
| **Frontend** | Out of scope — API-first approach only |
| **Pipelines** | Realtime pipeline (live transcription) separated from post-meeting AI pipeline |
| **Task sync** | Human-in-the-loop Review Queue before any external sync |
| **Member Context** | `UserOrgMembership.Context` text field — human-managed role/expertise descriptions |
| **Action Items** | Extracted from transcripts via LLM with participant roster matching; reviewed before Trello sync |
| **Trello sync** | One-way Platform → Trello; API Key + Token model; member mapping with graceful fallback for unmapped users |
| **Recording pipeline** | LiveKit Cloud Egress → Cloud storage → Hangfire download job → local MinIO |
| **Membership model** | Simplified: one active organization per user. `UserOrgMembership` preserved for metadata. Registration requires organization (v3.4) |
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
│   │   │   ├── Invitation/
│   │   │   └── MeetingTag/
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
- Smoke test: DB connection, Redis ping, Hangfire dashboard, pgvector extension loaded
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
  - `ExpiresAtUtc`, `CreatedAtUtc`, `RevokedAtUtc` (timestampz, nullable)
- `MeetingTag` (organization-scoped labels for categorizing meetings)
  - `Id`, `OrganizationId`
  - `Name` (text, max 50) — display name of the tag
  - `Color` (text, max 7, nullable) — hex color code (e.g., "#4CAF50")
  - `IsActive` (bool, default true) — soft delete flag
  - `CreatedAtUtc`, `UpdatedAtUtc`
  - **DB Constraint**: `UNIQUE(OrganizationId, LOWER(Name)) WHERE IsActive = true` — case-insensitive uniqueness per org
  - Soft delete via `IsActive = false` — deleted tags remain in DB for referential integrity with existing meetings
  - Referenced by meetings via many-to-many junction table (defined in Phase 3)

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
    │   ├── Invitation/
    │   │   ├── InvitationController.cs
    │   │   ├── CreateInvitationEndpoint.cs
    │   │   ├── JoinOrganizationEndpoint.cs
    │   │   └── RevokeInvitationEndpoint.cs
    │   │
    │   └── MeetingTag/
    │       ├── MeetingTagController.cs
    │       ├── ListMeetingTagsEndpoint.cs
    │       ├── CreateMeetingTagEndpoint.cs
    │       ├── UpdateMeetingTagEndpoint.cs
    │       └── DeleteMeetingTagEndpoint.cs
    │
    ├── Models/
    │   ├── Requests/
    │   │   ├── CreateOrganizationRequest.cs
    │   │   ├── CreateInvitationRequest.cs
    │   │   ├── JoinOrganizationRequest.cs
    │   │   ├── UpdateMemberRoleRequest.cs
    │   │   ├── UpdateMemberContextRequest.cs
    │   │   ├── CreateMeetingTagRequest.cs
    │   │   └── UpdateMeetingTagRequest.cs
    │   └── Responses/
    │       ├── OrganizationResponse.cs
    │       ├── MemberResponse.cs
    │       ├── InvitationResponse.cs
    │       └── MeetingTagResponse.cs
    │
    ├── Services/
    │   ├── IOrganizationService.cs
    │   ├── OrganizationService.cs
    │   ├── IMemberService.cs
    │   ├── MemberService.cs
    │   ├── IInvitationService.cs
    │   ├── InvitationService.cs
    │   ├── IMeetingTagService.cs
    │   └── MeetingTagService.cs
    │
    └── Validators/
        ├── CreateOrganizationRequestValidator.cs
        ├── CreateInvitationRequestValidator.cs
        ├── JoinOrganizationRequestValidator.cs
        ├── UpdateMemberRoleRequestValidator.cs
        ├── UpdateMemberContextRequestValidator.cs
        ├── CreateMeetingTagRequestValidator.cs
        └── UpdateMeetingTagRequestValidator.cs
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
- `POST /api/organizations/{orgId}/invitations/{invitationId}/revoke` → `RevokeInvitationEndpoint.cs`

### MeetingTagController — `api/organizations/{orgId}/meeting-tags`

```csharp
namespace MeetingAssistant.Features.Organizations.Endpoints.MeetingTag;

[ApiController]
[Route("api/organizations/{orgId:guid}/meeting-tags")]
public partial class MeetingTagController : ControllerBase
{
    private readonly IMeetingTagService _meetingTagService;

    public MeetingTagController(IMeetingTagService meetingTagService)
    {
        _meetingTagService = meetingTagService;
    }
}
```

**Endpoints:**
- `GET /api/organizations/{orgId}/meeting-tags` → `ListMeetingTagsEndpoint.cs`
- `POST /api/organizations/{orgId}/meeting-tags` → `CreateMeetingTagEndpoint.cs`
- `PUT /api/organizations/{orgId}/meeting-tags/{tagId}` → `UpdateMeetingTagEndpoint.cs`
- `DELETE /api/organizations/{orgId}/meeting-tags/{tagId}` → `DeleteMeetingTagEndpoint.cs`

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
- `MemberLeftEvent`
- `InvitationRevokedEvent`
- `MeetingTagCreatedEvent`
- `MeetingTagUpdatedEvent`
- `MeetingTagDeletedEvent`

## Tests (Phase 2)

- **Integration (critical)**: tenant isolation — prove cross-org data leakage is impossible
- Unit: invitation validation, role assignment logic, context update

Deliverable:
- Multi-tenant isolation working with single-membership constraint
- Org-scoped queries enforced
- Member context stored and retrievable
- Leave organization flow functional
- Meeting tag CRUD with soft delete and case-insensitive uniqueness
- 4 controllers, 12 endpoint files

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
- `MeetingMeetingTag` (junction table — many-to-many between `Meeting` and `MeetingTag`)
  - `MeetingId` (FK to Meeting), `MeetingTagId` (FK to MeetingTag defined in Phase 2)
  - Composite PK: `(MeetingId, MeetingTagId)`

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
> | **Post-meeting AI** (Phase 6) | Summarization, action item extraction, Trello sync | After meeting ends, async via Hangfire |

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

> **v3.6 amendment**: the "Live transcription flowing and stored" deliverable
> above is superseded by Phase 4.5. No live STT in v3.6.

---

# Phase 5 — Participant Audio Egress & Storage (Weeks 7.5–8.5) (refactor in v3.6)

> **v3.6 direction change**: the recording concept is replaced by
> per-participant audio track egress. The previous `Recording` entity
> (room-composite video/audio) is removed. Video recording is out of scope.

## LiveKit Cloud Egress (per-participant track)

- Configure **LiveKit Cloud Egress** in **track-based mode**: one audio track
  egress per meeting participant.
- Output targets LiveKit Cloud's managed storage (S3-compatible).
- Backend runs a **Hangfire download job** to transfer each track to local MinIO.

> **Why not direct Egress → MinIO?** Unchanged from prior direction — LiveKit
> Cloud Egress runs in the cloud and cannot reach a Docker-local MinIO
> instance without public exposure.

## Participant Audio Download Pipeline

```
LiveKit Cloud Egress completes track (one per participant)
  → Webhook: egress_ended event received by backend (one event per track)
  → Hangfire job: DownloadParticipantAudioJob(trackId)
    → Download audio track from LiveKit Cloud storage URL
    → Upload to local MinIO
    → Update ParticipantAudioTrack row (Status = Available, LocalFilePath set)
    → Check: all tracks for this MeetingId now Available?
        → If yes, emit ParticipantAudioReadyEvent(meetingId)
```

## Entities

- **`ParticipantAudioTrack`** (replaces `Recording`):
  - `Id`, `MeetingId`, `OrganizationId`, `ParticipantUserId`
  - `CloudStorageUrl` (LiveKit Cloud storage URL — source)
  - `LocalFilePath` (MinIO path — populated after download)
  - `DurationSeconds`, `SizeBytes`
  - `Status` (Pending / Downloading / Available / Failed)
  - `CreatedAtUtc`, `UpdatedAtUtc`
  - **DB Index**: `(MeetingId, Status)` for join-barrier checks

> **Base entity note**: inherits `CreatedAtUtc` / `UpdatedAtUtc` from Phase 0.3.

## Folder Structure

```text
Features/
└── ParticipantAudio/
    ├── Models/
    │   └── Responses/
    │       └── ParticipantAudioTrackResponse.cs
    │
    ├── Services/
    │   ├── IParticipantAudioService.cs
    │   ├── ParticipantAudioService.cs
    │   ├── IStorageService.cs
    │   └── StorageService.cs
    │
    ├── Jobs/
    │   └── DownloadParticipantAudioJob.cs
    │
    └── Validators/
```

> **No controllers / endpoints in v3.6.** Participant audio tracks are
> internal pipeline artifacts. Future phases may expose admin/debug
> endpoints for track inspection if needed.

## Storage Service

- MinIO typed client (unchanged shape)
- Presigned URL generation (reserved for future admin access — no public exposure in v3.6)
- Egress completion webhook → enqueue `DownloadParticipantAudioJob`

## Domain Events

- `ParticipantAudioReadyEvent` — fires once when all tracks for a `MeetingId` reach `Status=Available`

## Tests (Phase 5)

- Unit: download job with mocked LiveKit Cloud storage
- Unit: join-barrier logic — event fires exactly once when all tracks complete
- Integration: webhook → per-track download → MinIO storage → ready event

Deliverable:
- Per-participant audio stored in MinIO via LiveKit Cloud Egress track-based pipeline
- `ParticipantAudioReadyEvent` drives the downstream STT pipeline (Phase 5.5)
- 0 controllers, 0 endpoint files (internal only)

---

# Phase 5.5 — Post-Meeting STT (Weeks 8.5–9.5) (new in v3.6)

> This phase converts per-participant audio into speaker-attributed
> `TranscriptSegment` rows. It runs entirely as Hangfire background jobs.
> No endpoints, no user-facing surface.

## Pipeline Architecture

```
ParticipantAudioReadyEvent(meetingId)
  │
  ├──→ [Orchestrator] Query ParticipantAudioTrack WHERE MeetingId=X AND Status=Available
  │       For each track → enqueue TranscribeParticipantAudioJob(trackId)
  │       Register join barrier keyed on MeetingId with N expected completions
  │
  └──→ [Hangfire] TranscribeParticipantAudioJob(trackId) × N (parallel)
          │
          ├── Fetch track from MinIO via LocalFilePath
          ├── Call ISpeechToTextService.TranscribeAsync(audioStream)
          ├── Write TranscriptSegment rows with:
          │     SpeakerUserId = ParticipantAudioTrack.ParticipantUserId (trivial attribution)
          │     StartTime / EndTime from STT output
          │     Text from STT output
          │     SequenceNumber within this track
          ├── Signal join barrier: completed = completed + 1
          └── If completed == N → emit MeetingTranscriptReadyEvent(meetingId)
```

## ISpeechToTextService Abstraction

```csharp
public interface ISpeechToTextService
{
    Task<SttResult> TranscribeAsync(Stream audio, SttRequest request, CancellationToken ct);
}

public sealed record SttRequest(string LanguageHint, string? Prompt = null);
public sealed record SttSegment(TimeSpan Start, TimeSpan End, string Text);
public sealed record SttResult(IReadOnlyList<SttSegment> Segments);
```

- Follows the **OpenAI `/v1/audio/transcriptions` contract** (Whisper-compatible).
- Default implementation: `OpenAiSpeechToTextService` (model: `whisper-1`).
- Any Whisper-compatible provider can be swapped via configuration — no code changes.
- Polly resiliency policies (retry, circuit-breaker) at `HttpClient` level.
- Registered via DI alongside `ILLMService` / `IEmbeddingService`.

## Folder Structure

```text
Features/
└── PostMeetingStt/
    ├── Jobs/
    │   ├── SttOrchestratorJob.cs           ← fan-out + join barrier
    │   └── TranscribeParticipantAudioJob.cs
    │
    ├── Services/
    │   ├── ISpeechToTextService.cs
    │   └── OpenAiSpeechToTextService.cs
    │
    └── (no endpoints)
```

> **Speaker attribution is trivial** — each `ParticipantAudioTrack` row
> identifies its speaker by `ParticipantUserId`. STT only needs to produce
> timed text; the speaker mapping is 1:1 from the track. No diarization.

## Entity Impact

- Writes `TranscriptSegment` rows (entity defined in Phase 4, schema unchanged).
- No new entities.

## Domain Events

- `ParticipantTranscriptReadyEvent(meetingId, participantUserId)` — per-track
- `MeetingTranscriptReadyEvent(meetingId)` — fires once when all tracks for a meeting are transcribed

> **No `MergeTranscriptJob`.** There is no merged transcript artifact.
> `SummarizeTranscriptJob` (Phase 6) does `ORDER BY StartTime` on the persisted
> segments at prompt-assembly time — a single indexed SELECT, microseconds even
> for hours-long meetings. Using an LLM to merge would cost tokens, introduce
> nondeterminism, and produce an intermediate artifact nobody consumes.

## Tests (Phase 5.5)

- Unit: `TranscribeParticipantAudioJob` with mocked `ISpeechToTextService`
- Unit: STT output → `TranscriptSegment` persistence with correct `SpeakerUserId`
- Unit: join-barrier completes event exactly once when N tracks finish
- Unit: retry/failure state transitions on STT provider errors
- Integration: `ParticipantAudioReadyEvent` → N parallel jobs → `MeetingTranscriptReadyEvent`

Deliverable:
- `ISpeechToTextService` registered and swappable
- Per-participant audio → speaker-attributed `TranscriptSegment` rows
- `MeetingTranscriptReadyEvent` drives Phase 6 summarization and action item extraction
- 0 controllers, 0 endpoint files (background jobs only)

---

# Phase 5.6 — Reminders — User-Facing (Weeks 9.5–10.5)

> Extracted from Phase 7 (v3.7). Reminders are now built before the post-meeting AI pipeline so that both user-facing and agent-facing surfaces are available for the Task Review Queue and Agent API phases.

## Folder Structure

```text
Features/
└── Tasks/
    ├── Endpoints/
    │   └── Reminder/
    │       ├── ReminderController.cs
    │       ├── CreateMyReminderEndpoint.cs        ← POST /api/me/reminders
    │       ├── ListMyRemindersEndpoint.cs         ← GET  /api/me/reminders
    │       ├── MarkMyReminderDeliveredEndpoint.cs ← POST /api/me/reminders/{id}/mark-delivered
    │       └── CancelMyReminderEndpoint.cs        ← DELETE /api/me/reminders/{id}
    │
    ├── Models/
    │   ├── Requests/
    │   │   └── CreateMyReminderRequest.cs
    │   └── Responses/
    │       └── ReminderResponse.cs
    │
    ├── Services/
    │   ├── IReminderService.cs
    │   └── ReminderService.cs
    │
    └── Validators/
        └── CreateMyReminderRequestValidator.cs
```

## Controller Definitions

### ReminderController — `api/me/reminders` (v3.7)

```csharp
namespace MeetingAssistant.Features.Tasks.Endpoints.Reminder;

[ApiController]
[Route("api/me/reminders")]
[Authorize]
public partial class ReminderController : ControllerBase
{
    private readonly IReminderService _reminderService;

    public ReminderController(IReminderService reminderService)
    {
        _reminderService = reminderService;
    }
}
```

> **v3.7 change**: route changed from `api/tasks/{taskId}/reminders` to
> `api/me/reminders`. Reminders are no longer linked to `TaskItem`. The thing
> to be reminded about is captured in the `Text` field on the `Reminder`
> entity (free string).

**Endpoints (all user-JWT scoped):**
- `POST   /api/me/reminders`                      → `CreateMyReminderEndpoint.cs` — creates `Scope=Personal, Channel=User, TargetUserId=me, MeetingId=null`
- `GET    /api/me/reminders`                      → `ListMyRemindersEndpoint.cs` — returns reminders affecting me: `(TargetUserId=me) OR (Scope=Public AND MeetingId IN <meetings I participate in>)`, filtered by `Status=Active`
- `POST   /api/me/reminders/{id}/mark-delivered`  → `MarkMyReminderDeliveredEndpoint.cs` — sets `Status=Delivered` (only the user's own Personal reminders)
- `DELETE /api/me/reminders/{id}`                 → `CancelMyReminderEndpoint.cs` — soft-cancel (`Status=Cancelled`), only the user's own Personal reminders

## Entities

- **`Reminder`** (refactored in v3.7):
  - `Id`, `OrganizationId`
  - `Text` (free string — the thing to be reminded about; no `TaskItem` linkage)
  - `Scope` (enum: `Personal` | `Public`)
  - `Channel` (enum: `User` | `Agent` — provenance of the reminder)
  - `CreatedByUserId` (who originated the intent)
  - `TargetUserId` (nullable — required when `Scope=Personal`; null when `Scope=Public`. Public reminders target all participants of `MeetingId`, derived at fetch time from `MeetingParticipant`.)
  - `MeetingId` (nullable — required when `Scope=Public` or when `Channel=Agent`. Always null when `Channel=User` because user-created personal reminders are standalone.)
  - `ReminderAtUtc` (when the reminder is "due"; used by fetch endpoints as a filter — see Reminder Fetch Semantics below. No firing.)
  - `Status` (enum: `Active` | `Delivered` | `Cancelled`)
  - `DeliveredAtUtc` (nullable)
  - `OriginalText` (nullable — raw agent input, audit only)
  - `CreatedAtUtc`, `UpdatedAtUtc`
  - **DB Indexes**: `(TargetUserId, Status)` for the user's "my reminders" query; `(MeetingId, Scope, Status)` for the agent's "this meeting's public reminders" query.

> **v3.7 changes**: removed `TaskItemId` linkage, removed `Pending/Sent/Failed`
> states, removed Hangfire `TriggerReminderJob`, removed SignalR push. Reminders
> are pure data, fetched via endpoints. Status lifecycle is now `Active →
> Delivered` (after fetch + acknowledgement) or `Active → Cancelled` (soft delete).

## Reminder System (v3.7)

- **No Hangfire firing**, **no SignalR push**. Reminders are pure data.
- Personal reminders are surfaced to the user via the `GET /api/me/reminders` poll endpoint. The user marks delivery via `POST /api/me/reminders/{id}/mark-delivered`.
- Public reminders are surfaced to the agent via `GET /api/agent/meetings/{meetingId}/reminders` (Phase 5.7) at meeting start. The agent speaks them and marks delivery via `POST /api/agent/reminders/{id}/mark-delivered`.

## Reminder Fetch Semantics (v3.7)

`ReminderAtUtc` acts as a **timing gate** in fetch queries:

| Endpoint | Filter |
|---|---|
| `GET /api/me/reminders` (user) | `Status=Active AND ReminderAtUtc <= now AND ((TargetUserId=me) OR (Scope=Public AND MeetingId IN <my meetings>))` |
| `GET /api/agent/meetings/{meetingId}/reminders` (agent — Phase 5.7) | `Status=Active AND Scope=Public AND MeetingId=X AND ReminderAtUtc <= meeting.scheduledStartUtc` |

For Public reminders bound to a recurring meeting (one row per series, Model A), the agent query `ReminderAtUtc <= meeting.scheduledStartUtc` ensures the reminder fires at the first occurrence whose start time is on or after `ReminderAtUtc`. This lets agents express "remind us in 2 weeks at the standup" by setting `ReminderAtUtc` to the target date — earlier occurrences will not return the reminder. After the agent speaks it and marks `Delivered`, it never returns again.

> **No per-occurrence targeting** (`OccurrenceDateUtc` is intentionally not modeled).
> Each reminder fires at exactly one occurrence: the first one whose start time
> is on or after `ReminderAtUtc`. After delivery, the reminder is closed.

## Domain Events

- `ReminderCreatedEvent`
- `ReminderDeliveredEvent` (v3.7 — replaces `ReminderTriggeredEvent`)
- `ReminderCancelledEvent` (v3.7)

## Tests (Phase 5.6)

- Unit: reminder fetch semantics — `ReminderAtUtc` timing gate for Personal and Public
- Unit: `mark-delivered` and `cancel` state transitions
- Integration: user creates personal reminder → polls → marks delivered → no longer returned
- Integration: tenant isolation — user cannot see/mark/cancel another user's or another org's reminders

Deliverable:
- User-facing reminder lifecycle works (create / list / mark-delivered / cancel)
- 1 controller, 4 endpoint files


---

# Phase 5.7 — Agent-Callable API Surface (Weeks 10.5–11.5) (new in v3.7)

> **Scope**: this phase defines all endpoints that the **LiveKit live agent**
> (STT → LLM → TTS loop) calls during a meeting. The agent itself is
> **external to this backend** and is not a deliverable of this repo. The
> backend's only contract with the agent is the endpoint surface defined
> below + the dedicated auth scheme.

## Agent Service-Identity Auth

A separate JWT issuer (HMAC-signed bearer) for LiveKit agent calls. Distinct
from the user JWT.

**Claims:**
- `agent` = `true`
- `organizationId` (required — tenant binding)
- `meetingId` (required — bound to a single meeting room for the token's lifetime)

**Policy:** `[Authorize(Policy="AgentOnly")]`. Agents cannot access user-JWT
endpoints; users cannot access agent-only endpoints. Tenant isolation is
enforced via the `organizationId` claim, identical to the user-JWT flow.

**Issuance:** the agent token is minted by the backend at meeting-room creation
time (when LiveKit Cloud's webhook fires `room_started`) and delivered to the
agent worker via the same channel it uses for room access. Token lifetime
matches the expected meeting duration + a buffer (e.g., 4 hours max).

> **Constitution alignment**: extends §V Authentication with a separate JWT
> issuer for service identities. Same key-rotation discipline as user JWTs.
> No hardcoded keys.

## Folder Structure

```text
Features/
└── AgentApi/
    ├── Endpoints/
    │   ├── Reminder/
    │   │   ├── AgentReminderController.cs
    │   │   ├── CreateReminderEndpoint.cs           ← POST /api/agent/meetings/{meetingId}/reminders
    │   │   ├── ListMeetingRemindersEndpoint.cs     ← GET  /api/agent/meetings/{meetingId}/reminders
    │   │   └── MarkReminderDeliveredEndpoint.cs    ← POST /api/agent/reminders/{id}/mark-delivered
    │   │
    │   └── Context/
    │       ├── AgentContextController.cs
    │       ├── GetOrganizationEndpoint.cs          ← GET /api/agent/organization
    │       ├── GetMeetingMembersEndpoint.cs        ← GET /api/agent/meetings/{meetingId}/members
    │       ├── ListMeetingsEndpoint.cs             ← GET /api/agent/meetings?status=upcoming|past&limit=N
    │       ├── GetMeetingDetailEndpoint.cs         ← GET /api/agent/meetings/{meetingId}
    │       ├── ListRecurringMeetingsEndpoint.cs    ← GET /api/agent/meetings/recurring
    │       └── ListMeetingTagsEndpoint.cs          ← GET /api/agent/meeting-tags
    │
    ├── Models/
    │   ├── Requests/
    │   │   └── CreateAgentReminderRequest.cs
    │   └── Responses/
    │       ├── AgentMemberResponse.cs              ← { userId, displayName, jobRole, context }
    │       ├── AgentMeetingResponse.cs             ← { id, title, scheduledStartUtc, status, recurrenceConfig?, tagIds }
    │       ├── AgentMeetingDetailResponse.cs       ← + participants, recurrenceConfig
    │       ├── AgentReminderResponse.cs            ← { id, text, scope, targetUserId?, reminderAtUtc, status }
    │       └── AgentOrganizationResponse.cs        ← { id, name, slug, memberCount }
    │
    ├── Services/
    │   ├── IAgentAuthService.cs                    ← mints + validates agent tokens
    │   ├── AgentAuthService.cs
    │   ├── IAgentContextService.cs                 ← read-only views over Meetings/Members/Tags
    │   └── AgentContextService.cs
    │
    └── Validators/
        └── CreateAgentReminderRequestValidator.cs
```

> **No new entities.** Phase 5.7 is purely an alternate API surface over
> existing data: `Reminder` (Phase 5.6), `Meeting`/`MeetingParticipant` (Phase 3),
> `UserOrgMembership.Context` (Phase 2), `MeetingTag` (Phase 2).

## Controller Definitions

### AgentReminderController — `api/agent`

```csharp
namespace MeetingAssistant.Features.AgentApi.Endpoints.Reminder;

[ApiController]
[Route("api/agent")]
[Authorize(Policy = "AgentOnly")]
public partial class AgentReminderController : ControllerBase
{
    private readonly IReminderService _reminderService;          // shared with Phase 5.6
    private readonly IAgentContextProvider _agentContext;        // resolves orgId/meetingId from token

    public AgentReminderController(IReminderService reminderService, IAgentContextProvider agentContext)
    {
        _reminderService = reminderService;
        _agentContext = agentContext;
    }
}
```

**Endpoints (all agent-policy):**
- `POST /api/agent/meetings/{meetingId}/reminders` → `CreateReminderEndpoint.cs`
  - Body: `{ text, scope ("Personal"|"Public"), targetUserId? (required if Personal), reminderAtUtc }`
  - `meetingId` route param MUST match the agent token's `meetingId` claim
  - Sets `Channel=Agent`, `CreatedByUserId` = the user the agent is assisting (passed in token claims or body), `MeetingId=meetingId`, `OriginalText` = raw user utterance
- `GET /api/agent/meetings/{meetingId}/reminders` → `ListMeetingRemindersEndpoint.cs`
  - Returns `Scope=Public AND MeetingId=X AND Status=Active AND ReminderAtUtc <= meeting.scheduledStartUtc`
  - **Never returns Personal reminders** (defence-in-depth at the service layer; agent must not speak personal data publicly)
- `POST /api/agent/reminders/{id}/mark-delivered` → `MarkReminderDeliveredEndpoint.cs`
  - Sets `Status=Delivered`, `DeliveredAtUtc=now`
  - Allowed only on `Scope=Public` reminders. Personal reminders are marked delivered by the user via their own endpoint (Phase 5.6) — the agent never sees them.

### AgentContextController — `api/agent`

```csharp
namespace MeetingAssistant.Features.AgentApi.Endpoints.Context;

[ApiController]
[Route("api/agent")]
[Authorize(Policy = "AgentOnly")]
public partial class AgentContextController : ControllerBase
{
    private readonly IAgentContextService _agentContext;

    public AgentContextController(IAgentContextService agentContext)
    {
        _agentContext = agentContext;
    }
}
```

**Endpoints (all agent-policy, all org-scoped via token claim):**
- `GET /api/agent/organization` → `GetOrganizationEndpoint.cs`
  - Returns: `{ id, name, slug, memberCount }`
- `GET /api/agent/meetings/{meetingId}/members` → `GetMeetingMembersEndpoint.cs`
  - **Filtered to current meeting participants only** (not the whole org roster)
  - Returns: `[{ userId, displayName, jobRole, context }]` for each `MeetingParticipant` of the meeting whose `UserOrgMembership.IsEnabled=true`
  - `meetingId` MUST match the agent token's `meetingId` claim
  - This is the agent's primary "who's-who" lookup for the people in the room
- `GET /api/agent/meetings?status=upcoming|past&limit=N&offset=M` → `ListMeetingsEndpoint.cs`
  - Paginated meeting list across the org
  - Each row: `{ id, title, scheduledStartUtc, scheduledEndUtc, status, recurrenceConfig?, tagIds }`
- `GET /api/agent/meetings/{meetingId}` → `GetMeetingDetailEndpoint.cs`
  - Single meeting detail: `{ ...summary fields, participants[], recurrenceConfig }` so the agent can detect recurring + compute next-occurrence dates from `RecurrenceConfig`
- `GET /api/agent/meetings/recurring` → `ListRecurringMeetingsEndpoint.cs`
  - Recurring series only (`RecurrenceConfig IS NOT NULL`), shortcut for the agent
- `GET /api/agent/meeting-tags` → `ListMeetingTagsEndpoint.cs`
  - Tag catalog: `[{ id, name, color }]` for active tags

> **Past-meeting summaries (RAG)**: deferred. The agent will use the existing
> Phase 6.5 `/api/organizations/{orgId}/meetings/ask` endpoint when access is
> opened to it (with an agent-policy variant). Out of scope for v3.7.

## Tool-Use Mapping (informational)

Each endpoint corresponds to one **tool** in the agent's LLM tool catalog.
Example mapping:

| Endpoint | Agent tool name | When LLM calls it |
|---|---|---|
| `GET /api/agent/meetings/{meetingId}/members` | `get_meeting_members` | "Who's in this meeting?" / "Remind Ahmed to..." |
| `GET /api/agent/meetings?status=upcoming` | `get_upcoming_meetings` | "Remind me at next meeting" → look up next meeting |
| `GET /api/agent/meetings/{meetingId}` | `get_meeting_detail` | Need to check if meeting is recurring |
| `POST /api/agent/meetings/{meetingId}/reminders` | `create_reminder` | User asks for a reminder |
| `GET /api/agent/meetings/{meetingId}/reminders` | `get_meeting_reminders` | At meeting start: anything to surface? |
| `POST /api/agent/reminders/{id}/mark-delivered` | `mark_reminder_delivered` | After speaking the reminder |

## Recurring Meeting + Reminder Semantics (agent guidance)

The backend models recurring meetings as **one row per series** (`Meeting` with
`RecurrenceConfig`). Reminders link to the series `MeetingId`; `ReminderAtUtc`
gates which occurrence first delivers the reminder.

**Agent decision tree for "remind us at next meeting":**
1. Call `get_meeting_detail(currentMeetingId)` → check `recurrenceConfig`.
2. If recurring → compute next occurrence's `scheduledStartUtc` from
   `RecurrenceConfig`; create reminder with `Scope=Public, MeetingId=<series>,
   ReminderAtUtc=<next occurrence start>`.
3. If not recurring → ask user to clarify: *"This is a one-off meeting. Did
   you mean a specific upcoming meeting?"* Then `get_upcoming_meetings()` and
   match by user's choice.

**Agent decision tree for "remind me at next meeting" (Personal):**
1. Call `get_upcoming_meetings(forUser=me)` → take the first row.
2. Confirm with user, then `create_reminder` with `Scope=Personal,
   TargetUserId=<user>, MeetingId=<that meeting>, ReminderAtUtc=<that
   meeting's start>`.

## Tenant Isolation Rules

- Every agent endpoint MUST verify `meetingId` route param (when present)
  matches the agent token's `meetingId` claim. Mismatch → 403.
- Every read query MUST filter by `OrganizationId` (inherits global query
  filter from `AppDbContext`).
- `ListMeetingRemindersEndpoint` MUST hard-filter `Scope=Public` at the
  service layer — even if a query string requests Personal, return empty.
  Defence-in-depth against future refactors.

## Tests (Phase 5.7)

- Unit: agent token issuance + validation; mismatched `meetingId` claim → 403
- Unit: `get_meeting_members` filters to current meeting participants only
- Unit: `ListMeetingRemindersEndpoint` never returns Personal reminders
- Unit: reminder fetch timing gate (`ReminderAtUtc <= meeting.scheduledStartUtc`)
- Integration: agent creates Public reminder → Public reminder surfaces at
  matching occurrence → mark-delivered → no longer surfaces
- Integration: tenant isolation — agent token for org A cannot read org B's
  meetings/members/reminders
- Integration: agent cannot reach user-JWT endpoints; user JWT cannot reach
  agent endpoints

## Domain Events

- `AgentReminderCreatedEvent` — emitted alongside `ReminderCreatedEvent` when
  `Channel=Agent`, for audit/observability
- `AgentReminderDeliveredEvent` — emitted alongside `ReminderDeliveredEvent`
  when an agent marks delivered

Deliverable:
- Agent service-identity auth scheme working
- Three reminder endpoints + six context endpoints under `/api/agent/*`
- Tenant isolation verified across agents and users
- 2 controllers, 9 endpoint files

---



---

# Phase 6 — Action Items & Trello Integration (Weeks 11.5–14)

> This phase replaces the previous Phase 6 (Meeting Memory + Task Review Queue)
> and Phase 8 (Trello Integration). It implements the complete flow from
> post-meeting action-item extraction through human review to one-way Trello sync.
>
> **Key decisions**:
> - Action item extraction runs in parallel with summarization after transcript completion.
> - LLM receives the full participant roster (with IDs) to resolve assignees deterministically.
> - Sync is one-way only: Platform → Trello. No Trello webhooks or bidirectional updates.
> - Trello auth uses API Key + Token model (OAuth 1.0a deferred).
> - No SignalR notifications. Status updates are poll-based via API.

## 6.1 Post-Meeting Pipeline Architecture

After `GenerateMeetingTranscriptJob` persists the transcript, it publishes a
`MeetingTranscriptReadyEvent` via MediatR. Two independent handlers listen:

| Handler | Enqueues Job | Input | Output |
|---------|--------------|-------|--------|
| `EnqueueSummaryGenerationHandler` | `GenerateMeetingSummaryJob` | Transcript text | `MeetingSummary` |
| `EnqueueActionItemExtractionHandler` | `ExtractActionItemsJob` | Transcript text | `List<ActionItem>` |

This avoids making the summary job a bottleneck for action items.

---

## 6.2 Action Item Extraction (`ExtractActionItemsJob`)

**Step 1: Build Participant Roster**
Fetch all `MeetingParticipant` records for the meeting:

```json
[
  { "participantId": "...", "userId": "...", "displayName": "Ahmed Hassan", "role": "Host" },
  { "participantId": "...", "userId": null, "displayName": "External Guest", "role": "Participant" }
]
```

Include `UserId` where available; use `MeetingParticipant.Id` as the fallback
identifier for participants without platform accounts.

**Step 2: LLM Prompt**
Send the transcript + roster to the outsourced LLM with a structured system
prompt:

> *"Given the transcript and the participant roster above, extract action items.
> For each item, return: title, description, responsibleParticipantId (from the
> roster), dueDate. If the responsible person is not in the roster, return null
> for the id."*

**Step 3: Persist**
- Deserialize response.
- Map `responsibleParticipantId` to `AssignedToParticipantId`.
- If `UserId` is present on the participant → set `AssignedToUserId`.
- If `UserId` is null → leave `AssignedToUserId` empty (plain-text assignee fallback).
- `Status = PendingReview`.

**Idempotency:** Before inserting, check `ActionItems.Any(x => x.MeetingId == meetingId)`. If items exist, exit.

**Edge case — LLM hallucination / bad JSON:** Wrap deserialization in try/catch. Log raw response and abort. Do not crash the job.

---

## 6.3 Action Item Review & Approval Flow

**Permissions:** Hosts, CoHosts, and Org Admins can review.

**Folder Structure**

```text
Features/
└── ActionItems/
    ├── Endpoints/
    │   ├── Review/
    │   │   ├── ActionItemReviewController.cs
    │   │   ├── ListMeetingActionItemsEndpoint.cs
    │   │   ├── UpdateActionItemEndpoint.cs
    │   │   ├── ApproveActionItemEndpoint.cs
    │   │   ├── RejectActionItemEndpoint.cs
    │   │   ├── SyncActionItemEndpoint.cs
    │   │   └── BulkSyncActionItemsEndpoint.cs
    │   │
    │   └── UserConnection/
    │       ├── UserConnectionController.cs
    │       ├── GetMyConnectionsEndpoint.cs
    │       ├── ConnectTrelloEndpoint.cs
    │       └── DisconnectTrelloEndpoint.cs
    │
    ├── Models/
    │   ├── Requests/
    │   │   ├── UpdateActionItemRequest.cs
    │   │   ├── ApproveActionItemRequest.cs
    │   │   ├── RejectActionItemRequest.cs
    │   │   └── ConnectTrelloRequest.cs
    │   └── Responses/
    │       ├── ActionItemResponse.cs
    │       └── ActionItemListResponse.cs
    │
    ├── Services/
    │   ├── IActionItemService.cs
    │   ├── ActionItemService.cs
    │   ├── ITrelloConnectionService.cs
    │   ├── TrelloConnectionService.cs
    │   ├── ITrelloClient.cs
    │   └── TrelloClient.cs
    │
    ├── Jobs/
    │   ├── ExtractActionItemsJob.cs
    │   └── SyncActionItemsToTrelloJob.cs
    │
    └── Validators/
        ├── UpdateActionItemRequestValidator.cs
        ├── ApproveActionItemRequestValidator.cs
        ├── RejectActionItemRequestValidator.cs
        └── ConnectTrelloRequestValidator.cs
```

**Controller Definitions**

### ActionItemReviewController — `api/organizations/{orgId}/meetings/{meetingId}/action-items`

```csharp
namespace MeetingAssistant.Features.ActionItems.Endpoints.Review;

[ApiController]
[Route("api/organizations/{orgId:guid}/meetings/{meetingId:guid}/action-items")]
public partial class ActionItemReviewController : ControllerBase
{
    private readonly IActionItemService _actionItemService;

    public ActionItemReviewController(IActionItemService actionItemService)
    {
        _actionItemService = actionItemService;
    }
}
```

**Endpoints:**
- `GET /api/organizations/{orgId}/meetings/{meetingId}/action-items` → `ListMeetingActionItemsEndpoint.cs`
- `PATCH /api/organizations/{orgId}/meetings/{meetingId}/action-items/{id}` → `UpdateActionItemEndpoint.cs`
- `POST /api/organizations/{orgId}/meetings/{meetingId}/action-items/{id}/approve` → `ApproveActionItemEndpoint.cs`
- `POST /api/organizations/{orgId}/meetings/{meetingId}/action-items/{id}/reject` → `RejectActionItemEndpoint.cs`
- `POST /api/organizations/{orgId}/meetings/{meetingId}/action-items/{id}/sync` → `SyncActionItemEndpoint.cs`
- `POST /api/organizations/{orgId}/meetings/{meetingId}/action-items/sync-all` → `BulkSyncActionItemsEndpoint.cs`

### UserConnectionController — `api/users/me/connections`

```csharp
namespace MeetingAssistant.Features.ActionItems.Endpoints.UserConnection;

[ApiController]
[Route("api/users/me/connections")]
public partial class UserConnectionController : ControllerBase
{
    private readonly ITrelloConnectionService _trelloConnectionService;

    public UserConnectionController(ITrelloConnectionService trelloConnectionService)
    {
        _trelloConnectionService = trelloConnectionService;
    }
}
```

**Endpoints:**
- `GET /api/users/me/connections` → `GetMyConnectionsEndpoint.cs`
- `POST /api/users/me/connections/trello` → `ConnectTrelloEndpoint.cs`
- `DELETE /api/users/me/connections/trello` → `DisconnectTrelloEndpoint.cs`

**UI Logic:**
- Action items appear in a review panel after the meeting ends.
- Host can edit title, description, assignee, due date, or mark as `Approved` / `Rejected`.
- Only `Approved` items are eligible for sync.
- Once synced, `TrelloCardUrl` is written back for deep-linking.

---

## 6.4 Trello Connection (API Key + Token Model)

### Organization-Level Connection
1. Admin navigates to Org Settings → Integrations → Trello.
2. Admin provides **Trello API Key + Token**.
3. Backend validates by calling `GET /1/members/me`.
4. Admin selects a **Board** and a **List** from fetched dropdowns.
5. System stores `TrelloWorkspaceConfig` with encrypted credentials.

### User-Level Connection (for Assignments)
1. User goes to Profile → Connected Accounts → Connect Trello.
2. User provides their personal Trello token.
3. Backend fetches Trello `MemberId` and username, stores encrypted token in
   `ExternalAccountLink`.

### Admin Member Mapping View
- Org Admin sees a list of members with Trello connection status
  (`IsConnected`, `TrelloUsername`).
- Admin can manually override the Trello Member ID mapping if needed.

---

## 6.5 Trello Sync Engine (`SyncActionItemsToTrelloJob`)

**Trigger:** Enqueued by sync endpoint or `sync-all` endpoint.

**Per-item logic:**
1. Load `TrelloWorkspaceConfig` for the org.
2. Call `POST /1/cards` with:
   - `name` = action item title
   - `desc` = action item description
   - `due` = due date (ISO 8601)
   - `idList` = configured list id
   - `idMembers` = resolved Trello member id (if any)

**Assignee Resolution:**
1. Look up `AssignedToUserId` → query `ExternalAccountLink` for Trello token + member id.
2. Validate that the member id exists in the target board's member list
   (cache board members for ~5 min).
3. If valid → include `idMembers`.
4. If invalid or missing → create card **without** assignee.

**Status mapping after sync:**

| Result | `ActionItem.Status` | `TrelloAssigneeMissingReason` |
|--------|---------------------|-------------------------------|
| Card created with assignee | `Synced` | `null` |
| Card created, no assignee (user not connected) | `SyncedNoAssignee` | `UserNotConnected` |
| Card created, no assignee (not a board member) | `SyncedNoAssignee` | `NotBoardMember` |
| Trello API 401 | Stop job. Mark integration `NeedsReconnect`. | — |
| Trello API 404 (list/board deleted) | Stop job. Mark integration `InvalidConfig`. | — |

**Idempotency:** If `TrelloCardId` is already set, skip that item.

---

## Entities

- **`ActionItem`**:
  - `Id`, `MeetingId`, `OrganizationId`
  - `Title`, `Description`
  - `AssignedToParticipantId` — resolved from LLM roster matching
  - `AssignedToUserId` — platform user (null if external/guest participant)
  - `DueDateUtc`
  - `Status` (`PendingReview`, `Approved`, `Rejected`, `Synced`, `SyncedNoAssignee`)
  - `TrelloCardId`, `TrelloCardUrl`
  - `TrelloAssigneeMissingReason` (nullable)
  - `ExtractedAtUtc`, `SyncedAtUtc`
  - `CreatedAtUtc`, `UpdatedAtUtc`

- **`OrganizationIntegration`**:
  - `Id`, `OrganizationId`
  - `Type` (enum: Trello — extensible)
  - `Status` (`Active`, `NeedsReconnect`, `InvalidConfig`, `Disabled`)
  - `CreatedAtUtc`, `UpdatedAtUtc`

- **`TrelloWorkspaceConfig`**:
  - `Id`, `OrganizationId`
  - `BoardId`, `ListId`
  - `ApiKey` (encrypted), `ApiToken` (encrypted)
  - `CreatedAtUtc`, `UpdatedAtUtc`

- **`ExternalAccountLink`**:
  - `Id`, `UserId`, `OrganizationId`
  - `Provider` (enum: Trello)
  - `ExternalUserId`, `ExternalUsername`
  - `AccessToken` (encrypted)
  - `CreatedAtUtc`, `UpdatedAtUtc`

- **`TrelloMemberMapping`**:
  - `Id`, `OrganizationId`, `UserId`
  - `TrelloMemberId`
  - `CreatedAtUtc`, `UpdatedAtUtc`

---

## Domain Events

- `ActionItemsExtractedEvent`
- `ActionItemApprovedEvent`
- `ActionItemRejectedEvent`
- `ActionItemSyncedEvent`
- `ActionItemSyncFailedEvent`
- `TrelloIntegrationConnectedEvent`
- `TrelloIntegrationDisconnectedEvent`

---

## Admin Settings API Surface (Org-Level)

```
GET    /api/organizations/{orgId}/integrations/trello
PUT    /api/organizations/{orgId}/integrations/trello
GET    /api/organizations/{orgId}/integrations/trello/boards
GET    /api/organizations/{orgId}/integrations/trello/boards/{boardId}/lists
GET    /api/organizations/{orgId}/members/trello-status
```

> These endpoints are added under `Features/Organizations/Endpoints/Integration/`
> to keep organization settings colocated, or under `Features/ActionItems/`
> depending on team preference. For tenant isolation consistency, the route
> remains under `/api/organizations/{orgId}/...`.

---

## Tests (Phase 6)

- Unit: `ExtractActionItemsJob` with mocked `ILLMService` — verifies roster injection and idempotency
- Unit: LLM response parsing with malformed JSON fallback
- Unit: `SyncActionItemsToTrelloJob` with mocked `ITrelloClient`
- Unit: assignee resolution — connected user, unconnected user, non-board member, guest participant
- Unit: retry/failure state transitions (401 → NeedsReconnect, 404 → InvalidConfig)
- Integration: full MeetingEnded → TranscriptReady → Extraction → Review → Sync flow
- Integration: tenant isolation — org A's Trello config cannot be used by org B

Deliverable:
- Action items extracted from meeting transcripts with deterministic participant matching
- Human review and approval flow functional
- One-way Trello sync for approved items
- Graceful handling of missing mappings and revoked credentials
- 3 controllers, 11 endpoint files

---

# Phase 9 — Integration Testing & Hardening (Weeks 16.5–17.5)

## End-to-End Flow Tests

1. Register → create org → set Member Context → create meeting → start meeting
2. LiveKit Cloud session → end meeting
3. Post-meeting pipeline: summary generated → action items extracted (with deterministic participant matching)
4. Host reviews extracted action items → approves some, rejects others
5. Approved action items synced to Trello (one-way sync)
6. User creates personal reminder → polls → marks delivered

## Security & Compliance Audit

- Tenant isolation audit: verify every query filtered by `OrganizationId`
- **Membership constraint audit**: verify `UNIQUE(user_id) WHERE is_enabled = true` on `UserOrgMembership`
- **Agent surface audit (v3.7)**:
  - Agent JWT issuer key rotation
  - Every `/api/agent/*` endpoint enforces `[Authorize(Policy="AgentOnly")]`
  - Every agent endpoint with a `meetingId` route param verifies it matches the token claim
  - `ListMeetingRemindersEndpoint` hard-filters `Scope=Public` at the service layer (Personal reminders never returned)
  - User-JWT endpoints reject agent tokens; agent endpoints reject user tokens
  - `GetMeetingMembersEndpoint` returns only current meeting participants, never the whole org roster
- Secrets audit: no hardcoded keys (JWT, agent JWT, LiveKit Cloud, Trello OAuth, MinIO)
- JWT key rotation verification (user JWT + agent JWT)
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
- Verify no synchronous LLM calls in request pipeline
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

# Phase 10 — Demo Preparation (Weeks 17.5–18.5)

## Demo Flow (API-only — Postman/HTTP collection)

1. Register user
2. Create organization + set Member Context ("Ahmed — backend engineer", etc.)
3. Invite member
4. Create meeting
5. Get LiveKit Cloud join token → join meeting
6. End meeting
7. Post-meeting AI pipeline runs
8. Summary generated with structured content
9. Action items extracted with deterministic participant matching via LLM roster
10. Host reviews action items → approves/edit/rejects
11. Approved action items synced to Trello (one-way sync)
12. User creates personal reminder → polls → marks delivered

## Documentation

- Architecture diagram (showing LiveKit Cloud boundary vs local Docker services)
- **Pipeline diagram** (realtime pipeline vs post-meeting AI pipeline)
- Event flow diagram
- ERD (all entities including ActionItem, TrelloWorkspaceConfig, ExternalAccountLink, Member Context fields)
- Deployment diagram (Docker Compose + LiveKit Cloud)
- AI orchestration flow (transcript → summarization → action item extraction)
- **Partial Controller Pattern reference** (v3.3) — shows controller grouping rules, file naming conventions, and endpoint-per-file examples. Includes anti-pattern list
- LLM + Embedding abstraction documentation (`ILLMService` + `IEmbeddingService` provider swap guide)
- API collection (Postman / `.http` files)

## Endpoint Summary (v3.4)

| Phase | Feature | Controllers | Endpoint Files | Total Actions |
|-------|---------|-------------|----------------|---------------|
| 1 | Identity | 3 (Auth, Token, Profile) | 8 | 8 |
| 2 | Organizations | 4 (Organization, Member, Invitation, MeetingTag) | 11 | 11 |
| 3 | Meetings | 4 (Meeting, Recurring, Participant, Calendar) | 7 | 7 |
| 4 | LiveSession | 3 (Session, Webhook, Transcript) | 3 | 3 |
| 4.5 | Realtime Amendment (docs) | 0 | 0 | 0 |
| 5 | Participant Audio | 0 (internal only) | 0 | 0 |
| 5.5 | Post-Meeting STT | 0 (background jobs only) | 0 | 0 |
| 5.6 | Reminders | 1 (Reminder) | 4 | 4 |
| 5.7 | Agent API Surface | 2 (AgentReminder, AgentContext) | 9 | 9 |
| 6 | Action Items & Trello | 3 (ActionItemReview, UserConnection, TrelloAdmin*) | 11 | 11 |
| **Total** | | **20 controllers** | **53 endpoint files** | **53 actions** |

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
| §I Tech stack (non-negotiable) | ✅ Compliant | All technologies match constitution. |
| §I Endpoint architecture | ✅ Compliant | v3.3: ASP.NET Controllers with Partial Controller Pattern. One endpoint per file via partial classes. No Minimal APIs. |
| §II Multi-tenancy (`OrganizationId`) | ✅ Compliant | All org-scoped entities include `OrganizationId`. Global query filter in `AppDbContext`. |
| §II Two-level role model | ✅ Compliant | Org roles (Admin/Member/Guest) + Meeting roles (Host/CoHost/Participant/Observer) |
| §II State separation (persistent vs ephemeral) | ✅ Compliant | No media state in PostgreSQL/Redis. LiveKit manages ephemeral state. |
| §III Domain events for cross-feature | ✅ Compliant | Events defined in every phase. No direct cross-feature service calls. |
| §III Hangfire for long-running ops | ✅ Compliant | Summarization, action item extraction, Trello sync all via Hangfire. |
| §III Retry policy (3 retries, Failed state) | ✅ Compliant | Phase 0.3 scaffolding. `Failed` state on all relevant entities. |
| §IV LiveKit Cloud responsibilities | ✅ Compliant | Backend does not proxy media. Token-based auth with role permissions. |
| §V JWT 15-min / refresh 7-day | ✅ Compliant | Phase 1 implements exact spec. |
| §V Secrets management | ✅ Compliant | user-secrets (dev), env vars (Docker). No hardcoded keys. |
| Operational — Correlation IDs | ✅ Compliant | Phase 0.3 middleware. Verified in Phase 9. |
| Operational — No sync AI in request pipeline | ✅ Compliant | Heavy AI strictly async via Hangfire. |
| Operational — <300ms API responses | ✅ Compliant | Phase 9 load tests target <300ms p95. |
| Operational — DI, FluentValidation, CancellationToken, UTC | ✅ Compliant | Phase 0.3 scaffolding enforces all coding standards. |
| Operational — Partial Controller Pattern | ✅ Compliant | v3.3: All endpoints follow one-file-per-action pattern. Phase 9 includes pattern audit. |
| Data pipeline integrity | ✅ Compliant | v3.6: bounded file-based STT (Phase 5.5) guarantees complete transcript before summarization. Flush ready-check removed — no longer applicable. |

> **No violations found**. v3.3 applies the Partial Controller Pattern across all phases.

---

# Estimated Timeline (Solo Developer)

| Phase | Duration | Weeks |
|-------|----------|-------|
| 0. Infrastructure & Foundation | 2 weeks | 1–2 |
| 1. Identity | 1 week | 2–3 |
| 2. Organizations (+ Member Context) | 1 week | 3–4 |
| 3. Meetings | 2 weeks | 4–6 |
| 4. LiveKit Cloud & Realtime Pipeline | 1 week | 6–7 |
| 4.5. Realtime Pipeline Amendment (v3.6, docs) | 0 weeks | — |
| 5. Participant Audio Egress & Storage | 1 week | 7–8 |
| 5.5. Post-Meeting STT (new in v3.6) | 1 week | 8–9 |
| 5.6. Reminders — User-Facing | 1 week | 9.5–10.5 |
| 5.7. Agent-Callable API Surface | 1 week | 10.5–11.5 |
| 6. Action Items & Trello Integration | 2.5 weeks | 11.5–14 |
| 9. Testing & Hardening | 1 week | 14–15 |
| 10. Demo Preparation | 1 week | 15–16 |

**Total estimated duration: 16–17 weeks**

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

## v3.8 Changes — Reminders Extracted to Independent Phases 5.6 / 5.7 (2026-04-27)

| # | Change | Detail |
|---|---|--------|
| 134 | Reminders extracted from Phase 7 to new Phase 5.6 | All user-facing reminder endpoints moved to independent **Phase 5.6 — Reminders — User-Facing**. Placed after Phase 5.5 (Post-Meeting STT) and before Phase 6. |
| 135 | Agent API Surface moved to Phase 5.7 | The agent-callable surface moved from Phase 7.5 to **Phase 5.7 — Agent-Callable API Surface**, placed immediately after Phase 5.6. |
| 136 | Phases 6, 6.5, 7, 8 replaced by new Phase 6 | Old Phases 6 (Meeting Memory), 6.5 (RAG), 7 (Task Review), 8 (Trello) removed and replaced with unified **Phase 6 — Action Items & Trello Integration**. |
| 137 | Week ranges adjusted | Timeline reduced by ~3 weeks due to removal of Meeting Memory, RAG, and generic Task Review Queue. Total timeline: 16–17 weeks. |
| 138 | Cross-references updated | All internal references updated to reflect new Phase 6 structure. Removed SignalR, Meeting Memory, and RAG references throughout plan. |

## v3.7 Changes — Flow-2: Agent-Callable API Surface + Reminder Refactor (2026-04-24)

| # | Change | Detail |
|---|--------|--------|
| 113 | LiveKit live agent declared external | The STT→LLM→TTS agent runs as a LiveKit Agents Framework worker hosted outside this backend. The backend's only contract is the endpoint surface defined in Phase 5.7 + the agent service-identity auth scheme. |
| 114 | New Phase 5.7 — Agent-Callable API Surface | New phase after Phase 5.6 (Reminders) and before Phase 6 (AI Pipeline). 2 controllers (AgentReminder, AgentContext), 9 endpoint files. All under `/api/agent/*` with `[Authorize(Policy="AgentOnly")]`. |
| 115 | Agent service-identity JWT scheme added | Separate JWT issuer with claims `agent=true`, `organizationId`, `meetingId`. Minted at meeting-room creation time. Distinct from user JWT — agents and users cannot cross-access each other's endpoints. |
| 116 | `Reminder` entity refactored | Removed `TaskItemId` linkage; `Text` is now a free string (the thing to be reminded about). Added `Scope` (Personal/Public), `Channel` (User/Agent), `TargetUserId`, `MeetingId` (nullable), `OriginalText`, `DeliveredAtUtc`. Status simplified to `Active/Delivered/Cancelled`. |
| 117 | `Group` scope dropped | Reminder targeting is binary: Personal (one user) or Public (all participants of a meeting). If an agent needs to target a subset, it creates N personal reminders. |
| 118 | `OccurrenceDateUtc` rejected | Reminders cannot target a specific occurrence of a recurring meeting. `ReminderAtUtc` acts as a timing gate — the reminder fires at the first occurrence whose `scheduledStartUtc >= ReminderAtUtc`. After delivery, the reminder is closed. |
| 119 | No Hangfire firing for reminders | `TriggerReminderJob` removed. Reminders are pure data, fetched via endpoints. No SignalR push. |
| 120 | Phase 7 `POST /api/tasks/{taskId}/reminders` removed | Reminders no longer linked to TaskItems. Replaced by `POST /api/me/reminders` (user-created, standalone Personal). |
| 121 | New user-facing reminder endpoints (Phase 5.6) | `POST /api/me/reminders`, `GET /api/me/reminders`, `POST /api/me/reminders/{id}/mark-delivered`, `DELETE /api/me/reminders/{id}`. All `[Authorize]` user-JWT. User-created reminders have `MeetingId=null` always (standalone). |
| 122 | New agent-facing reminder endpoints (Phase 5.7) | `POST /api/agent/meetings/{meetingId}/reminders`, `GET /api/agent/meetings/{meetingId}/reminders`, `POST /api/agent/reminders/{id}/mark-delivered`. Agent can create Personal or Public; can fetch only Public; can mark delivered only on Public. |
| 123 | Reminder fetch semantics — `ReminderAtUtc` timing gate | User query: `Status=Active AND ReminderAtUtc <= now`. Agent query: `Status=Active AND Scope=Public AND ReminderAtUtc <= meeting.scheduledStartUtc`. Lets agent express "remind us in 2 weeks at the standup" without per-occurrence targeting. |
| 124 | User's `GET /api/me/reminders` returns Personal AND Public | Returns all reminders affecting the user: `(TargetUserId=me) OR (Scope=Public AND MeetingId IN <my meetings>)`. Includes both user-created Personal and agent-created Public for meetings the user participates in. |
| 125 | Agent context endpoints — focused, not mega | Six focused endpoints: `/api/agent/organization`, `/api/agent/meetings/{id}/members`, `/api/agent/meetings`, `/api/agent/meetings/{id}`, `/api/agent/meetings/recurring`, `/api/agent/meeting-tags`. Each maps 1:1 to an LLM tool — better for token efficiency than one mega-endpoint. |
| 126 | `GET /api/agent/meetings/{meetingId}/members` scoped to current participants | Returns only `MeetingParticipant` of the meeting, not the whole org roster. Each row: `{ userId, displayName, jobRole, context }`. |
| 127 | Past-meeting summaries deferred | RAG / Meeting Memory removed from current plan. May be reintroduced in future phases. |
| 128 | Defence-in-depth: agent never sees Personal reminders | `ListMeetingRemindersEndpoint` hard-filters `Scope=Public` at the service layer. Even if a query string requests Personal, returns empty. |
| 129 | Recurring meeting model: Model A confirmed | One `Meeting` row per series with `RecurrenceConfig` JSONB. Reminders link to the series MeetingId; `ReminderAtUtc` resolves the target occurrence. Matches Google Calendar / Outlook semantics. |
| 130 | Domain events updated | `ReminderTriggeredEvent` removed. Added `ReminderDeliveredEvent`, `ReminderCancelledEvent`, plus `AgentReminderCreatedEvent` and `AgentReminderDeliveredEvent` for audit observability. |
| 131 | Phase 9 audit expanded | Added agent surface audit checklist: agent JWT key rotation, every agent endpoint enforces policy, `meetingId` claim verification, hard-filter on Personal reminders, cross-token rejection, member endpoint scope verification. |
| 132 | Timeline +2 weeks | Phase 5.6 (+1 week) and Phase 5.7 (+1 week) inserted before Phase 6. Total: 19–20 weeks (was 17–18). |
| 133 | Endpoint summary updated | Phase 5.6 adds 1 controller / 4 endpoint files (user reminders). Phase 5.7 adds 2 controllers / 9 endpoint files (agent surface). Phase 7 reduced to 2 controllers / 10 endpoint files (reminders moved out). New totals: 21 controllers, 57 endpoint files. |

## v3.6 Changes — Flow-1 Realignment: Post-Meeting STT Direction (2026-04-24)

| # | Change | Detail |
|---|--------|--------|
| 95 | Live STT removed from Phase 4 | LiveKit Cloud's built-in transcription agent is no longer used. Live captions are out of scope in v3.6. All transcription is post-meeting. Documented in new Phase 4.5 (amendment). Phase 4 proper remains frozen per the phase-edit rule. |
| 96 | New Phase 4.5 — Realtime Pipeline Amendment | Documentation-only phase that supersedes the live-transcription portion of Phase 4. Restricts `TranscriptController` access to OrgAdmin / debug policy. Removes live-transcription SignalR status events. No new entities, endpoints, or jobs. |
| 97 | Phase 5 refactored to "Participant Audio Egress & Storage" | `Recording` entity removed entirely. Replaced with `ParticipantAudioTrack` (per-participant audio track, bound by `MeetingId`). Video recording out of scope. |
| 98 | `ParticipantAudioTrack` entity added | Fields: `Id, MeetingId, OrganizationId, ParticipantUserId, CloudStorageUrl, LocalFilePath, DurationSeconds, SizeBytes, Status, CreatedAtUtc, UpdatedAtUtc`. Indexed on `(MeetingId, Status)` for join-barrier checks. |
| 99 | `DownloadRecordingJob` → `DownloadParticipantAudioJob` | Fans out: one job per participant track. Join-barrier logic emits `ParticipantAudioReadyEvent` once when all tracks for a `MeetingId` reach `Status=Available`. |
| 100 | LiveKit Egress configured in track-based mode | Per-participant audio track egress (not room-composite). |
| 101 | Phase 5 endpoints removed | `RecordingController`, `GetRecordingEndpoint`, `ListRecordingsEndpoint` deleted. Participant audio tracks are internal pipeline artifacts with no user-facing surface in v3.6. |
| 102 | New Phase 5.5 — Post-Meeting STT | Converts per-participant audio to speaker-attributed `TranscriptSegment` rows via Hangfire. 0 endpoints. |
| 103 | `ISpeechToTextService` abstraction added | Provider-agnostic STT interface (OpenAI `/v1/audio/transcriptions` / Whisper-compatible standard). Registered via DI alongside `ILLMService` / `IEmbeddingService`. |
| 104 | `TranscribeParticipantAudioJob` + `SttOrchestratorJob` added | Orchestrator fan-outs per track; each job fetches from MinIO, calls STT, writes `TranscriptSegment` rows with speaker attribution (1:1 from `ParticipantAudioTrack.ParticipantUserId`). |
| 105 | No merge job / no merged transcript artifact | `SummarizeTranscriptJob` does `ORDER BY StartTime` on persisted segments at prompt-assembly time. LLM-based merge rejected (token cost, nondeterminism, no consumer for merged output). |
| 106 | `SummarizeTranscriptJob` trigger changed | `MeetingEndedEvent` → `MeetingTranscriptReadyEvent` (from Phase 5.5). The Meeting-Ended lifecycle event now drives egress (Phase 5), not summarization. |
| 107 | Transcription Flush Ready-Check removed | Bounded file-based STT produces a complete transcript by construction before summarization runs. |
| 108 | Speaker attribution trivial | Each `ParticipantAudioTrack` row identifies its speaker via `ParticipantUserId`. No diarization. 1:1 mapping to `TranscriptSegment.SpeakerUserId`. |
| 109 | Transcript debug-only access | `GET /api/meetings/{meetingId}/transcript` restricted to OrgAdmin / debug policy. Transcripts are internal; users consume the summary (Phase 6). Reads ordered `TranscriptSegment` rows from PostgreSQL — no MinIO involvement. |
| 110 | Timeline +1 week net | Phase 4 reduced by ~0.5 week (no live STT work); Phase 5.5 adds 1 week; net +1 week rounded. Downstream phases shift accordingly. Total: 16–17 weeks (was 15–16). |
| 111 | Compliance row updated | Removed "Transcription flush ready-check" as the data-pipeline-integrity mechanism. Replaced with "bounded file-based STT". |
| 112 | Endpoint summary updated | Phase 5 drops 1 controller / 2 endpoints (Recording removed). Phase 4.5 and 5.5 add 0 endpoints. New totals: 19 controllers, 45 endpoint files. |

## v3.5 Changes — Meeting Tags in Phase 2 (2026-04-07)

| # | Change | Detail |
|---|--------|--------|
| 91 | `MeetingTag` moved back to Phase 2 (Organizations) | Reverses decision #1 (v1). MeetingTag is an organization-scoped tag pool with CRUD, soft delete (`IsActive`), and optional color. Entity defined in Phase 2 with `MeetingTagController` (4 endpoints). Phase 3 defines the `MeetingMeetingTag` junction table for many-to-many relationship with meetings. |
| 92 | Phase 2 expanded to 4 controllers, 12 endpoint files | Added `MeetingTagController` with `ListMeetingTagsEndpoint`, `CreateMeetingTagEndpoint`, `UpdateMeetingTagEndpoint`, `DeleteMeetingTagEndpoint`. New service: `IMeetingTagService` / `MeetingTagService`. New validators: `CreateMeetingTagRequestValidator`, `UpdateMeetingTagRequestValidator`. |
| 93 | MeetingTag domain events added | `MeetingTagCreatedEvent`, `MeetingTagUpdatedEvent`, `MeetingTagDeletedEvent` added to Phase 2 domain events. |
| 94 | Phase 3 `MeetingTag` entity replaced with junction table | Phase 3 no longer defines `MeetingTag` entity. Instead defines `MeetingMeetingTag` junction table (composite PK: `MeetingId`, `MeetingTagId`) for many-to-many. |

## v3.4 Changes — Simplified Membership Model (2026-03-13)

| # | Change | Detail |
|---|--------|--------|
| 81 | Simplified membership model | Each user may have only one active `UserOrgMembership` at a time. DB enforces `UNIQUE(user_id) WHERE is_enabled = true`. Replaces multi-org model. |
| 82 | `UserOrganization` renamed to `UserOrgMembership` | All references updated across the plan. Entity now includes: `UserId`, `OrganizationId`, `OrgRole`, `JobRole`, `Context`, `ContextStatus`, `IsEnabled`. |
| 83 | Registration requires organization | Two flows: (A) register + create org (Admin), (B) register via invitation (Member). No standalone registration allowed. |
| 84 | JWT includes `organizationId` | Access tokens now include `userId` + `organizationId` claims. `organizationId` resolved from active `UserOrgMembership` during login. |
| 85 | SignalR hub simplified | Hub reads `organizationId` from JWT on connect, adds to exactly one group. No DB query for multiple memberships. |
| 86 | `RegisterWithInviteEndpoint` added | New endpoint: `POST /api/auth/register/invite` for Scenario B registration. Phase 1 now has 8 endpoint files. |
| 87 | `LeaveOrganizationEndpoint` added | New endpoint: `POST /api/organizations/{orgId}/members/leave`. Deactivates `UserOrgMembership`. Phase 2 now has 8 endpoint files. |
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
| 68 | Phase 6 restructured | New Phase 6 (Action Items & Trello) replaces old Phases 6, 6.5, 7, 8. 3 controllers, 11 endpoint files. |
| 69 | Phase 9 audit updated | Removed SignalR audit checks. Retained tenant isolation and Partial Controller Pattern audits. |
| 70 | Phase 10 documentation updated | Removed RAG and SignalR diagrams. Added Action Item + Trello flow documentation. |
| 71 | Best practices reference section retained | Full DO/DON'T table and testing strategy for the Partial Controller Pattern. |
| 72 | Constitution bumped to v1.3.3 | Added endpoint architecture rule: ASP.NET Controllers with Partial Controller Pattern. |
| 73 | Compliance table updated | Removed SignalR and Meeting Memory rows. Added Action Items & Trello integration row. |

## v3.2 Changes — Consistency Pass (2026-03-06)

| # | Change | Detail |
|---|--------|--------|
| 45 | `IntegrationMapping`: added `OrganizationId` | Required for `AppDbContext` global query filter tenant isolation (constitution §II). Without it, direct queries bypass tenant isolation. |
| 46 | Phase 6.5 header removed | Old Phase 6.5 (Meeting Memory RAG) removed. Content merged into new Phase 6. |
| 47 | Phase 8 SignalR notifications removed | No SignalR notifications for Trello sync. Status is poll-based via API. |
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
| 39 | Transcription flush ready-check removed | Bounded file-based STT (Phase 5.5) guarantees complete transcript before summarization. Flush ready-check no longer applicable. |
| 40 | Phase 10 documentation updated | Removed RAG and SignalR diagrams. Added Action Item + Trello flow diagrams. |

## v3 Changes (2026-03-06, retained)

| # | Change | Detail |
|---|--------|--------|
| 27 | `IEmbeddingService` abstraction retained | Provider-agnostic embedding interface kept in DI for future use. Not actively used in current phase plan. |
| 28 | Terminology clarified | **Member Context** = `UserOrgMembership.Context` (human-managed, Phase 2). Used for action item assignee resolution via LLM roster matching. |
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
| 9 | Missing `Reminder` entity | Added to Phase 7 (renumbered to Phase 5.6 in v3.8) |
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
| §I Tech Stack — Database | "pgvector (reserved for future use)" | "pgvector available for future vector features" |
| §I Tech Stack — AI | "`ILLMService` abstraction" | "`ILLMService` + `IEmbeddingService` abstractions" |
| §I Endpoint Architecture | Not specified | "ASP.NET Controllers with Partial Controller Pattern. One endpoint per file via partial classes. No Minimal APIs." |
| § Performance Constraints | "No synchronous AI calls" (absolute) | No exceptions — all AI processing async via Hangfire |
| §IV SignalR Usage | "SignalR messages MUST be scoped to `org:{OrganizationId}` Groups. `Clients.All` MUST NOT be used." | SignalR removed from current scope. No real-time notifications required. |
| §IV — LiveKit Cloud | Already updated in v1.1.0 | No change |
| §I — Single deployable | Already resolved in v1.1.0 | No change |

---

# End of Revised Implementation Plan (v3.9)

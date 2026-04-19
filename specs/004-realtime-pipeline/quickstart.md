# Quickstart: Realtime Session Pipeline (Phase 4)

**Branch**: `004-realtime-pipeline` | **Date**: 2026-04-18

## Prerequisites

- Phase 1 (Identity), Phase 2 (Organizations), and Phase 3 (Meetings) fully implemented with green tests.
- PostgreSQL running with the Phase 3 migration applied (`Meetings`, `MeetingParticipants`, `MeetingMeetingTags` tables exist).
- .NET 10 SDK installed.
- A LiveKit Cloud project provisioned with:
  - An API key / secret pair (stored in `LiveKit:ApiKey` and `LiveKit:ApiSecret` user secrets).
  - The LiveKit Cloud server URL (`LiveKit:ServerUrl`, e.g., `wss://meetingassistant.livekit.cloud`).
  - A webhook destination pointing at the backend's `/api/webhooks/livekit` URL with a shared webhook secret (same value as `LiveKit:ApiSecret` unless LiveKit lets us configure a dedicated webhook secret — set `LiveKit:WebhookSecret` to whichever one the dashboard asks us to sign with).
  - Transcription enabled on the project (LiveKit Cloud's built-in STT / Agents Framework).

## Implementation Order

### Step 1: NuGet dependency

Add the LiveKit Server SDK to `MeetingAssistant/MeetingAssistant.csproj`:

```xml
<PackageReference Include="Livekit.Server.Sdk" Version="<latest>" />
```

Also ensure `Microsoft.AspNetCore.SignalR` is referenced (it comes transitively via ASP.NET Core but may need an explicit reference depending on the template).

### Step 2: Models & Enums

Create under `Features/LiveSession/Models/`:

1. `SessionEventType.cs` — enum (RoomStarted, RoomFinished, ParticipantJoined, ParticipantLeft, TranscriptionStarted, TranscriptionPaused, TranscriptionResumed, TranscriptionFinished, TranscriptSegmentStored, Unknown)
2. `SessionPermissions.cs` — `readonly record struct` with `CanPublish, CanSubscribe, CanModerate, CanPublishData` and a static `ForRole(MeetingRole)` method
3. `TranscriptSegment.cs` — entity inheriting `BaseEntity`, implementing `IHasOrganizationId`
4. `SessionEvent.cs` — entity inheriting `BaseEntity`, implementing `IHasOrganizationId`
5. `Events/LiveSessionEvents.cs` — MediatR notifications: `SessionStartedEvent`, `SessionEndedEvent`, `TranscriptSegmentIngestedEvent`, `TranscriptionStateChangedEvent`

### Step 3: Entity Configurations

Create under `Features/LiveSession/Infrastructure/Persistence/Configurations/`:

1. `TranscriptSegmentConfiguration.cs` — unique index on `(MeetingId, SequenceNumber)`; check constraint `EndMs >= StartMs`; global query filter by `OrganizationId`.
2. `SessionEventConfiguration.cs` — unique index on `ExternalEventId`; `PayloadJson` mapped to `jsonb`; global query filter by `OrganizationId`.

### Step 4: DbContext registration + migration

Add to `ApplicationDbContext`:

```csharp
public DbSet<TranscriptSegment> TranscriptSegments => Set<TranscriptSegment>();
public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
```

Generate migration:

```bash
dotnet ef migrations add AddLiveSession -p MeetingAssistant -s MeetingAssistant
```

### Step 5: Contracts

Create request/response records under `Features/LiveSession/Contracts/`. See [contracts/](contracts/) for the full schemas:

- `Requests/JoinTokenRequest.cs`
- `Responses/JoinTokenResponse.cs`
- `Responses/TranscriptResponse.cs`
- `Responses/TranscriptSegmentResponse.cs`

### Step 6: Errors

Create `Shared/Errors/LiveSessionErrors.cs` mirroring the `OrganizationErrors` / `MeetingErrors` pattern:

- `NotAParticipant`
- `MeetingNotJoinable`
- `ForbiddenForRole`
- `TranscriptionNotPausable`
- `TranscriptionNotResumable`
- `InvalidWebhookSignature`
- `LiveKitCallFailed`

### Step 7: LiveKit seams + SignalR

Create under `Features/LiveSession/Services/`:

1. `ILiveKitTokenIssuer` / `LiveKitTokenIssuer` — wraps `AccessToken` from the LiveKit SDK. Accepts `(meetingId, userId, displayName, SessionPermissions)` and returns `(accessToken, expiresAtUtc)`. TTL = 15 minutes.
2. `ILiveKitWebhookValidator` / `LiveKitWebhookValidator` — wraps `WebhookReceiver`. Accepts `(rawBody, authorizationHeader)`; returns a parsed `WebhookEvent` or a validation error.
3. `ILiveKitRoomAdmin` / `LiveKitRoomAdmin` — wraps LiveKit admin calls used by pause/resume.

Create under `Features/LiveSession/Hubs/`:

1. `LiveSessionHub.cs` — SignalR hub. `OnConnectedAsync` reads the JWT's `organizationId` claim and calls `Groups.AddToGroupAsync(ConnectionId, $"org:{organizationId}")`.
2. `ILiveSessionNotifier` / `LiveSessionNotifier.cs` — wraps `IHubContext<LiveSessionHub>`. Exposes only group-scoped send methods (`NotifySessionStartedAsync`, `NotifySessionEndedAsync`, `NotifyParticipantJoinedAsync`, `NotifyParticipantLeftAsync`, `NotifyTranscriptionStateChangedAsync`). **No** `NotifyAll` method.

### Step 8: Validators

Create under `Features/LiveSession/Validators/`:

1. `JoinTokenRequestValidator.cs` — `displayName` length 1–120 if present.

### Step 9: Services

Implement under `Features/LiveSession/Services/` in this order:

1. `ISessionService` / `SessionService`
   - `IssueJoinTokenAsync(meetingId, callerUserId, displayName)` — look up participant, map role → permissions, call `ILiveKitTokenIssuer`, return `JoinCredential`.
   - `PauseTranscriptionAsync(meetingId, callerUserId)` — authorize (Host / CoHost only), call `ILiveKitRoomAdmin`.
   - `ResumeTranscriptionAsync(meetingId, callerUserId)` — same.
2. `ITranscriptService` / `TranscriptService`
   - `GetAsync(meetingId, callerUserId)` — participant check + ordered segment read.
   - `IngestAsync(segmentIngestRequest)` — called by `WebhookService`; single-row insert with dedup-on-unique.
3. `IWebhookService` / `WebhookService`
   - `ProcessAsync(validatedWebhookEvent)` — dispatch by event type, wrap in transaction with `SessionEvents` insert.

### Step 10: Endpoints

Create partial controllers and endpoint files under `Features/LiveSession/Endpoints/` per the layout in `plan.md`. Apply `[Authorize]` + `[EnforceOrgAccess]` on `SessionController` and `TranscriptController`. **Do NOT** apply `[Authorize]` to `WebhookController` — the endpoint authenticates by signature validation inside the action (R-015).

On the webhook endpoint, read the raw body ONCE before model binding:

```csharp
Request.EnableBuffering();
using var reader = new StreamReader(Request.Body, leaveOpen: true);
var rawBody = await reader.ReadToEndAsync();
Request.Body.Position = 0;
```

Hand `rawBody` + `Authorization` header to the validator before any deserialization-driven logic.

### Step 11: DI registration

Create `Features/LiveSession/LiveSessionDI.cs`:

```csharp
public static IServiceCollection AddLiveSessionFeature(this IServiceCollection services)
{
    services.AddScoped<ISessionService, SessionService>();
    services.AddScoped<ITranscriptService, TranscriptService>();
    services.AddScoped<IWebhookService, WebhookService>();
    services.AddScoped<ILiveKitTokenIssuer, LiveKitTokenIssuer>();
    services.AddScoped<ILiveKitWebhookValidator, LiveKitWebhookValidator>();
    services.AddScoped<ILiveKitRoomAdmin, LiveKitRoomAdmin>();
    services.AddScoped<ILiveSessionNotifier, LiveSessionNotifier>();
    return services;
}
```

In `Program.cs`:

```csharp
builder.Services.AddLiveSessionFeature();
builder.Services.AddSignalR();
// ...
app.MapHub<LiveSessionHub>("/hubs/live-session").RequireAuthorization();
```

### Step 12: Mapping

Create `Features/LiveSession/Mapping/LiveSessionMappingConfig.cs` — Mapster configs for `TranscriptSegment → TranscriptSegmentResponse`.

### Step 13: Tests

**Unit** (`tests/Unit/LiveSession/`):

- `RolePermissionMappingTests` — assert the full `SessionPermissions.ForRole` matrix.
- `WebhookIdempotencyTests` — same `ExternalEventId` delivered twice → one row, no duplicate side effects.
- `TranscriptDeduplicationTests` — duplicate `(MeetingId, SequenceNumber)` → `IngestAsync` returns success without inserting a second row.

**Integration** (`tests/Integration/LiveSession/`):

- `JoinTokenTests` — participant gets token with correct permissions per role; non-participant gets 403; cancelled/completed meeting gets 409; expired token is rejected by LiveKit (asserted against issued `exp` claim, no live call).
- `WebhookIngestionTests` — signed `room_started` transitions meeting; unsigned payload → 401; replayed signed payload → 200 with no duplicate transition; transcription segment ingestion persists to DB.
- `TranscriptRetrievalTests` — participant gets ordered segments; non-participant gets 403; empty transcript returns `[]`.
- `TranscriptionControlTests` — Host can pause; CoHost can pause; Participant gets 403; Observer gets 403.

## Key Patterns to Follow

| Pattern | Reference File |
|---------|---------------|
| Entity with tenant isolation | `Features/Meetings/Models/Meeting.cs` |
| Service with Result pattern | `Features/Meetings/Services/MeetingService.cs` |
| Partial controller definition | `Features/Meetings/Endpoints/Meeting/MeetingController.cs` |
| Endpoint file (one action per file) | `Features/Meetings/Endpoints/Meeting/CreateMeetingEndpoint.cs` |
| Error catalog | `Shared/Errors/MeetingErrors.cs` |
| Entity configuration (global query filter) | `Features/Meetings/Infrastructure/Persistence/Configurations/MeetingConfiguration.cs` |
| Validator | `Features/Meetings/Validators/CreateMeetingRequestValidator.cs` |
| DI registration | `Features/Meetings/MeetingsDI.cs` |
| Domain events | `Features/Meetings/Models/Events/MeetingEvents.cs` |

## Local development notes

- LiveKit Cloud webhooks cannot reach `localhost` directly. During development, run `ngrok http 5000` (or equivalent) and set the LiveKit Cloud webhook URL to the ngrok tunnel for your current session.
- The LiveKit Server SDK needs API key / secret at DI composition time. Prefer `dotnet user-secrets` for local development, environment variables in CI, and a proper secret store in production.
- A small console harness (`scripts/livekit-webhook-replay.csx` or similar) is helpful for replaying captured webhook bodies against a running backend — build this on demand rather than committing replay fixtures wholesale.

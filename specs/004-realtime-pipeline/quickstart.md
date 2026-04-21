# Quickstart: Realtime Session Pipeline (Phase 4)

**Branch**: `004-realtime-pipeline` | **Date**: 2026-04-18 | **Last Updated**: 2026-04-21

> **Revision note (2026-04-21)**: Transcript persistence, transcript retrieval, and transcription pause/resume are no longer Phase 4 scope. The recording pipeline has been reduced to the MVP minimum: one entity (`Recording`), one job (`DownloadRecordingJob`), one essential webhook (`egress_ended`), no domain events for recording, no reconciliation. MinIO is the integration boundary with Phase 6.

## Prerequisites

- Phase 1 (Identity), Phase 2 (Organizations), and Phase 3 (Meetings) fully implemented with green tests.
- PostgreSQL running with the Phase 3 migration applied.
- .NET 10 SDK installed.
- **MinIO** (or another S3-compatible object store) reachable from the backend for recording storage.
- **Hangfire** registered on the host (required by `DownloadRecordingJob`).
- A LiveKit Cloud project provisioned with:
  - An API key / secret pair (`LiveKit:ApiKey`, `LiveKit:ApiSecret` via user secrets).
  - LiveKit Cloud server URL (`LiveKit:ServerUrl`, e.g., `wss://meetingassistant.livekit.cloud`).
  - A webhook destination pointing at the backend's `/api/webhooks/livekit` URL with a shared webhook secret (`LiveKit:WebhookSecret`).
  - **Egress enabled** for room recording, writing to LiveKit Cloud managed storage.
  - Live transcription on the platform side is OPTIONAL (captions delivered directly to clients); it is NOT required by the backend.

## Implementation Order

### Step 1: NuGet dependencies

Add to `MeetingAssistant/MeetingAssistant.csproj`:

```xml
<PackageReference Include="Livekit.Server.Sdk" Version="<latest>" />
<PackageReference Include="Hangfire.AspNetCore" Version="<latest>" />
<PackageReference Include="Hangfire.PostgreSql" Version="<latest>" />
<PackageReference Include="Minio" Version="<latest>" />
```

Ensure `Microsoft.AspNetCore.SignalR` is referenced.

### Step 2: Models & Enums

Create under `Features/LiveSession/Models/`:

1. `SessionEventType.cs` — enum (`RoomStarted`, `RoomFinished`, `ParticipantJoined`, `ParticipantLeft`, `RecordingStarted`, `EgressEnded`, `Unknown`).
2. `RecordingStatus.cs` — enum (`Pending`, `Completed`, `Failed`).
3. `SessionPermissions.cs` — `readonly record struct` with `CanPublish, CanSubscribe, CanModerate, CanPublishData` and a static `ForRole(MeetingRole)` method.
4. `SessionEvent.cs` — entity inheriting `BaseEntity`, implementing `IHasOrganizationId`.
5. `Recording.cs` — entity inheriting `BaseEntity`, implementing `IHasOrganizationId`. Fields: `MeetingId`, `OrganizationId`, `FilePath?`, `Status`.
6. `Events/LiveSessionEvents.cs` — MediatR notifications: `SessionStartedEvent`, `SessionEndedEvent`. **No recording events.**

> **Removed in 2026-04-21**: `TranscriptSegment.cs`, `RecordingAvailableEvent`, `RecordingFailedEvent`. Phase 4 does not publish recording domain events — Phase 6 reads MinIO directly.

### Step 3: Entity Configurations

Create under `Features/LiveSession/Infrastructure/Persistence/Configurations/`:

1. `SessionEventConfiguration.cs` — unique index on `ExternalEventId`; `PayloadJson` mapped to `jsonb`; global query filter by `OrganizationId`.
2. `RecordingConfiguration.cs` — unique index on `MeetingId` (one recording per meeting, also the idempotency primitive); `FilePath` nullable, max 500 chars; global query filter by `OrganizationId`.

### Step 4: DbContext registration + migration

Add to `ApplicationDbContext`:

```csharp
public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
public DbSet<Recording> Recordings => Set<Recording>();
```

Generate migration:

```bash
dotnet ef migrations add AddLiveSession -p MeetingAssistant -s MeetingAssistant
```

### Step 5: Contracts

Create request/response records under `Features/LiveSession/Contracts/`. See [contracts/](contracts/) for the full schemas:

- `Requests/JoinTokenRequest.cs`
- `Responses/JoinTokenResponse.cs`

> **Removed in 2026-04-21**: `Responses/TranscriptResponse.cs`, `Responses/TranscriptSegmentResponse.cs`. No transcript endpoint in this phase.

### Step 6: Errors

Create `Shared/Errors/LiveSessionErrors.cs`:

- `NotAParticipant`
- `MeetingNotJoinable`
- `InvalidWebhookSignature`
- `LiveKitCallFailed`
- `MeetingNotFound`
- `RecordingDownloadFailed`

> **Removed in 2026-04-21**: `ForbiddenForRole`, `TranscriptionNotPausable`, `TranscriptionNotResumable` (pause/resume endpoints are gone).

### Step 7: LiveKit seams + SignalR

Create under `Features/LiveSession/Services/`:

1. `ILiveKitTokenIssuer` / `LiveKitTokenIssuer` — wraps `AccessToken` from the LiveKit SDK. TTL = 15 minutes.
2. `ILiveKitWebhookValidator` / `LiveKitWebhookValidator` — wraps `WebhookReceiver`.
3. `IStorageService` / `StorageService` — thin MinIO wrapper. Only the operations the download job needs: upload from a cloud URL (streamed) to a given object key; optionally `GetObjectStream(key)` for future readers.

Create under `Features/LiveSession/Hubs/`:

1. `LiveSessionHub.cs` — SignalR hub. `OnConnectedAsync` reads the JWT's `organizationId` claim and calls `Groups.AddToGroupAsync`.
2. `ILiveSessionNotifier` / `LiveSessionNotifier.cs` — wraps `IHubContext<LiveSessionHub>`. Exposes only group-scoped send methods for **session lifecycle**: `NotifySessionStartedAsync`, `NotifySessionEndedAsync`, `NotifyParticipantJoinedAsync`, `NotifyParticipantLeftAsync`. **No** `NotifyAll` method, **no** `NotifyRecording*` methods.

### Step 8: Validators

`Features/LiveSession/Validators/JoinTokenRequestValidator.cs` — `displayName` length 1–120 if present.

### Step 9: Services

Implement under `Features/LiveSession/Services/`:

1. `ISessionService` / `SessionService`
   - `IssueJoinTokenAsync(meetingId, callerUserId, displayName)` — look up participant, map role → permissions, call `ILiveKitTokenIssuer`, return `JoinTokenResponse`.
2. `IWebhookService` / `WebhookService`
   - `ProcessAsync(validatedWebhookEvent)` — dispatches by event type inside a transaction that also inserts the `SessionEvent` row:
     - `HandleRoomStarted`, `HandleRoomFinished`, `HandleParticipantJoined`, `HandleParticipantLeft` — lifecycle + notifier fan-out.
     - `HandleEgressEnded` — **the primary recording handler**. Upserts `Recording(MeetingId, Status=Pending)` on success variant and enqueues `DownloadRecordingJob(meetingId, sourceCloudUrl)`; on failure variant upserts `Recording(MeetingId, Status=Failed)` and does not enqueue.
     - `HandleRecordingStarted` *(optional)* — if enabled, upserts `Recording(MeetingId, Status=Pending)` for audit; otherwise the event is persisted as `Unknown` and ignored.
     - `HandleUnknown` — persists `SessionEvent` with `EventType = Unknown` and returns 200.

> **Removed in 2026-04-21 MVP**: `IRecordingService` / `RecordingService`. The `Recording` row is simple enough (four fields) that `WebhookService` and `DownloadRecordingJob` manage it directly via `DbContext`. If row manipulation ever grows, it can be extracted without changing the public surface.

### Step 10: Jobs

Create under `Features/LiveSession/Jobs/`:

1. `DownloadRecordingJob.cs` — Hangfire job. Signature: `RunAsync(Guid meetingId, string sourceCloudUrl, CancellationToken ct)`.
   - Re-reads the `Recording` row by `MeetingId` on each attempt; exits early if `Status ∈ {Completed, Failed}`.
   - Downloads from `sourceCloudUrl`; uploads to MinIO at the deterministic key `recordings/{MeetingId}.mp4` via `IStorageService`.
   - On success: sets `FilePath = recordings/{MeetingId}.mp4`, `Status = Completed`.
   - On terminal failure (after Hangfire's retry budget): sets `Status = Failed`.
   - **Does not publish any MediatR events.**

> **Removed in 2026-04-21 MVP**: `SessionReconciliationJob.cs`. No reconciliation sweep in Phase 4 — acceptable MVP behaviour per the spec.

### Step 11: Endpoints

Create partial controllers under `Features/LiveSession/Endpoints/`:

- `Session/SessionController.cs` + `GetJoinTokenEndpoint.cs` — `[Authorize]` + `[EnforceOrgAccess]`.
- `Webhook/WebhookController.cs` + `LiveKitWebhookEndpoint.cs` — **no `[Authorize]`** (R-015).

On the webhook endpoint, read the raw body ONCE before model binding:

```csharp
Request.EnableBuffering();
using var reader = new StreamReader(Request.Body, leaveOpen: true);
var rawBody = await reader.ReadToEndAsync();
Request.Body.Position = 0;
```

Hand `rawBody` + `Authorization` header to the validator before any deserialization-driven logic.

> **Removed in 2026-04-21**: `Transcript/TranscriptController.cs`, `Transcript/GetTranscriptEndpoint.cs`, `Session/PauseTranscriptionEndpoint.cs`, `Session/ResumeTranscriptionEndpoint.cs`.

### Step 12: DI registration

Create `Features/LiveSession/LiveSessionDI.cs`:

```csharp
public static IServiceCollection AddLiveSessionFeature(this IServiceCollection services)
{
    services.AddScoped<ISessionService, SessionService>();
    services.AddScoped<IWebhookService, WebhookService>();
    services.AddScoped<ILiveKitTokenIssuer, LiveKitTokenIssuer>();
    services.AddScoped<ILiveKitWebhookValidator, LiveKitWebhookValidator>();
    services.AddScoped<IStorageService, StorageService>();
    services.AddScoped<ILiveSessionNotifier, LiveSessionNotifier>();
    return services;
}
```

In `Program.cs`:

```csharp
builder.Services.AddLiveSessionFeature();
builder.Services.AddSignalR();
// Hangfire registered as part of the shared infrastructure.

app.MapHub<LiveSessionHub>("/hubs/live-session").RequireAuthorization();
```

> **Removed in 2026-04-21 MVP**: the `RecurringJob.AddOrUpdate<SessionReconciliationJob>(...)` wiring. There is no recurring reconciliation in Phase 4.

### Step 13: Mapping

Create `Features/LiveSession/Mapping/LiveSessionMappingConfig.cs` — Mapster configs for `Recording → RecordingResponse` if/when a later phase exposes a read endpoint. In the MVP, no response mapping for `Recording` is required (Phase 6 reads the entity directly).

### Step 14: Tests

**Unit** (`tests/Unit/LiveSession/`):

- `RolePermissionMappingTests` — full `SessionPermissions.ForRole` matrix.
- `WebhookIdempotencyTests` — same `ExternalEventId` delivered twice → one `SessionEvent` row, no duplicate side effects; duplicate `egress_ended` for the same meeting → one `Recording` row, no duplicate download enqueue.
- `DownloadRecordingJobTests` — exits early on `Completed`/`Failed`; retries transient failures; marks `Failed` after the retry budget; deterministic object key.

**Integration** (`tests/Integration/LiveSession/`):

- `JoinTokenTests` — role-scoped permissions; non-participant → 403; cancelled/completed → 409.
- `WebhookIngestionTests` — signed `room_started` transitions meeting; unsigned payload → 401; replayed signed payload → 200 with no duplicate transition.
- `LifecycleWebhookTests` — `room_started`/`room_finished`/`participant_joined`/`participant_left` behaviours.
- `RecordingHandoffTests` — `egress_ended` (success) creates `Recording(Pending)` and enqueues download; job completion sets `FilePath` + `Status=Completed`; duplicate `egress_ended` is idempotent; failure variant creates `Recording(Failed)` without a download; orphan room (no matching meeting) → 200 without creating a `Recording` (FR-020).
- `TenantScopedNotificationTests` — two orgs; session lifecycle SignalR notifications never cross.

> **Removed in 2026-04-21**: `TranscriptRetrievalTests`, `TranscriptionControlTests`, `ReconciliationTests`. Those surfaces no longer exist.

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

- LiveKit Cloud webhooks cannot reach `localhost` directly. During development, run `ngrok http 5000` (or equivalent) and set the LiveKit Cloud webhook URL to the ngrok tunnel.
- The LiveKit Server SDK needs API key / secret at DI composition time. Prefer `dotnet user-secrets` locally, environment variables in CI, and a proper secret store in production.
- For recording download tests, use a local HTTP server (or `WireMock.Net`) to simulate the LiveKit Cloud storage URL so the test suite never calls out to the real cloud.
- Phase 6 is the consumer of the `Recording` row + the MinIO object. While Phase 6 is not yet implemented, a useful smoke test is: after end-to-end-ing a meeting, confirm the object exists at `recordings/{MeetingId}.mp4` in MinIO and that `Recording.Status = Completed`.

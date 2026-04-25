# Quickstart: Opus Realtime Amendment

This walkthrough verifies the Phase 4.5 deliverables end-to-end against a developer stack. No LiveKit Cloud round-trip is required — the amendment is a scope reduction plus one admin debug endpoint.

## Prerequisites

- `docker-compose up -d` (Postgres + MinIO; see [docker-compose.yml](../../docker-compose.yml)).
- `dotnet build` succeeds at the repo root.
- At least one seeded organization with: one OrgAdmin user, one non-admin org member, one meeting in `Completed` status, one meeting in `InProgress` status.

## 1. Run the test suite

```bash
dotnet test tests/MeetingAssistant.Tests.Integration --filter "FullyQualifiedName~TranscriptDebugEndpoint"
```

All ten scenarios in the contract test checklist (see [contracts/transcript-endpoint.md](./contracts/transcript-endpoint.md)) pass. This is the primary acceptance surface for the phase.

## 2. Manual verification — happy path (Completed meeting, stub reader)

```bash
# 1. Sign in as an OrgAdmin and capture the access token.
TOKEN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@example.com","password":"..."}' | jq -r '.accessToken')

# 2. Request the transcript for an ENDED meeting.
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/organizations/$ORG_ID/meetings/$COMPLETED_MEETING_ID/transcript | jq
```

Expected:
```json
{
  "segments": [],
  "pipelineStatus": "NotStarted"
}
```

Why `NotStarted` and not `Completed`: Phase 5.5 has not landed, so the DI-registered `ITranscriptReadService` is the stub; no `TranscriptSegment` rows exist. This is the intended state for this phase.

## 3. Manual verification — live meeting rejection (FR-012)

```bash
curl -is -H "Authorization: Bearer $TOKEN" \
  http://localhost:5000/api/organizations/$ORG_ID/meetings/$IN_PROGRESS_MEETING_ID/transcript
```

Expected:
- HTTP `409 Conflict`
- Body (RFC 7807 problem details):
```json
{
  "type":   "https://httpstatuses.io/409",
  "title":  "Meeting is still in progress",
  "status": 409,
  "detail": "Transcript is available only after the meeting has ended. Retry after the meeting completes.",
  "meetingStatus": "InProgress",
  "traceId": "00-…-00"
}
```
- Body MUST NOT contain `segments` or `pipelineStatus` (FR-012).

## 4. Manual verification — non-admin denial (FR-006 / SC-004)

Sign in as a non-admin org member (a Host or Participant of the meeting) and repeat the Completed-meeting request:

```bash
MEMBER_TOKEN=$(curl ...)   # login as a non-admin

curl -is -H "Authorization: Bearer $MEMBER_TOKEN" \
  http://localhost:5000/api/organizations/$ORG_ID/meetings/$COMPLETED_MEETING_ID/transcript
```

Expected: HTTP `403 Forbidden` with an RFC 7807 body. No `segments` leaked.

## 5. SignalR sanity check (FR-002)

With the backend running, connect a SignalR client to the `LiveSessionHub`:

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl('http://localhost:5000/hubs/livesession', { accessTokenFactory: () => TOKEN })
  .build();

connection.on('session.started',    e => log('session.started', e));
connection.on('session.ended',      e => log('session.ended', e));
connection.on('participant.joined', e => log('participant.joined', e));
connection.on('participant.left',   e => log('participant.left', e));

// These MUST NEVER fire — add handlers to assert they don't.
for (const bad of ['transcription.started', 'transcription.progress',
                   'transcription.completed', 'transcription.failed',
                   'transcription.status']) {
  connection.on(bad, () => { throw new Error(`forbidden event: ${bad}`); });
}

await connection.start();
```

Run a full meeting (start → participants join → end) and observe: only the four lifecycle events fire. Any `transcription.*` event is a regression.

## 6. Post-conditions to confirm before declaring the phase done

- [ ] No new EF Core migration is present under `MeetingAssistant/Migrations/` for this phase.
- [ ] No new Hangfire job is registered in [Program.cs](../../MeetingAssistant/Program.cs) or [LiveSessionDI.cs](../../MeetingAssistant/Features/LiveSession/LiveSessionDI.cs) for this phase (beyond the existing `DownloadRecordingJob`).
- [ ] `ITranscriptReadService` is registered in `LiveSessionDI` → `StubTranscriptReadService`.
- [ ] No audit-record entity or write path introduced (Q3 out-of-scope).
- [ ] `LiveSessionNotifier` has no new methods, and no `NotifyTranscription*` method is added anywhere in the codebase.
- [ ] [docs/implementation-plan.md](../../docs/implementation-plan.md) remains the canonical source of truth for the Phase 4.5 amendment; the spec, plan, research, data-model, and contract in this directory are the feature-level artifacts that implement it.

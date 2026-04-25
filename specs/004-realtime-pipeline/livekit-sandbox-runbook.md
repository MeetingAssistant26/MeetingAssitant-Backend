# Runbook: LiveKit Cloud Sandbox E2E (T052)

## Goal

Execute the full Phase 4 recording flow end-to-end against a fresh LiveKit Cloud sandbox and collect evidence for:

1. Join token issuance works.
2. Room lifecycle webhook events are ingested.
3. `egress_ended` fan-outs one participant track per `FileResult`.
4. MinIO objects exist at `tracks/{MeetingId}/{ParticipantUserId}.ogg`.
5. `ParticipantAudioTracks.Status` reaches terminal states (`Available`/`Failed`) and join barrier can fire.
6. Post-meeting pipeline writes `MeetingTranscript` and `MeetingSummary`.

## Prerequisites

1. Local backend builds and runs.
2. PostgreSQL is reachable by the backend connection string.
3. MinIO is reachable and credentials are valid.
4. Hangfire is enabled in the backend host.
5. LiveKit Cloud project exists with webhook support and egress enabled.
6. A public callback URL to local backend exists (for example `ngrok`).
7. Track egress profile is configured as audio-only (no video).

## Required Configuration

Set user secrets for `MeetingAssistant` before running.

```powershell
Set-Location "c:\Users\DEll\source\repos\MeetingAssitant-Backend\MeetingAssistant"

dotnet user-secrets set "LiveKit:ApiKey" "<livekit-api-key>"
dotnet user-secrets set "LiveKit:ApiSecret" "<livekit-api-secret>"
dotnet user-secrets set "LiveKit:ServerUrl" "wss://<project>.livekit.cloud"
dotnet user-secrets set "LiveKit:WebhookSecret" "<livekit-webhook-secret>"

dotnet user-secrets set "Storage:Endpoint" "http://localhost:9000"
dotnet user-secrets set "Storage:AccessKey" "<minio-access-key>"
dotnet user-secrets set "Storage:SecretKey" "<minio-secret-key>"
dotnet user-secrets set "Storage:Bucket" "meeting-recordings"
```

Notes:

1. The backend binds storage settings from `Storage:*` keys.
2. `LiveKit:WebhookSecret` must match the secret configured in LiveKit Cloud webhook settings.

## Start Local Dependencies

If you use the repository docker compose setup:

```powershell
Set-Location "c:\Users\DEll\source\repos\MeetingAssitant-Backend"
docker compose up -d postgres minio
```

MinIO console: `http://localhost:9001`

## Start Backend

```powershell
Set-Location "c:\Users\DEll\source\repos\MeetingAssitant-Backend"
dotnet run --project "MeetingAssistant\MeetingAssistant.csproj"
```

Assume backend base URL is `http://localhost:5112` in the commands below. Adjust if your launch profile uses a different port.

## Expose Webhook Endpoint Publicly

Run a tunnel in a second terminal:

```powershell
ngrok http 5112
```

In LiveKit Cloud project settings, set webhook URL to:

`https://<ngrok-id>.ngrok-free.app/api/webhooks/livekit`

Use the same webhook secret as `LiveKit:WebhookSecret`.

## Seed User, Organization, Meeting, and Get Join Token

Run this PowerShell script in a third terminal.

```powershell
$ErrorActionPreference = "Stop"

$ApiBase = "http://localhost:5112"
$Email = "sandbox-$(Get-Random)@example.com"
$Password = "Passw0rd!123"
$DisplayName = "Sandbox Host"

# 1) Register
$registerBody = @{
    email = $Email
    password = $Password
    displayName = $DisplayName
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri "$ApiBase/api/auth/register" -ContentType "application/json" -Body $registerBody | Out-Null

# 2) Login (initial token)
$loginBody = @{
    email = $Email
    password = $Password
} | ConvertTo-Json

$login = Invoke-RestMethod -Method Post -Uri "$ApiBase/api/auth/login" -ContentType "application/json" -Body $loginBody
$token = $login.token
$headers = @{ Authorization = "Bearer $token" }

# 3) Create organization
$orgBody = @{ name = "Sandbox Org $(Get-Random)" } | ConvertTo-Json
$org = Invoke-RestMethod -Method Post -Uri "$ApiBase/api/organizations" -Headers $headers -ContentType "application/json" -Body $orgBody
$orgId = $org.id

# 4) Login again so JWT includes organizationId claim
$login = Invoke-RestMethod -Method Post -Uri "$ApiBase/api/auth/login" -ContentType "application/json" -Body $loginBody
$token = $login.token
$headers = @{ Authorization = "Bearer $token" }

# 5) Create meeting
$startUtc = (Get-Date).ToUniversalTime().AddMinutes(5).ToString("o")
$endUtc = (Get-Date).ToUniversalTime().AddMinutes(65).ToString("o")

$meetingBody = @{
    title = "LiveKit Sandbox E2E"
    description = "Phase 4 T052 runbook validation"
    scheduledStartUtc = $startUtc
    scheduledEndUtc = $endUtc
    tagIds = @()
} | ConvertTo-Json

$meeting = Invoke-RestMethod -Method Post -Uri "$ApiBase/api/organizations/$orgId/meetings" -Headers $headers -ContentType "application/json" -Body $meetingBody
$meetingId = $meeting.id

# 6) Issue join token
$joinBody = @{ displayName = "Sandbox Host" } | ConvertTo-Json
$join = Invoke-RestMethod -Method Post -Uri "$ApiBase/api/organizations/$orgId/meetings/$meetingId/session/join-token" -Headers $headers -ContentType "application/json" -Body $joinBody

Write-Host "OrganizationId: $orgId"
Write-Host "MeetingId:      $meetingId"
Write-Host "RoomName:       $($join.roomName)"
Write-Host "ServerUrl:      $($join.serverUrl)"
Write-Host "AccessToken:    $($join.accessToken)"
```

Expected result:

1. `join-token` returns `200`.
2. `roomName` is `mtg:{MeetingId}`.

## Configure Track Egress in LiveKit Cloud (Audio-Only)

Before running the room flow, configure egress in LiveKit Cloud as:

1. **Mode:** Track-based egress (one file per participant track).
2. **Media:** Audio only (video disabled).
3. **Container/codec:** OGG/Opus.
4. **Output:** LiveKit-managed storage (backend ingests via webhook-provided locations).
5. **Identity:** Ensure each file result can be mapped to participant identity (identity in payload/filename).

## Join Room and Trigger Egress in LiveKit Cloud

1. Open LiveKit Cloud web tester for your project.
2. Paste `ServerUrl`, `RoomName`, and `AccessToken` from the script output.
3. Join the room as the host.
4. Start track egress (audio-only) from LiveKit Cloud.
5. Keep the session active briefly, then leave/end the room.
6. Wait for webhook deliveries (`room_finished`, then `egress_ended`).

## Evidence Collection Checklist

Collect all items below and attach to the task/PR.

1. Backend logs contain `Issued LiveKit token` with meeting and user ids.
2. Backend logs show webhook processing for room end and recording handoff.
3. Backend logs show `Participant audio ingest completed` with target key `tracks/{MeetingId}/{ParticipantUserId}.ogg`.
4. MinIO bucket contains one object per participant under `tracks/{MeetingId}/`.
5. Database rows in `ParticipantAudioTracks` have `MeetingId = {MeetingId}` with terminal statuses and populated `StorageObjectKey` for available tracks.
6. Database rows in `MeetingTranscripts` and `MeetingSummaries` exist for the meeting when at least one track is available.

Example SQL checks (PostgreSQL):

```sql
SELECT "MeetingId", "ParticipantUserId", "Status", "StorageObjectKey", "SizeBytes", "UpdatedAtUtc"
FROM "ParticipantAudioTracks"
WHERE "MeetingId" = '<MEETING_ID>';

SELECT "ExternalEventId", "EventType", "OccurredAtUtc"
FROM "SessionEvents"
WHERE "MeetingId" = '<MEETING_ID>'
ORDER BY "OccurredAtUtc";

SELECT "MeetingId", "GeneratedAtUtc", "SttModel"
FROM "MeetingTranscripts"
WHERE "MeetingId" = '<MEETING_ID>';

SELECT "MeetingId", "GeneratedAtUtc", "LlmModel"
FROM "MeetingSummaries"
WHERE "MeetingId" = '<MEETING_ID>';
```

If using docker-compose postgres service:

```powershell
docker exec -it meetingassistant-postgres psql -U meetingassistant -d meetingassistant
```

## Pass/Fail Criteria

PASS when all are true:

1. Join token request succeeded.
2. Room was joined in LiveKit tester.
3. `egress_ended` webhook was accepted.
4. MinIO objects exist under `tracks/{MeetingId}/` for successful participant tracks.
5. `ParticipantAudioTracks` rows are terminal (`Available` or `Failed`) and webhook idempotency remains intact.
6. Transcript and summary rows are persisted for meetings with at least one available track.

FAIL when any are true:

1. Webhook rejected due to signature mismatch.
2. No `egress_ended` observed.
3. Download job fails and status is `Failed`.
4. Object missing from MinIO.

## Common Troubleshooting

1. `403` on org-scoped endpoints after creating org:
   - Login again after org creation so token includes `organizationId` claim.
2. Webhook `401`:
   - Verify LiveKit webhook secret exactly matches `LiveKit:WebhookSecret`.
3. Join token works but no participant audio objects appear:
   - Confirm track egress (audio-only) was started and completed in LiveKit Cloud.
   - Check Hangfire dashboard/jobs for `IngestParticipantAudioJob` failures.
4. MinIO connection errors:
   - Verify `Storage:Endpoint`, `Storage:AccessKey`, `Storage:SecretKey`, and `Storage:Bucket`.

# LiveSession Feature — End-to-End Runbook

**Scope**: Per-participant audio egress pipeline (Phase 5) + post-meeting STT & summarization (Phase 5.5)
**Date**: 2026-04-27
**Branch**: `main`
**Prerequisites**: PostgreSQL + MinIO + Backend API running (local dev)

---

## 0. Prerequisites

### 0.1 Infrastructure

```bash
# Terminal 1: Start PostgreSQL + MinIO
cd C:\Users\DEll\source\repos\MeetingAssitant-Backend
docker compose up -d postgres minio

# Verify
pg_isready -h localhost -p 5432
# MinIO console: http://localhost:9001 (minioadmin / minioadmin)
```

### 0.2 Environment

Ensure `.env` / `appsettings.Development.json` contains:

```json
{
  "LiveKitOptions": {
    "ApiKey": "your-livekit-api-key",
    "ApiSecret": "your-livekit-api-secret",
    "ServerUrl": "wss://your-project.livekit.cloud",
    "WebhookSecret": "your-webhook-secret",
    "EgressHost": "your-egress-host"  // optional; if empty, auto-start is disabled
  },
  "StorageOptions": {
    "Endpoint": "http://localhost:9000",
    "AccessKey": "minioadmin",
    "SecretKey": "minioadmin",
    "Bucket": "meeting-recordings"
  },
  "OpenAiCompatible": {
    "Stt": {
      "BaseUrl": "https://api.openai.com/v1",
      "ApiKey": "sk-...",
      "Model": "whisper-1"
    },
    "Llm": {
      "BaseUrl": "https://api.openai.com/v1",
      "ApiKey": "sk-...",
      "Model": "gpt-4o"
    }
  }
}
```

### 0.3 Backend

```bash
# Terminal 2: Start backend with Hangfire dashboard
cd C:\Users\DEll\source\repos\MeetingAssitant-Backend
dotnet run --project MeetingAssistant/MeetingAssistant.csproj

# Verify
# API: http://localhost:5000
# Hangfire Dashboard: http://localhost:5000/hangfire
# Swagger: http://localhost:5000/swagger
```

### 0.4 Test Baseline

```bash
# Terminal 3: Run full test suite — must be 149 passed, 0 failed
dotnet test tests/MeetingAssistant.Tests.Integration/MeetingAssistant.Tests.Integration.csproj
```

---

## 1. Create a Meeting with Participants

### 1.1 Register & Login (if no existing token)

```bash
curl -X POST http://localhost:5000/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@test.org","password":"Test123!","organizationName":"Test Org"}'

# Response: { "accessToken": "eyJ...", "refreshToken": "..." }
# Save accessToken as $TOKEN
TOKEN=eyJ...
```

### 1.2 Create a Meeting

```bash
# Create meeting 15 minutes from now, 1 hour duration
START=$(date -u +"%Y-%m-%dT%H:%M:%SZ" -d "+15 minutes")
END=$(date -u +"%Y-%m-%dT%H:%M:%SZ" -d "+75 minutes")

# Get your org ID from the JWT claim, or list orgs
curl http://localhost:5000/api/auth/profile \
  -H "Authorization: Bearer $TOKEN"

# Response contains activeOrganization.id -> save as $ORG_ID
ORG_ID=your-org-guid

# Create meeting
curl -X POST "http://localhost:5000/api/organizations/$ORG_ID/meetings" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"title\": \"Runbook Test Meeting\",
    \"scheduledStartUtc\": \"$START\",
    \"scheduledEndUtc\": \"$END\",
    \"description\": \"End-to-end pipeline verification\"
  }"

# Response: { "id": "meeting-guid", ... }
# Save as $MEETING_ID
MEETING_ID=your-meeting-guid
```

### 1.3 Add Participants

```bash
# Register a second user (participant)
curl -X POST http://localhost:5000/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"participant@test.org","password":"Test123!","organizationName":"Test Org"}'

# Response: { "userId": "participant-user-guid" }
PARTICIPANT_USER_ID=participant-user-guid

# Or use an existing user from your org

# Add as meeting participant
curl -X POST "http://localhost:5000/api/meetings/$MEETING_ID/participants" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"userId\": \"$PARTICIPANT_USER_ID\",
    \"role\": \"Participant\"
  }"
```

### 1.4 Verify Meeting State in DB

```sql
-- PostgreSQL
SELECT id, title, status, organization_id
FROM "Meetings"
WHERE id = 'your-meeting-guid'::uuid;

-- Expected: status = 'Scheduled' (0), organization_id = $ORG_ID

SELECT user_id, meeting_role
FROM "MeetingParticipants"
WHERE "MeetingId" = 'your-meeting-guid'::uuid;

-- Expected: 2 rows (Host + Participant)
```

---

## 2. Live Session Lifecycle

### 2.1 Request Join Tokens

```bash
# Host token
curl -X POST "http://localhost:5000/api/organizations/$ORG_ID/meetings/$MEETING_ID/session/join-token" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"displayName":"Runbook Host"}'

# Response: { "accessToken": "livekit-jwt-...", "roomName": "mtg:meeting-guid", "serverUrl": "wss://..." }
# Save host LiveKit token

# Participant token (use participant's JWT)
PARTICIPANT_TOKEN=participant-jwt...
curl -X POST "http://localhost:5000/api/organizations/$ORG_ID/meetings/$MEETING_ID/session/join-token" \
  -H "Authorization: Bearer $PARTICIPANT_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"displayName":"Runbook Participant"}'
```

### 2.2 Simulate LiveKit Webhooks

Use the webhook secret from your LiveKit project settings. The backend verifies the `Authorization` header using `WebhookReceiver`.

```bash
WEBHOOK_SECRET=your-webhook-secret
API_KEY=your-livekit-api-key

# Helper: generate LiveKit webhook signature
# (In practice, LiveKit Cloud sends these. For local testing, use the SDK or curl with a valid JWT)
```

**Option A: Use LiveKit Cloud (real)**

1. Use the host token to connect to the LiveKit room via the JS/React client or CLI.
2. Publish microphone audio.
3. LiveKit Cloud will send webhooks to your publicly exposed backend (use ngrok for local dev).

**Option B: Direct webhook injection (local dev only)**

```bash
# Room Started
ROOM_STARTED_PAYLOAD='{"event":"room_started","id":"runbook-room-started-1","createdAt":'$(date +%s)',"room":{"name":"mtg:'$MEETING_ID'"}}'

# Sign the payload (requires LiveKit SDK or manual HMAC)
# For local testing, temporarily bypass validation by modifying WebhookValidator to skip check
# OR use the correct signature:

curl -X POST http://localhost:5000/api/webhooks/livekit \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <webhook-auth-token>" \
  -d "$ROOM_STARTED_PAYLOAD"

# Expected: HTTP 200
```

### 2.3 Verify Webhook Processing in DB

```sql
-- After each webhook, verify SessionEvents
SELECT event_type, external_event_id, occurred_at_utc
FROM "SessionEvents"
WHERE "MeetingId" = 'your-meeting-guid'::uuid
ORDER BY occurred_at_utc;

-- Expected sequence:
-- 1. RoomStarted (0)
-- 2. ParticipantJoined (2) — for host
-- 3. ParticipantJoined (2) — for participant
-- 4. TrackPublished (7) — for host's audio track (if unmuted)
-- 5. TrackPublished (7) — for participant's audio track
```

**Check meeting status transitioned to InProgress:**

```sql
SELECT status FROM "Meetings" WHERE id = 'your-meeting-guid'::uuid;
-- Expected: status = 'InProgress' (1)
```

---

## 3. Egress & Audio Pipeline

### 3.1 Trigger Egress (Real or Simulated)

**Real (with EgressHost configured):**
- The `TrackPublished` webhook automatically fires `_egressService.StartTrackEgressAsync` (fire-and-forget).
- LiveKit Egress writes audio files directly to MinIO at: `tracks/mtg:{meetingId}/user:{userId}/track-{trackSid}.ogg`

**Simulated (for runbook testing without LiveKit Egress):**

```bash
# Manually upload a test audio file to MinIO
# (Use a real OGG/Opus audio file, e.g., 30 seconds of speech)

mc cp test-audio.ogg local/meeting-recordings/tracks/mtg:$MEETING_ID/user:$HOST_USER_ID/track-test-host.ogg
mc cp test-audio.ogg local/meeting-recordings/tracks/mtg:$MEETING_ID/user:$PARTICIPANT_USER_ID/track-test-participant.ogg
```

### 3.2 Simulate `egress_ended` Webhook

```bash
# Build egress_ended payload for host
cat > egress_host.json <<EOF
{
  "event": "egress_ended",
  "id": "runbook-egress-host-1",
  "createdAt": $(date +%s),
  "egressInfo": {
    "roomName": "mtg:$MEETING_ID",
    "status": 0,
    "fileResults": [
      {
        "filename": "tracks/mtg-$MEETING_ID/user:$HOST_USER_ID/track-test-host.ogg",
        "location": "http://localhost:9000/meeting-recordings/tracks/mtg-$MEETING_ID/user:$HOST_USER_ID/track-test-host.ogg"
      }
    ]
  }
}
EOF

# Send webhook
curl -X POST http://localhost:5000/api/webhooks/livekit \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <webhook-auth>" \
  -d @egress_host.json

# Repeat for participant with their filename/location
```

### 3.3 Verify Pipeline State

```sql
-- Check ParticipantAudioTrack rows
SELECT participant_user_id, status, storage_object_key, size_bytes, created_at_utc
FROM "ParticipantAudioTracks"
WHERE "MeetingId" = 'your-meeting-guid'::uuid;

-- Expected: 2 rows, status = 'Available' (2)
-- Storage object key = tracks/mtg:.../user:.../track-....ogg

-- Check Hangfire dashboard: http://localhost:5000/hangfire
-- Expected: 2x IngestParticipantAudioJob succeeded
-- Then: 1x GenerateMeetingTranscriptJob succeeded
-- Then: 1x GenerateMeetingSummaryJob succeeded

-- Check for ParticipantAudioReadyEvent (SessionEvent)
SELECT event_type, external_event_id
FROM "SessionEvents"
WHERE "MeetingId" = 'your-meeting-guid'::uuid
  AND event_type = 6;  -- ParticipantAudioReady = 6

-- Expected: exactly 1 row
```

---

## 4. Verify Transcript & Summary

### 4.1 Database Check

```sql
-- Transcript
SELECT full_text, segments_json, stt_model, generated_at_utc
FROM "MeetingTranscripts"
WHERE "MeetingId" = 'your-meeting-guid'::uuid;

-- Expected: full_text contains formatted transcript with timestamps and speaker names
-- segments_json is valid JSON array

-- Summary
SELECT summary_text, llm_model, prompt_tokens, completion_tokens, generated_at_utc
FROM "MeetingSummaries"
WHERE "MeetingId" = 'your-meeting-guid'::uuid;

-- Expected: summary_text is a coherent meeting summary
```

### 4.2 API Check (if transcript endpoint is exposed)

```bash
# Note: GET /api/meetings/{meetingId}/transcript is restricted to OrgAdmin/debug in current version
curl "http://localhost:5000/api/meetings/$MEETING_ID/transcript" \
  -H "Authorization: Bearer $TOKEN"

# May return 403 if you are not Admin; use a Host/Admin token
```

---

## 5. Room End & Cleanup

### 5.1 Simulate `room_finished` Webhook

```bash
cat > room_finished.json <<EOF
{
  "event": "room_finished",
  "id": "runbook-room-finished-1",
  "createdAt": $(date +%s),
  "room": {
    "name": "mtg:$MEETING_ID"
  }
}
EOF

curl -X POST http://localhost:5000/api/webhooks/livekit \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <webhook-auth>" \
  -d @room_finished.json
```

### 5.2 Verify Meeting Completed

```sql
SELECT status FROM "Meetings" WHERE id = 'your-meeting-guid'::uuid;
-- Expected: status = 'Completed' (2)
```

---

## 6. Edge Case Tests

### 6.1 Duplicate Webhook Idempotency

```bash
# Send the SAME egress_ended webhook twice (same id)
curl -X POST http://localhost:5000/api/webhooks/livekit \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <webhook-auth>" \
  -d @egress_host.json

curl -X POST http://localhost:5000/api/webhooks/livekit \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <webhook-auth>" \
  -d @egress_host.json

# Verify: exactly 1 SessionEvent for this external_event_id
SELECT COUNT(*) FROM "SessionEvents"
WHERE external_event_id = 'runbook-egress-host-1';
-- Expected: 1

# Verify: exactly 1 ParticipantAudioTrack for this participant
SELECT COUNT(*) FROM "ParticipantAudioTracks"
WHERE "MeetingId" = 'your-meeting-guid'::uuid
  AND participant_user_id = 'host-user-id'::uuid;
-- Expected: 1

# Verify: Hangfire has exactly 1 job for this track (not 2)
-- Check Hangfire dashboard > Succeeded Jobs
```

### 6.2 Missing Participant (Join Barrier)

```bash
# Create a meeting with 3 participants
# Only simulate egress for 2 participants
# Verify: ParticipantAudioReadyEvent does NOT fire

# Then simulate egress for the 3rd participant
# Verify: ParticipantAudioReadyEvent fires exactly once
```

### 6.3 Non-Audio File Rejection

```bash
# Simulate egress with a .mp4 video file
cat > egress_video.json <<EOF
{
  "event": "egress_ended",
  "id": "runbook-video-1",
  "createdAt": $(date +%s),
  "egressInfo": {
    "roomName": "mtg:$MEETING_ID",
    "status": 0,
    "fileResults": [
      {
        "filename": "tracks/mtg-$MEETING_ID/user:$HOST_USER_ID/track-test.mp4",
        "location": "http://localhost:9000/meeting-recordings/tracks/mtg-$MEETING_ID/user:$HOST_USER_ID/track-test.mp4"
      }
    ]
  }
}
EOF

curl -X POST http://localhost:5000/api/webhooks/livekit \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <webhook-auth>" \
  -d @egress_video.json

# Verify: 0 ParticipantAudioTrack rows created for .mp4
# Check backend logs for: "Skipping non-audio file in egress payload"
```

### 6.4 8-Minute Ceiling (Manual DB Injection)

```sql
-- Create a stale Pending track
INSERT INTO "ParticipantAudioTracks" (
  id, "MeetingId", "OrganizationId", "ParticipantUserId",
  status, "CreatedAtUtc", "UpdatedAtUtc"
) VALUES (
  gen_random_uuid(),
  'your-meeting-guid'::uuid,
  'your-org-id'::uuid,
  'fake-user-id'::uuid,
  0,  -- Pending
  NOW() - INTERVAL '10 minutes',
  NOW() - INTERVAL '10 minutes'
);

-- Manually enqueue the ingest job via Hangfire dashboard
-- Or trigger via code
-- Verify: track.Status = Failed (3) within seconds
-- Verify: if this was the last non-terminal track, ParticipantAudioReadyEvent fires
```

---

## 7. MinIO Verification

```bash
# List stored audio tracks
mc ls local/meeting-recordings/tracks/mtg-$MEETING_ID/

# Expected: user:{host-user-id}/track-....ogg
#           user:{participant-user-id}/track-....ogg

# Verify object metadata
mc stat local/meeting-recordings/tracks/mtg-$MEETING_ID/user:$HOST_USER_ID/track-test-host.ogg
```

---

## 8. Troubleshooting

| Symptom | Check | Fix |
|---------|-------|-----|
| Webhook returns 400/401 | `WebhookSecret` / `ApiKey` mismatch | Verify `.env` matches LiveKit Cloud project settings |
| `EgressHost` empty → no auto-start | `LiveKitOptions:EgressHost` not configured | Add egress host or manually trigger egress via LiveKit CLI |
| Track stays `Pending` forever | Hangfire worker not running | Check Hangfire dashboard for failed jobs; check logs for 8-min ceiling |
| Transcript empty | STT API key invalid / model down | Check `SttService` logs for HTTP errors; verify `OpenAiCompatible:Stt` config |
| Summary empty | LLM API key invalid | Check `SummarizerService` logs; verify `OpenAiCompatible:Llm` config |
| `Downloading` status mentioned in old docs | Enum value removed | `ParticipantAudioTrackStatus` now only has `Pending=0`, `Available=2`, `Failed=3` |
| Cross-tenant data leak | Background jobs missing `OrganizationId` filter | Already fixed in `main` — all background queries use `IgnoreQueryFilters()` + explicit `OrganizationId` |

---

## 9. Quick Verification Checklist

After any change to `LiveSession`, run:

```bash
# 1. Build
dotnet build

# 2. Full test suite (149 tests must pass)
dotnet test tests/MeetingAssistant.Tests.Integration/MeetingAssistant.Tests.Integration.csproj

# 3. No new migrations (Phase 5 is amendment-only)
git status --short MeetingAssistant/Migrations/
# Expected: empty

# 4. Lint / verify no compilation warnings in LiveSession
dotnet build MeetingAssistant/MeetingAssistant.csproj -warnaserror
```

---

## 10. Architecture Notes for Operators

### Direct S3 Write vs. Download

Our implementation uses **LiveKit Egress → direct MinIO write** (self-hosted stack). The backend does **not** download from a presigned URL. Instead:

1. LiveKit Egress writes the `.ogg` file directly to MinIO
2. Backend receives `egress_ended` webhook with the S3 object path
3. `IngestParticipantAudioJob` parses the object key from the URL and marks the track `Available`

**Implication**: This only works with self-hosted LiveKit + Egress + MinIO. LiveKit Cloud requires a download-then-upload pipeline (not implemented in this version).

### SignalR Removed

All real-time push notifications (`LiveSessionHub`, `LiveSessionNotifier`) were deleted. The pipeline is **silent** — success/failure is only visible via:
- Hangfire dashboard
- Database queries
- Backend logs

### No `PipelineStateService`

Operational visibility into pipeline state (track count, barrier status, expected vs. actual participants) is **not exposed via API**. Query the database directly:

```sql
-- Pipeline state for a meeting
WITH tracks AS (
  SELECT status, COUNT(*) as cnt
  FROM "ParticipantAudioTracks"
  WHERE "MeetingId" = 'guid'::uuid
  GROUP BY status
),
expected AS (
  SELECT COUNT(*) as cnt
  FROM "SessionEvents"
  WHERE "MeetingId" = 'guid'::uuid
    AND event_type = 2  -- ParticipantJoined
)
SELECT
  (SELECT cnt FROM expected) as expected_participants,
  (SELECT cnt FROM tracks WHERE status = 0) as pending,
  (SELECT cnt FROM tracks WHERE status = 2) as available,
  (SELECT cnt FROM tracks WHERE status = 3) as failed
FROM expected;
```

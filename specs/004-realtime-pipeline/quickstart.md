# Quickstart: Realtime Session Pipeline (Phase 4/5/5.5)

**Branch**: `004-realtime-pipeline` | **Date**: 2026-04-18 | **Last Updated**: 2026-04-25

This quickstart reflects the amended pipeline:

1. LiveKit track egress (audio-only, no video).
2. Webhook fan-out into `ParticipantAudioTrack` rows.
3. Per-track ingest to MinIO by `IngestParticipantAudioJob`.
4. Join barrier (`Available` or `Failed`) emits `ParticipantAudioReadyEvent` exactly once.
5. Hangfire transcript job writes `MeetingTranscript`.
6. Hangfire summary job writes `MeetingSummary`.

## Prerequisites

- Phase 1-3 complete with green tests.
- PostgreSQL reachable by app connection string.
- MinIO (or S3-compatible storage) reachable by backend.
- Hangfire enabled in host.
- LiveKit Cloud project with webhook support and track egress.
- ffmpeg available in the worker/container image (required for large-file STT chunking).
- .NET 10 SDK installed.

## Required Configuration

Set user secrets for `MeetingAssistant`:

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

dotnet user-secrets set "OpenAiCompatible:Stt:BaseUrl" "https://api.openai.com/v1"
dotnet user-secrets set "OpenAiCompatible:Stt:ApiKey" "<stt-api-key>"
dotnet user-secrets set "OpenAiCompatible:Stt:Model" "whisper-1"

dotnet user-secrets set "OpenAiCompatible:Llm:BaseUrl" "https://api.openai.com/v1"
dotnet user-secrets set "OpenAiCompatible:Llm:ApiKey" "<llm-api-key>"
dotnet user-secrets set "OpenAiCompatible:Llm:Model" "gpt-4o-mini"
```

Equivalent environment variables:

- `OpenAiCompatible__Stt__BaseUrl`
- `OpenAiCompatible__Stt__ApiKey`
- `OpenAiCompatible__Stt__Model`
- `OpenAiCompatible__Llm__BaseUrl`
- `OpenAiCompatible__Llm__ApiKey`
- `OpenAiCompatible__Llm__Model`

## LiveKit Egress Requirements

Configure LiveKit egress profile as:

- Mode: track-based egress.
- Media: audio only.
- Video: disabled.
- Container/codec: OGG/Opus.
- Destination: LiveKit-managed cloud storage.

## Apply Database Changes

```powershell
Set-Location "c:\Users\DEll\source\repos\MeetingAssitant-Backend"
dotnet ef database update --project "MeetingAssistant\MeetingAssistant.csproj" --startup-project "MeetingAssistant\MeetingAssistant.csproj"
```

## Run Locally

```powershell
Set-Location "c:\Users\DEll\source\repos\MeetingAssitant-Backend"
dotnet run --project "MeetingAssistant\MeetingAssistant.csproj"
```

If running locally against LiveKit Cloud webhooks, expose backend via ngrok:

```powershell
ngrok http <backend-http-port>
```

Set webhook URL to:

`https://<ngrok-id>.ngrok-free.app/api/webhooks/livekit`

## Validation Commands

```powershell
dotnet build MeetingAssistant/MeetingAssistant.csproj
dotnet test tests/MeetingAssistant.Tests.Integration/MeetingAssistant.Tests.Integration.csproj
```

## Expected Data Artifacts

After a successful meeting post-processing run:

- `ParticipantAudioTracks` has one row per participant with terminal status.
- `SessionEvents` contains one `ParticipantAudioReady` event per meeting.
- MinIO contains objects under `tracks/{MeetingId}/`.
- `MeetingTranscripts` has one row per meeting.
- `MeetingSummaries` has one row per meeting.

## Sandbox E2E Runbook

For an evidence-oriented sandbox run, use:

- `specs/004-realtime-pipeline/livekit-sandbox-runbook.md`

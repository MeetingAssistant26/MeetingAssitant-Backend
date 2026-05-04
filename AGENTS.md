# MeetingAssitant-Backend Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-05-03

## Active Technologies
- C# / .NET 10 + ASP.NET Core (Controllers + SignalR), EF Core (Npgsql), FluentValidation, MediatR, Mapster, **LiveKit Server SDK for .NET** (`Livekit.Server.Sdk`) for access-token generation and webhook signature verification (004-realtime-pipeline)
- PostgreSQL via Npgsql with EF Core � new tables `ParticipantAudioTracks`, `MeetingTranscripts`, `MeetingSummaries`, and `SessionEvents` (004-realtime-pipeline)
- C# / .NET 10 + ASP.NET Core (Controllers + SignalR), EF Core 9 (Npgsql), FluentValidation, MediatR, Mapster, `Livekit.Server.Sdk.Dotnet` (005-opus-realtime-amendment)
- PostgreSQL via Npgsql + EF Core. This phase adds **no new entities, tables, or migrations.** (005-opus-realtime-amendment)
- C# / .NET 10 + ASP.NET Core, EF Core (Npgsql), FluentValidation, Mapster, MediatR, LiveKit Server SDK (`Livekit.Server.Sdk`) (007-agent-api-surface)
- Agent-facing auth/rate-limit stack: `AgentJwt` authentication scheme, `AgentOnly` authorization policy, and `AgentPerMeeting` fixed-window limiter (100 req/min/meeting) (007-agent-api-surface)
- PostgreSQL (pgvector for Meeting Memory in future phases), Redis (caching), MinIO (storage) (007-agent-api-surface)

- C# / .NET 10 + ASP.NET Core, EF Core (Npgsql), FluentValidation, MediatR, Mapster (003-meetings)
- C# / .NET 10 + ASP.NET Core, EF Core (Npgsql), FluentValidation, MediatR, Mapster, ASP.NET Core Data Protection, Provider Strategy Pattern (`ITaskProvider`) (008-action-items-integration)
- PostgreSQL via Npgsql + EF Core. New tables: `ActionItems`, `OrganizationIntegrations`, `OrganizationIntegrationConfigs`, `ExternalAccountLinks`, `ExternalMemberMappings` (008-action-items-integration)

## Project Structure

```text
backend/
frontend/
tests/
```

## Commands

# Add commands for C# / .NET 10

## Code Style

C# / .NET 10: Follow standard conventions

## Recent Changes
- 008-action-items-integration: Implemented action item extraction from transcripts, review/approve workflow, pluggable provider sync via `ITaskProvider`, Trello as first provider, admin settings, and user connections
- 007-agent-api-surface: Implemented agent reminder + catalog endpoints, token refresh endpoint, tenant isolation tests, and rate limiting tests
- 005-opus-realtime-amendment: Added C# / .NET 10 + ASP.NET Core (Controllers + SignalR), EF Core 9 (Npgsql), FluentValidation, MediatR, Mapster, `Livekit.Server.Sdk.Dotnet`
- 004-realtime-pipeline: Added C# / .NET 10 + ASP.NET Core (Controllers + SignalR), EF Core (Npgsql), FluentValidation, MediatR, Mapster, **LiveKit Server SDK for .NET** (`Livekit.Server.Sdk`) for access-token generation and webhook signature verification


<!-- MANUAL ADDITIONS START -->
- Spec Kit is available to Codex through `.agents/skills/speckit-*/SKILL.md`.
- Claude Code remains enabled through `.claude/commands/speckit.*.md`; treat those files as the canonical workflow prompts so both integrations stay aligned.
- When Spec Kit context changes, keep `AGENTS.md` and `CLAUDE.md` synchronized instead of replacing one integration with the other.
<!-- MANUAL ADDITIONS END -->

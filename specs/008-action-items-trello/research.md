# Research: Action Items & Trello Integration

**Feature**: 008-action-items-trello  
**Date**: 2026-05-04

## Decisions

### 1. Trello API Integration Pattern

**Decision**: Use direct REST API calls via `HttpClient` with a lightweight `ITrelloClient` wrapper. Do not use third-party Trello SDKs.

**Rationale**: Existing .NET Trello SDKs (`Manatee.Trello`) are unmaintained or overly complex. A thin typed client over the Trello REST API v1 gives us full control, minimal dependencies, and aligns with the existing `ILLMService` HTTP client pattern in the codebase.

**Alternatives considered**:
- `Manatee.Trello` NuGet package — rejected: last updated years ago, heavy object model, unnecessary abstraction.
- Custom Trello SDK wrapper library — rejected: over-engineering for ~4 API calls (create card, get boards, get lists, get board members).

### 2. Encryption Strategy for Trello Tokens

**Decision**: Use ASP.NET Core Data Protection (`IDataProtector`) to encrypt `ApiToken` and `ApiKey` at rest.

**Rationale**: Data Protection is already configured in the ASP.NET Core pipeline. It handles key rotation, encryption algorithm selection, and secure storage automatically. No new encryption libraries or key management needed.

**Alternatives considered**:
- AES encryption with manual key management — rejected: introduces key rotation and storage complexity.
- Hashing (one-way) — rejected: tokens must be recoverable to make API calls.

### 3. Background Job Infrastructure

**Decision**: Use Hangfire (already in use) for `ExtractActionItemsJob` and `SyncActionItemsToTrelloJob`.

**Rationale**: Hangfire is already configured with PostgreSQL storage, retry policies, and dashboard. The existing `GenerateMeetingSummaryJob` and `IngestParticipantAudioJob` follow the same pattern.

**Alternatives considered**:
- MediatR `INotificationHandler` with fire-and-forget — rejected: no retry, no visibility, no persistence on crash.
- Quartz — rejected: Hangfire is already the project's standard.

### 4. Parallel Extraction Trigger

**Decision**: Publish `MeetingTranscriptReadyEvent` from `GenerateMeetingTranscriptJob`, with two independent MediatR handlers enqueued via Hangfire: `GenerateMeetingSummaryJob` and `ExtractActionItemsJob`.

**Rationale**: Both jobs depend only on the transcript text. Running them in parallel reduces end-to-end latency. The existing `ParticipantAudioReadyEvent` pattern in the codebase demonstrates this approach.

**Alternatives considered**:
- Chain sequentially (Summary → then Extraction) — rejected: extraction does not depend on summary output; sequential execution wastes time.
- Combine into single mega-job — rejected: violates single responsibility; summary and extraction are independent concerns.

### 5. Trello Member Validation

**Decision**: Cache board member list for ~5 minutes to avoid repeated API calls during sync.

**Rationale**: Board membership rarely changes mid-sync. Caching reduces API calls and improves sync throughput. Cache is in-memory (no Redis complexity needed for this scale).

**Alternatives considered**:
- Query Trello for every card creation — rejected: wasteful; 10 action items = 10 identical member-list API calls.
- Persist board members in database — rejected: adds sync complexity; Trello is the source of truth.

### 6. HTTP Status Code for Bulk Sync Partial Failure

**Decision**: Return `207 Multi-Status` for bulk sync with per-item results.

**Rationale**: RFC 4918 defines 207 for mixed success/failure scenarios. It allows returning detailed per-item status in a single response body, which is exactly what the spec requires.

**Alternatives considered**:
- `200 OK` with error array — rejected: semantically misleading; not all items succeeded.
- `400 Bad Request` — rejected: implies the entire request was invalid.
- `202 Accepted` with polling — rejected: adds unnecessary async complexity for a synchronous sync operation.

## Open Questions (None)

All technical decisions are resolved based on existing codebase patterns and spec clarifications.

## References

- Trello REST API: https://developer.atlassian.com/cloud/trello/rest/api-group-cards/
- ASP.NET Core Data Protection: https://docs.microsoft.com/en-us/aspnet/core/security/data-protection/
- Hangfire Documentation: https://docs.hangfire.io/
- RFC 4918 (WebDAV): https://tools.ietf.org/html/rfc4918#section-11.1

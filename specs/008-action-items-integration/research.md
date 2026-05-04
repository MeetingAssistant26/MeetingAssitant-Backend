# Research: Action Items & External Task Provider Integration

**Feature**: 008-action-items-integration  
**Date**: 2026-05-04  
**Status**: Validated against official Trello REST API documentation

---

## Decisions

### 1. Provider Integration Pattern

**Decision**: Use a generic `ITaskProvider` abstraction with direct REST API calls via `HttpClient` inside provider-specific implementations. Do not use third-party SDKs.

**Rationale**: Existing .NET SDKs for task providers are often unmaintained or overly complex. A thin typed client over each provider's REST API gives full control, minimal dependencies, and aligns with the existing `ILLMService` HTTP client pattern. Trello is the first provider; ClickUp can be added later by implementing the same interface.

**Alternatives considered**:
- `Manatee.Trello` NuGet package — rejected: last updated years ago, heavy object model, unnecessary abstraction.
- Custom per-provider SDK wrapper libraries — rejected: over-engineering for ~4 API calls per provider (create task, get projects, get lists, get project members).
- Provider-specific SDKs (e.g., ClickUp SDK) — rejected: adds external dependencies with varying quality and maintenance.

---

### 2. Encryption Strategy for Provider Tokens

**Decision**: Use ASP.NET Core Data Protection (`IDataProtector`) to encrypt provider credentials at rest inside `OrganizationIntegrationConfig.EncryptedProviderPayload`.

**Rationale**: Data Protection is already configured in the ASP.NET Core pipeline. It handles key rotation, encryption algorithm selection, and secure storage automatically. No new encryption libraries or key management needed. The encrypted JSON blob approach allows adding new providers without schema changes.

**Alternatives considered**:
- AES encryption with manual key management — rejected: introduces key rotation and storage complexity.
- Hashing (one-way) — rejected: tokens must be recoverable to make API calls.
- Per-provider encrypted columns — rejected: requires schema migrations for each new provider.

---

### 3. Background Job Infrastructure

**Decision**: Use Hangfire (already in use) for `ExtractActionItemsJob` and `SyncActionItemsToProviderJob`.

**Rationale**: Hangfire is already configured with PostgreSQL storage, retry policies, and dashboard. The existing `GenerateMeetingSummaryJob` and `IngestParticipantAudioJob` follow the same pattern.

**Alternatives considered**:
- MediatR `INotificationHandler` with fire-and-forget — rejected: no retry, no visibility, no persistence on crash.
- Quartz — rejected: Hangfire is already the project's standard.

---

### 4. Parallel Extraction Trigger

**Decision**: Publish `MeetingTranscriptReadyEvent` from `GenerateMeetingTranscriptJob`, with two independent MediatR handlers enqueued via Hangfire: `GenerateMeetingSummaryJob` and `ExtractActionItemsJob`.

**Rationale**: Both jobs depend only on the transcript text. Running them in parallel reduces end-to-end latency. The existing `ParticipantAudioReadyEvent` pattern in the codebase demonstrates this approach.

**Alternatives considered**:
- Chain sequentially (Summary → then Extraction) — rejected: extraction does not depend on summary output; sequential execution wastes time.
- Combine into single mega-job — rejected: violates single responsibility; summary and extraction are independent concerns.

---

### 5. Provider Member Validation

**Decision**: Cache project member list for ~5 minutes to avoid repeated API calls during sync.

**Rationale**: Project membership rarely changes mid-sync. Caching reduces API calls and improves sync throughput. Cache is in-memory (no Redis complexity needed for this scale). Each `ITaskProvider` implementation handles its own member-list fetching and caching.

**Alternatives considered**:
- Query provider for every task creation — rejected: wasteful; 10 action items = 10 identical member-list API calls.
- Persist project members in database — rejected: adds sync complexity; external provider is the source of truth.

---

### 6. HTTP Status Code for Bulk Sync Partial Failure

**Decision**: Return `207 Multi-Status` for bulk sync with per-item results.

**Rationale**: RFC 4918 defines 207 for mixed success/failure scenarios. It allows returning detailed per-item status in a single response body, which is exactly what the spec requires.

**Alternatives considered**:
- `200 OK` with error array — rejected: semantically misleading; not all items succeeded.
- `400 Bad Request` — rejected: implies the entire request was invalid.
- `202 Accepted` with polling — rejected: adds unnecessary async complexity for a synchronous sync operation.

---

### 7. Provider-Specific Configuration Storage

**Decision**: Store provider-specific settings as an encrypted JSON blob in `OrganizationIntegrationConfig.EncryptedProviderPayload`.

**Rationale**: Each provider has different settings (Trello needs `apiKey + apiToken`, ClickUp might need `apiToken + teamId`, etc.). An encrypted JSON blob avoids schema migrations when adding new providers. The blob is decrypted and deserialized by the specific `ITaskProvider` implementation at runtime.

**Alternatives considered**:
- Typed columns per provider — rejected: requires schema changes for each new provider.
- Separate table per provider — rejected: excessive normalization; most orgs will only use one provider.
- Key-value store — rejected: adds infrastructure complexity; JSON blob in PostgreSQL is sufficient.

---

## Trello-Specific Implementation Details (Official API Validation)

### Authentication Model

Trello uses **API Key + Token** authentication:
- **API Key**: Generated at https://trello.com/power-ups/admin — tied to a Power-Up, public
- **Token**: Generated per-user via `https://trello.com/1/authorize?expiration=never&scope=read,write&response_type=token&key={APIKey}` — secret, grants access to user's account
- Users can revoke tokens at any time from their account settings
- Token revocation → API returns `401` with message `"invalid token"`

**Passing credentials** (3 ways per official docs):
1. Query params: `?key={apiKey}&token={token}`
2. Authorization header: `Authorization: OAuth oauth_consumer_key="{apiKey}", oauth_token="{token}"`
3. PUT/POST body: JSON payload with `key` and `token` fields

**Validation endpoint**: `GET https://api.trello.com/1/members/me?key={apiKey}&token={token}`
- Returns: `{ "id": "...", "username": "...", "fullName": "...", ... }`
- 401 = invalid credentials

### Rate Limits (Official)

| Limit | Value | Scope |
|-------|-------|-------|
| API Key | 300 requests / 10 seconds | Per API key across all tokens |
| Token | 100 requests / 10 seconds | Per token |
| Members route | 100 requests / 900 seconds | Special limit for `/1/members/*` |

**429 Response body**:
```json
{ "error": "API_TOKEN_LIMIT_EXCEEDED", "message": "Rate limit exceeded" }
// or
{ "error": "API_KEY_LIMIT_EXCEEDED", "message": "Rate limit exceeded" }
```

**Rate limit headers**:
```
x-rate-limit-api-token-interval-ms: 10000
x-rate-limit-api-token-max: 100
x-rate-limit-api-token-remaining: 99
x-rate-limit-api-key-interval-ms: 10000
x-rate-limit-api-key-max: 300
x-rate-limit-api-key-remaining: 299
```

> **Important**: The `/1/members/` route has a much stricter limit (100 per 15 minutes). During sync, we should use `GET /1/boards/{boardId}/members` instead of individual `/1/members/{id}` calls to avoid hitting this limit.

### Key Endpoints for TrelloTaskProvider

| Purpose | Method | Endpoint | Required Params |
|---------|--------|----------|-----------------|
| Validate credentials | GET | `/1/members/me` | `key`, `token` |
| List boards (projects) | GET | `/1/members/me/boards` | `key`, `token` |
| Get board details | GET | `/1/boards/{id}` | `key`, `token` |
| List lists | GET | `/1/boards/{id}/lists` | `key`, `token` |
| List board members | GET | `/1/boards/{id}/members` | `key`, `token` |
| Create card | POST | `/1/cards` | `key`, `token`, `idList` |

### Card Creation Details

**POST `/1/cards`**
- Required: `idList` (Trello list ID)
- Optional: `name`, `desc`, `due` (ISO 8601), `idMembers` (array of member IDs)
- `idMembers` format: `idMembers[]=memberId1&idMembers[]=memberId2` (query params) or array in JSON body

**Response** (200 OK):
```json
{
  "id": "card-id",
  "name": "Card title",
  "desc": "Card description",
  "due": "2026-05-10T00:00:00.000Z",
  "idList": "list-id",
  "idBoard": "board-id",
  "idMembers": ["member-id-1"],
  "url": "https://trello.com/c/shortLink",
  "shortUrl": "https://trello.com/c/shortLink"
}
```

### Error Handling Mapping

| Trello Status | Meaning | Our Integration Status |
|---------------|---------|----------------------|
| 401 Unauthorized | Invalid/expired token | `NeedsReconnect` |
| 404 Not Found | Board/list not found | `InvalidConfig` |
| 429 Too Many Requests | Rate limit exceeded | Retry with exponential backoff |
| 5xx Server Error | Trello server issue | Retry with exponential backoff |

---

## Open Questions (None)

All technical decisions are resolved based on existing codebase patterns, spec clarifications, and official Trello API documentation.

## References

- Trello REST API: https://developer.atlassian.com/cloud/trello/rest/api-group-cards/
- Trello Authorization Guide: https://developer.atlassian.com/cloud/trello/guides/rest-api/authorization/
- Trello Rate Limits: https://developer.atlassian.com/cloud/trello/guides/rest-api/rate-limits/
- ASP.NET Core Data Protection: https://docs.microsoft.com/en-us/aspnet/core/security/data-protection/
- Hangfire Documentation: https://docs.hangfire.io/
- RFC 4918 (WebDAV): https://tools.ietf.org/html/rfc4918#section-11.1

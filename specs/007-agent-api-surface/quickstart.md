# Quickstart: Agent-Callable API Surface

**Feature**: Agent-Callable API Surface (Phase 5.7)
**Prerequisites**: Identity, Organizations, Meetings, and Reminders features are available
**Validation Date**: 2026-05-03

## 1. Configure Agent JWT

Add `AgentJwt` settings in `appsettings.Development.json` (or user-secrets):

```json
{
  "AgentJwt": {
    "SigningKey": "your-agent-jwt-signing-key-min-32-chars",
    "Issuer": "MeetingAssistant",
    "Audience": "MeetingAssistantAgent",
    "TokenExpiryMinutes": 360
  }
}
```

## 2. Ensure Service Registration

Agent API is wired through:

- `AddAgentApiFeature()` in `Program.cs`
- `AgentJwt` authentication scheme and `AgentOnly` policy in `AuthDI.cs`
- `AgentPerMeeting` rate limiter in `Program.cs`

## 3. Mint an Agent Token

```csharp
var tokenResult = await agentAuthService.MintTokenAsync(
    organizationId: organizationId,
    meetingId: meetingId,
    lifetime: TimeSpan.FromHours(4),
    cancellationToken: cancellationToken);
```

Claims include:
- `agent = true`
- `organizationId`
- `meetingId`

## 4. Call Agent Endpoints

```bash
# Organization profile
curl -H "Authorization: Bearer $AGENT_TOKEN" \
  https://localhost:5001/api/agent/organization

# Meeting members (must match token meetingId)
curl -H "Authorization: Bearer $AGENT_TOKEN" \
  https://localhost:5001/api/agent/meetings/{meetingId}/members

# Create reminder
curl -X POST -H "Authorization: Bearer $AGENT_TOKEN" -H "Content-Type: application/json" \
  -d '{"text":"Review Q3 metrics","scope":"Public","targetUserId":null,"reminderAtUtc":"2026-05-10T09:00:00Z","createdByUserId":null}' \
  https://localhost:5001/api/agent/meetings/{meetingId}/reminders

# List public reminders due by meeting start
curl -H "Authorization: Bearer $AGENT_TOKEN" \
  https://localhost:5001/api/agent/meetings/{meetingId}/reminders

# Meeting catalog
curl -H "Authorization: Bearer $AGENT_TOKEN" \
  "https://localhost:5001/api/agent/meetings?status=upcoming&limit=20&offset=0"

# Token refresh (allowed only while meeting is InProgress)
curl -X POST -H "Authorization: Bearer $AGENT_TOKEN" \
  https://localhost:5001/api/agent/refresh
```

## 5. Run Test Suite

```bash
dotnet test tests/MeetingAssistant.Tests.Integration/MeetingAssistant.Tests.Integration.csproj --filter "FullyQualifiedName~AgentApi"
```

## 6. Expected Behaviors

- `GET /api/agent/meetings/{meetingId}/members` and reminder meeting routes enforce meeting-token binding.
- Public reminder list excludes personal reminders by hard filter (`Scope = Public`).
- Rate limit is 100 requests/minute per token `meetingId` (`429` + `Retry-After`).
- Cross-tenant data is isolated via `organizationId` claim and query scoping.
- `POST /api/agent/refresh` returns `403` when meeting status is not `InProgress`.

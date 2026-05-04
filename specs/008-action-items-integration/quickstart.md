# Quickstart: Action Items & External Task Provider Integration

## Prerequisites

- .NET 10 SDK
- PostgreSQL (with existing MeetingAssistant database)
- External task provider account (e.g., Trello) with API credentials
- Existing codebase with Hangfire, EF Core, MediatR configured

## Local Development Setup

### 1. Configure Provider Credentials (for testing)

**Trello Example:**
1. Visit https://trello.com/power-ups/admin/ and generate an **API Key**.
2. Generate a **Token** from the same page.
3. Create a test board and list in Trello.
4. Note down the Board ID and List ID (visible in Trello URL or via API).

### 2. Database Migration

After implementing the entities, generate and apply the migration:

```bash
cd MeetingAssistant
dotnet ef migrations add AddActionItemsAndIntegration --context ApplicationDbContext
dotnet ef database update --context ApplicationDbContext
```

### 3. Running the Extraction Job Locally

Trigger the job manually via Hangfire Dashboard:

1. Start the application.
2. Navigate to `/jobs` (Hangfire Dashboard).
3. Enqueue `ExtractActionItemsJob` with parameters:
   - `meetingId`: ID of a meeting with transcript segments
   - `organizationId`: Matching organization ID

Or trigger via code:

```csharp
BackgroundJob.Enqueue<ExtractActionItemsJob>(
    job => job.RunAsync(meetingId, organizationId, CancellationToken.None));
```

### 4. Testing Provider Sync

1. Ensure org has an integration configured (use the admin settings endpoint).
2. Create action items for a meeting and mark them `Approved`.
3. Call the sync endpoint:

```bash
curl -X POST \
  https://localhost:5001/api/organizations/{orgId}/meetings/{meetingId}/action-items/{id}/sync \
  -H "Authorization: Bearer <token>"
```

4. Verify the task appears in the configured external provider project.

### 5. Integration Test Execution

Run the full integration test suite for this feature:

```bash
cd tests/MeetingAssistant.Tests.Integration
dotnet test --filter "FullyQualifiedName~ActionItems"
```

## Key Configuration

No new `appsettings` sections are required. Provider credentials are stored per-organization in the database (encrypted). The existing `ILLMService` configuration (base URL, API key) is reused for action item extraction.

## Adding a New Provider (e.g., ClickUp)

To add a new external task provider without changing controllers or database schema:

1. **Create provider implementation**:
   ```csharp
   public class ClickUpTaskProvider : ITaskProvider
   {
       public string ProviderName => "ClickUp";
       // Implement ValidateCredentialsAsync, ListProjectsAsync, ListListsAsync, CreateTaskAsync
   }
   ```

2. **Register in DI**:
   ```csharp
   services.AddScoped<ITaskProvider, ClickUpTaskProvider>();
   ```

3. **Define JSON payload shape** for `OrganizationIntegrationConfig.EncryptedProviderPayload` (e.g., `{ apiToken, teamId, spaceId }`).

4. **Add provider to `ExternalProvider` enum**.

No new controllers, endpoints, or database migrations are required.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Action items not extracted | Transcript not ready / job not triggered | Verify `MeetingTranscriptReadyEvent` is emitted. Check Hangfire dashboard for failed jobs. |
| Provider sync returns 401 | Invalid or expired token | Org admin must re-enter provider credentials. |
| Provider sync returns 404 | Project/list deleted | Org admin must select new project/list. |
| 409 Conflict on update | Another user edited the item | Refresh the page and retry. |
| Assignee not mapped | User not connected to provider | User connects via profile settings, or admin sets manual mapping. |


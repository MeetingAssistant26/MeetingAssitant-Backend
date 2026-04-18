# Quickstart: Meetings (Phase 3)

**Branch**: `003-meetings` | **Date**: 2026-04-08

## Prerequisites

- Phase 1 (Identity) and Phase 2 (Organizations) fully implemented and passing tests
- PostgreSQL running with existing migrations applied
- .NET 10 SDK installed

## Implementation Order

### Step 1: Models & Enums

Create entities and enums under `Features/Meetings/Models/`:

1. `MeetingStatus.cs` — enum (Scheduled, InProgress, Completed, Cancelled, Failed)
2. `MeetingRole.cs` — enum (Host, CoHost, Participant, Observer)
3. `RecurrenceFrequency.cs` — enum (Daily, Weekly, Monthly)
4. `Meeting.cs` — entity inheriting `BaseEntity`, implementing `IHasOrganizationId`
5. `MeetingParticipant.cs` — entity inheriting `BaseEntity`, implementing `IHasOrganizationId`
6. `MeetingMeetingTag.cs` — junction entity (no BaseEntity, composite PK)
7. `Events/MeetingEvents.cs` — domain event records

### Step 2: Entity Configurations

Create EF Core configurations under `Features/Meetings/Infrastructure/Persistence/Configurations/`:

1. `MeetingConfiguration.cs` — table, indexes, JSONB owned entity for RecurrenceConfig
2. `MeetingParticipantConfiguration.cs` — unique constraint on (MeetingId, UserId)
3. `MeetingMeetingTagConfiguration.cs` — composite PK, foreign keys

### Step 3: DbContext Registration

Add to `ApplicationDbContext`:
```csharp
public DbSet<Meeting> Meetings => Set<Meeting>();
public DbSet<MeetingParticipant> MeetingParticipants => Set<MeetingParticipant>();
public DbSet<MeetingMeetingTag> MeetingMeetingTags => Set<MeetingMeetingTag>();
```

Then generate EF Core migration: `dotnet ef migrations add AddMeetings`

### Step 4: Contracts

Create request/response records under `Features/Meetings/Contracts/`:
- See [contracts/](contracts/) for full schemas

### Step 5: Validators

Create FluentValidation validators under `Features/Meetings/Validators/`:
- Follow the pattern in `CreateMeetingTagRequestValidator`

### Step 6: Services

Implement services under `Features/Meetings/Services/` in this order:
1. `IMeetingService` / `MeetingService` — create, update, cancel, list
2. `IParticipantService` / `ParticipantService` — add, remove, conflict check
3. `IRecurrenceService` / `RecurrenceService` — generate instances from config
4. `ICalendarService` / `CalendarService` — week query

### Step 7: Endpoints

Create partial controllers and endpoint files under `Features/Meetings/Endpoints/`:
- Follow the partial controller pattern from Phase 2
- One action method per file

### Step 8: DI Registration

Create `MeetingsDI.cs` and register in `Program.cs`:
```csharp
builder.Services.AddMeetingsFeature();
```

### Step 9: Mapping

Create `Mapping/MeetingMappingConfig.cs` for Mapster configurations.

### Step 10: Tests

- Unit tests for RecurrenceService (instance generation logic) and conflict detection
- Integration tests for the full meeting lifecycle

## Key Patterns to Follow

| Pattern | Reference File |
|---------|---------------|
| Entity with tenant isolation | `Features/Organizations/Models/MeetingTag.cs` |
| Service with Result pattern | `Features/Organizations/Services/MeetingTagService.cs` |
| Partial controller definition | `Features/Organizations/Endpoints/Organization/OrganizationController.cs` |
| Endpoint file | `Features/Organizations/Endpoints/Organization/CreateOrganizationEndpoint.cs` |
| Error definitions | `Shared/Errors/OrganizationErrors.cs` |
| Entity configuration | `Features/Organizations/Infrastructure/Persistence/Configurations/MeetingTagConfiguration.cs` |
| Validator | `Features/Organizations/Validators/CreateMeetingTagRequestValidator.cs` |
| DI registration | `Features/Organizations/OrganizationsDI.cs` |
| Domain events | `Features/Organizations/Models/Events/OrganizationEvents.cs` |

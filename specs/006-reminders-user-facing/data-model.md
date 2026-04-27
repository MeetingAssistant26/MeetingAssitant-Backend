# Data Model: Reminders — User-Facing

**Feature**: Phase 5.6 Reminders — User-Facing
**Date**: 2026-04-27

## Entity: Reminder

### C# Entity Class

```csharp
public sealed class Reminder : EntityBase
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    
    public string Text { get; set; } = null!;
    
    public ReminderScope Scope { get; set; }
    public ReminderChannel Channel { get; set; }
    
    public Guid CreatedByUserId { get; set; }
    public Guid? TargetUserId { get; set; }
    public Guid? MeetingId { get; set; }
    
    public DateTime ReminderAtUtc { get; set; }
    
    public ReminderStatus Status { get; set; } = ReminderStatus.Active;
    public DateTime? DeliveredAtUtc { get; set; }
    
    public string? OriginalText { get; set; }
}

public enum ReminderScope
{
    Personal = 0,
    Public = 1
}

public enum ReminderChannel
{
    User = 0,
    Agent = 1
}

public enum ReminderStatus
{
    Active = 0,
    Delivered = 1,
    Cancelled = 2
}
```

### EF Core Configuration (Fluent API)

```csharp
public void Configure(EntityTypeBuilder<Reminder> builder)
{
    builder.ToTable("Reminders");
    
    builder.HasKey(r => r.Id);
    
    builder.Property(r => r.Text)
        .IsRequired()
        .HasMaxLength(500);
    
    builder.Property(r => r.Scope)
        .HasConversion<string>()
        .HasMaxLength(20);
    
    builder.Property(r => r.Channel)
        .HasConversion<string>()
        .HasMaxLength(20);
    
    builder.Property(r => r.Status)
        .HasConversion<string>()
        .HasMaxLength(20);
    
    builder.Property(r => r.OriginalText)
        .HasMaxLength(500);
    
    builder.HasIndex(r => new { r.TargetUserId, r.Status })
        .HasDatabaseName("IX_Reminders_TargetUserId_Status");
    
    builder.HasIndex(r => new { r.MeetingId, r.Scope, r.Status })
        .HasDatabaseName("IX_Reminders_MeetingId_Scope_Status");
    
    builder.HasIndex(r => new { r.OrganizationId, r.Status, r.ReminderAtUtc })
        .HasDatabaseName("IX_Reminders_Org_Status_ReminderAtUtc");
    
    // Tenant isolation via global query filter in AppDbContext
}
```

### Database Indexes

| Index Name | Columns | Purpose |
|-----------|---------|---------|
| `IX_Reminders_TargetUserId_Status` | `(TargetUserId, Status)` | Fast "my reminders" lookup for Personal reminders |
| `IX_Reminders_MeetingId_Scope_Status` | `(MeetingId, Scope, Status)` | Fast agent "meeting reminders" lookup (Phase 5.7) |
| `IX_Reminders_Org_Status_ReminderAtUtc` | `(OrganizationId, Status, ReminderAtUtc)` | Supports tenant-scoped list queries with status + timing filter |

## Entity Relationships

```
Reminder
├── Organization (implicit via OrganizationId + global filter)
├── CreatedByUser → ApplicationUser (via CreatedByUserId)
├── TargetUser → ApplicationUser (via TargetUserId, nullable)
└── Meeting → Meeting (via MeetingId, nullable)
```

**Note**: No explicit navigation properties to `ApplicationUser` or `Meeting` are required. Queries use ID lookups through `MeetingParticipant` for the "affecting me" list filter.

## State Transitions

```
┌─────────┐    mark-delivered     ┌───────────┐
│ Active  │ ─────────────────────→ │ Delivered │
└─────────┘                       └───────────┘
     │
     │ cancel
     ▼
┌───────────┐
│ Cancelled │
└───────────┘

Terminal states: Delivered, Cancelled
No transitions out of terminal states.
```

## Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `Text` | Required, non-empty, max 500 chars | Validation error (422) |
| `ReminderAtUtc` | Required, valid UTC DateTime, not before 2000-01-01 | Validation error (422) |
| `Scope` (body) | If present in body, must not be `Public` for user endpoint | Validation error (422) |
| `TargetUserId` | Required when `Scope=Personal` | Enforced by endpoint logic |
| `MeetingId` | Required when `Scope=Public` or `Channel=Agent` | Enforced by endpoint/agent logic |

## Assumptions

- `EntityBase` provides `CreatedAtUtc` and `UpdatedAtUtc` (from Phase 0.3)
- `AppDbContext` global query filter on `OrganizationId` is already active
- `MeetingParticipant` table (from Phase 3) has `(MeetingId, UserId)` composite structure for join queries

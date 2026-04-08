# Implementation Plan: [FEATURE]

**Branch**: `[###-feature-name]` | **Date**: [DATE] | **Spec**: [link]
**Input**: Feature specification from `/specs/[###-feature-name]/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

[Extract from feature spec: primary requirement + technical approach from research]

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: ASP.NET Core EF Core (Npgsql), MediatR, Mapster
**Storage**: PostgreSQL (pgvector not directly used for this phase, but standard JSONB mapping needed)
**Validation**: FluentValidation (single source of truth, SharpGrip auto-validation, custom `ValidationResultFactory`)
**Testing**: xUnit, NSubstitute
**Target Platform**: Linux server (Docker)
**Project Type**: web-service
**Performance Goals**: < 300ms p95 for API requests
**Constraints**: Single tenant constraint via DB index `UNIQUE(user_id) WHERE is_enabled = true;`
**Scale/Scope**: Multitenant Enterprise SaaS structure

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

[x] No unknown unknowns - All constraints and decisions correctly map to standard ASP.NET features
[x] Adheres to Partial Controller pattern mandated by the project architecture
[x] Abides by isolated DB schema with `OrganizationId`
[x] Adheres to standard error processing formatting (`ResultExtensions.ToProblem()`)

## Project Structure

### Documentation (this feature)

```text
specs/[###-feature]/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
src/
├── Features/
│   ├── Organizations/
│   │   ├── Endpoints/
│   │   │   ├── Organization/
│   │   │   │   ├── OrganizationController.cs
│   │   │   │   ├── CreateOrganizationEndpoint.cs
│   │   │   │   └── LeaveOrganizationEndpoint.cs
│   │   │   ├── Member/
│   │   │   │   ├── MemberController.cs
│   │   │   │   ├── ListMembersEndpoint.cs
│   │   │   │   ├── UpdateMemberRoleEndpoint.cs
│   │   │   │   └── UpdateMemberContextEndpoint.cs
│   │   │   ├── Invitation/
│   │   │   │   ├── InvitationController.cs
│   │   │   │   ├── CreateInvitationEndpoint.cs
│   │   │   │   └── JoinInvitationEndpoint.cs
│   │   │   └── MeetingTag/
│   │   │       ├── MeetingTagController.cs
│   │   │       ├── ListMeetingTagsEndpoint.cs
│   │   │       ├── CreateMeetingTagEndpoint.cs
│   │   │       ├── UpdateMeetingTagEndpoint.cs
│   │   │       └── DeleteMeetingTagEndpoint.cs
│   │   ├── Models/
│   │   ├── Services/
│   │   └── Validators/
├── Infrastructure/
└── Shared/

tests/
├── Features.UnitTests/
└── Features.IntegrationTests/
```

**Structure Decision**: ASP.NET Core project using the requested Partial Controller Pattern inside a Vertical Slice architecture for routing. One endpoint file per action.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |

# MeetingAssistant Constitution

## Core Principles

### I. Vertical Slice Architecture
Features are grouped by domain (e.g., Organizations, Users) rather than technical grouping. Each slice contains its own Models, Services, Validators, and Endpoints.

### II. Partial Controller Pattern
Enforce exactly one API endpoint per file using partial controller classes. The base controller file defines routes and shared dependencies; endpoint files contain a single action method to maintain singular responsibility.

### III. Tenant Isolation by Default
Every organization scope must inherently use OrganizationId for direct tenant isolation. This MUST be structurally enforced via EF Core Global Query Filters on all tenant-specific entities.

### IV. Strict Single Membership Rule
A user can only actively belong to one organization at a time. This invariant MUST be enforced at the database level using constraints, for example: UNIQUE(user_id) WHERE is_enabled = true.

### V. Standardized Operational Errors
All endpoints must return standard RFC 7807 problem details. All endpoints MUST use esult.ToProblem(correlationIdProvider) so that external clients have tracing IDs for debugging context.

## Development Constraints
- Use ASP.NET Core (.NET 10) and PostgreSQL via Npgsql.
- No new features can be implemented without automated Contract/Integration tests covering the Acceptance Scenarios.

## Governance
All code must pass through speckit checks before implementation. Exceptions to the Partial Controller or DB tenant pattern require explicit recorded consensus.

**Version**: 1.0.0 | **Ratified**: 2026-03-15

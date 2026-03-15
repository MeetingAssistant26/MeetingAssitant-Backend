# Phase 0: Research & Technical Approach

## 1. Architectural Pattern: Partial Controller Pattern
**Decision**: We will implement the feature using the **Partial Controller Pattern** as mandated by the implementation plan (v3.3).
**Rationale**: This promotes a vertical slice architecture where each endpoint gets its own file for the handler method, while keeping routing configuration centralized in a controller definition file.
**Alternatives considered**: Traditional monolithic controllers or Minimal APIs were rejected based on architecture guidance.

## 2. Tenant Isolation
**Decision**: Use an `OrganizationId` global query filter in `AppDbContext` via Entity Framework Core. Scoped DB context injected per HTTP request enforces multitenancy.
**Rationale**: Easiest way to guarantee developers don't accidentally query data from other organizations, satisfying the cross-tenant isolation requirement (FR-005).

## 3. Membership Enforcement
**Decision**: Enforce single active membership with a PostgreSQL partial unique index: `CREATE UNIQUE INDEX UX_UserOrgMembership_UserId ON UserOrgMemberships (UserId) WHERE IsEnabled = true;`. 
**Rationale**: Protects data integrity strictly at the database tier in case of concurrent requests or race conditions. Also covers the scenario where a user leaves an organization (`IsEnabled = false`) and joins another.

## 4. Invitation Email Storage
**Decision**: Store `EmailWhitelist` as a JSONB column in PostgreSQL (`jsonb[]` or serialized JSON mapped to a string collection). EF Core 8/9/10 natively supports mapping primitive collections to/from PostgreSQL JSON arrays.
**Rationale**: We don't need relational lookups for the whitelist, we just need to read the list and verify the accepting user's email against it at runtime.

## 5. Admin Removal Protection
**Decision**: Perform a concurrency-safe check in the database transaction when a user leaves or changes roles, doing a COUNT of active admins with a repository lock. Or emit a database function/trigger. Simpler: read lock `SELECT ... FOR UPDATE` or `SERIALIZABLE` isolation level in EF Core to ensure at least 1 admin remains.
**Rationale**: Guaranteed safety against concurrent admin demotions/leaves that would orphan an organization.
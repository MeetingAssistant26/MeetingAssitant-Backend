# Phase 0 Research: Opus Realtime Amendment

**Source spec**: [spec.md](./spec.md)

The Technical Context in [plan.md](./plan.md) has no `NEEDS CLARIFICATION` markers — every unknown was resolved against the existing codebase, the constitution, and the clarification answers captured in the spec. This document records the five decisions that shaped the plan so Phase 1 and /speckit.tasks can trace them.

---

## R-1 — How is `TranscriptPipelineStatus` computed today when no `TranscriptSegment` table exists?

**Decision**: Introduce an `ITranscriptReadService` seam. This phase ships a `StubTranscriptReadService` that always returns `(NotStarted, empty)` for ended meetings and has no dependency on persistence. Phase 5.5 replaces the stub with a real implementation that inspects the forthcoming `TranscriptSegment` rows and Hangfire job state.

**Rationale**:
- FR-009 forbids new stored business records in this phase, which rules out adding `TranscriptSegment` here.
- FR-006 still requires the endpoint to exist.
- A stub preserves the public contract (FR-007 response shape; FR-012 in-progress rejection) and makes the integration tests in this phase real, not aspirational.
- The stub is an internal implementation detail behind an interface — it is not a new stored business record, so it does not violate FR-009.

**Alternatives considered**:
- *Defer the endpoint until Phase 5.5*: rejected because the spec clarifications (Q1/Q2) and FR-007/FR-012 are specifically about the endpoint's response shape and error path; leaving the endpoint unimplemented would leave those requirements untestable and push contract churn into Phase 5.5.
- *Add `TranscriptSegment` here and skip the stub*: rejected; violates FR-009 ("no new stored business records").
- *Derive pipeline status from Hangfire state directly*: rejected for this phase — no STT jobs exist yet, so there is nothing to query. Phase 5.5 can add that logic when it adds the jobs.

---

## R-2 — What is the correct HTTP surface for FR-012 ("reject live-meeting requests")?

**Decision**: `409 Conflict` returned via `Result.Conflict(...).ToProblem(_correlationIdProvider)` using the repo's existing RFC 7807 problem-details pipeline. Problem detail `title = "Meeting is still in progress"`; `detail` explains the transcript is available only after the meeting ends.

**Rationale**:
- Constitution §V mandates RFC 7807 via `Result.ToProblem(correlationIdProvider)` — used by every LiveSession endpoint today (e.g. [GetJoinTokenEndpoint](../../MeetingAssistant/Features/LiveSession/Endpoints/Session/GetJoinTokenEndpoint.cs)).
- `409 Conflict` is semantically accurate: the caller's request conflicts with the current state of the meeting (not ended yet). `400` implies client-side malformation; `404` implies the meeting doesn't exist; `425 Too Early` is unusual and poorly supported by tooling.
- The shape is distinct from the normal success response (FR-012: "MUST NOT return the normal segment-plus-status response shape"), because a problem-details body does not include `segments` or `pipelineStatus`.

**Alternatives considered**:
- `400 Bad Request`: rejected; the request is well-formed, the *state* is wrong.
- `425 Too Early`: rejected; rarely used, surprises tooling, and semantically matches "replay attack" not "come back later."
- Custom `200 OK` with `pipelineStatus = "MeetingInProgress"`: rejected by Q2 clarification — adding that status value conflates lifecycle and pipeline states.

---

## R-3 — Which authorization policy enforces OrgAdmin-only access?

**Decision**: Reuse the existing `"RequireOrgAdmin"` policy registered in [AuthDI.cs:104](../../MeetingAssistant/Infrastructure/DependencyInjection/AuthDI.cs#L104). The `GetTranscriptEndpoint` action gets `[Authorize(Policy = "RequireOrgAdmin")]`. The base controller continues to carry `[EnforceOrgAccess]` for tenant-scope validation (matches [SessionController](../../MeetingAssistant/Features/LiveSession/Endpoints/Session/SessionController.cs)).

**Rationale**:
- Constitution §III requires tenant isolation; the existing filter + policy combination already provides it and is used across Organizations endpoints.
- The spec (FR-006) explicitly rules out role overrides: Host/CoHost/Participant all denied regardless of meeting role. The policy enforces the org-admin claim; the `[EnforceOrgAccess]` filter ensures the caller is an admin *of this org*, not some other org.
- Reusing the policy avoids introducing new authorization primitives.

**Alternatives considered**:
- *Add a new `"RequireTranscriptDebug"` policy*: rejected; the spec clarification (Q-on-non-admin denial) collapses "admin debug access" into the standard OrgAdmin role. A distinct policy would suggest a separate concept that isn't there.
- *Hybrid: allow OrgAdmin OR meeting host*: rejected by the third clarification in the spec ("Strict OrgAdmin-only. No exceptions.") embedded in FR-006.

---

## R-4 — Route shape and partial-controller layout

**Decision**:

- Base controller: `api/organizations/{orgId:guid}/meetings/{meetingId:guid}/transcript`, carrying `[Authorize]` + `[EnforceOrgAccess]`.
- Action: `HttpGet("")` returning `TranscriptDebugResponse` on success and RFC 7807 on every failure path.
- One endpoint file per action (constitution §II).

**Rationale**:
- Every existing LiveSession endpoint is nested under `api/organizations/{orgId:guid}/meetings/{meetingId:guid}/...` — see [SessionController](../../MeetingAssistant/Features/LiveSession/Endpoints/Session/SessionController.cs). Matching that pattern keeps tenant isolation structural.
- Phase 4's design referenced `GET /api/meetings/{meetingId}/transcript` (no `{orgId}`); that was an under-specified route in the implementation plan. Following the codebase convention is more consistent with constitution §III and with the 19-controller inventory referenced in [docs/implementation-plan.md](../../docs/implementation-plan.md).

**Alternatives considered**:
- `api/meetings/{meetingId}/transcript` (flat, no org): rejected; breaks the structural tenancy convention and requires the controller to resolve the org from the meeting at runtime, which is exactly the antipattern §III calls out.

---

## R-5 — Integration test infrastructure

**Decision**: Add `TranscriptDebugEndpointTests.cs` alongside [LifecycleWebhookTests.cs](../../tests/Integration/LiveSession/LifecycleWebhookTests.cs) and [JoinTokenTests.cs](../../tests/Integration/LiveSession/JoinTokenTests.cs), sharing the existing `LiveSessionTestSupport` fixture and `WebApplicationFactory` setup. xUnit is already the test runner across this suite.

**Rationale**:
- Six integration scenarios cover the acceptance surface: (1) OrgAdmin, meeting ended, no segments → `NotStarted`+empty; (2) OrgAdmin, meeting ended, stub could report `Processing`/`Completed`/`Failed` via test-double; (3) non-admin org member → 403; (4) meeting host who is not OrgAdmin → 403; (5) meeting in progress → 409; (6) tenant cross-check: OrgAdmin of a different org → 403/404.
- The stub implementation is swappable in the test DI container, so scenarios (2) can set the status freely without any persistence.
- No new test infrastructure is required — the existing `LiveSessionTestSupport` handles DB seeding via Testcontainers.

**Alternatives considered**:
- *Unit-test the endpoint against a mocked service*: lower value than integration tests here because the constitution requires contract/integration coverage for acceptance scenarios, and most of what's being validated is HTTP+auth+shape, not logic.
- *Add a separate test project for this feature*: rejected; one more project for one endpoint is overhead without benefit.

---

## Open questions carried forward

None. Every item in the clarifications section of the spec has a concrete Phase 1 landing spot (see [data-model.md](./data-model.md) and [contracts/transcript-endpoint.md](./contracts/transcript-endpoint.md)).

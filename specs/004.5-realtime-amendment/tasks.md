---

description: "Tasks for Opus Realtime Amendment (Phase 4.5)"
---

# Tasks: Opus Realtime Amendment (Phase 4.5)

**Input**: Design documents from `specs/005-opus-realtime-amendment/`
**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/transcript-endpoint.md](./contracts/transcript-endpoint.md), [quickstart.md](./quickstart.md)

**Tests**: REQUIRED. Constitution dev-constraint: *"No new features can be implemented without automated Contract/Integration tests covering the Acceptance Scenarios."* Integration tests are written first and must fail before each story's implementation lands.

**Organization**: Tasks grouped by user story from [spec.md](./spec.md). US1 and US2 are enforce-the-absence stories — their only tasks are assertions/documentation. US3 carries all implementation work.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Maps to user story from spec.md (US1, US2, US3)
- File paths are absolute from repo root

## Path Conventions

- Backend code lives under `MeetingAssistant/`.
- Integration tests live under `tests/MeetingAssistant.Tests.Integration/`.
- No frontend in this repo; all tasks are backend.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Confirm preconditions and scaffolding needed by downstream stories. No package adds, no EF migrations — this phase adds no entities.

- [ ] T001 Verify the empty folder [MeetingAssistant/Features/LiveSession/Endpoints/Transcript/](../../MeetingAssistant/Features/LiveSession/Endpoints/Transcript/) exists; if not, create it (it will hold the new controller + endpoint files in US3).
- [ ] T002 Verify the `"RequireOrgAdmin"` authorization policy is registered at [MeetingAssistant/Infrastructure/DependencyInjection/AuthDI.cs:104](../../MeetingAssistant/Infrastructure/DependencyInjection/AuthDI.cs#L104). No change if present; fail the task and surface a fix proposal if absent (research.md R-3 assumes reuse).
- [ ] T003 Verify the `EnforceOrgAccess` filter is available to LiveSession controllers (import path used by [SessionController](../../MeetingAssistant/Features/LiveSession/Endpoints/Session/SessionController.cs)). No code change if present.

**Checkpoint**: Folder scaffolding ready; auth policy + tenant filter confirmed reusable.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Nothing new is required — every foundational dependency (EF Core, Postgres, `Result`/`ToProblem`, correlation-id provider, LiveSession DI, LiveSessionTestSupport test harness) is already in place. This phase is intentionally empty.

> No tasks. Proceed directly to user stories.

**Checkpoint**: Foundation ready — user story implementation can now begin.

---

## Phase 3: User Story 1 — Live Sessions Run Without Any Transcription Promise (Priority: P1) 🎯 MVP

**Goal**: Enforce the scope boundary that the system produces no live transcription output and emits no live-transcription status signals during a meeting. This is a *negative* requirement; no new production code is added by US1 — the code already lacks these surfaces. This phase adds regression guards plus documentation so the constraint cannot silently regress.

**Independent Test**: Start a meeting with two participants, exercise a full lifecycle (join → speech → leave → room finished), and observe every channel (SignalR events, REST responses, DB writes). Story passes when no transcript text, caption stream, or `transcription.*` event is produced and the meeting itself behaves normally.

### Tests for User Story 1

- [ ] T004 [US1] Create [tests/MeetingAssistant.Tests.Integration/LiveSession/NoLiveTranscriptionTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/NoLiveTranscriptionTests.cs) and add the SignalR regression test — connect a SignalR client to `LiveSessionHub`, drive a meeting through `session.started` / `participant.joined` / `participant.left` / `session.ended`, and assert that zero events whose method name starts with `transcription.` or contains `caption` are received. Scenario list: at least `transcription.started`, `transcription.progress`, `transcription.completed`, `transcription.failed`, `transcription.status`.
- [ ] T005 [US1] In the same file [tests/MeetingAssistant.Tests.Integration/LiveSession/NoLiveTranscriptionTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/NoLiveTranscriptionTests.cs), add a reflection-based assertion test that inspects `ILiveSessionNotifier` and asserts no method name contains `Transcription` or `Caption`. Prevents a future `NotifyTranscriptionStartedAsync` from being added without removing this test. Depends on T004 (same file scaffolded).

### Implementation for User Story 1

- [ ] T006 [P] [US1] Update [docs/implementation-plan.md](../../docs/implementation-plan.md) Phase 4 section (or the amendment pointer to Phase 4.5 — whichever already references live transcription) so that the "live STT / live captions" item is unambiguously marked out of scope and references [specs/005-opus-realtime-amendment/spec.md](./spec.md) for the authoritative product-level scope. Do not invent a new section; edit the existing Phase 4 language surgically.

**Checkpoint**: No production code added. Regression tests fail to compile if a live-transcription method is re-introduced. Documentation reflects the amendment.

---

## Phase 4: User Story 2 — Room Lifecycle Visibility Is Preserved (Priority: P2)

**Goal**: Confirm that the four lifecycle events (`session.started`, `participant.joined`, `participant.left`, `session.ended`) continue to fire with the right payloads and the right tenant scoping after the amendment. No code change — this phase is a regression guard anchored to the existing [LiveSessionNotifier](../../MeetingAssistant/Features/LiveSession/Hubs/LiveSessionNotifier.cs).

**Independent Test**: Drive a full meeting lifecycle with two participants; assert the four expected events are received in order on the org-scoped SignalR group and no other event types are received.

### Tests for User Story 2

- [ ] T007 [US2] Create [tests/MeetingAssistant.Tests.Integration/LiveSession/LifecycleStillWorksTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/LifecycleStillWorksTests.cs) and add the end-to-end lifecycle regression test — connect a SignalR client to the org-scoped group `org:{organizationId}`, drive a synthetic meeting (via the existing LiveKit webhook test support in [tests/Integration/LiveSession/LifecycleWebhookTests.cs](../../tests/Integration/LiveSession/LifecycleWebhookTests.cs)), and assert receipt of exactly: `session.started`, `participant.joined`, `participant.left`, `session.ended`. No other event types.
- [ ] T008 [US2] In the same file [tests/MeetingAssistant.Tests.Integration/LiveSession/LifecycleStillWorksTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/LifecycleStillWorksTests.cs), add a tenant-isolation assertion: a client connected to the group of Org B receives zero events while Org A's meeting runs (piggybacks on the pattern in [TenantScopedNotificationTests.cs](../../tests/Integration/LiveSession/TenantScopedNotificationTests.cs)). Depends on T007.

### Implementation for User Story 2

> No production code changes. Tests defend behavior that already exists.

**Checkpoint**: Lifecycle events still flow correctly; cross-tenant leakage still does not occur.

---

## Phase 5: User Story 3 — Transcript Is Treated As Post-Meeting Internal Data (Priority: P3)

**Goal**: Ship the administrator-only transcript debug endpoint, its response types, the `ITranscriptReadService` seam, and the stub implementation. Enforce the FR-006 authorization, FR-007 response shape, FR-008 ordering, and FR-012 rejection of live meetings. See [contracts/transcript-endpoint.md](./contracts/transcript-endpoint.md) for the full HTTP contract.

**Independent Test**: Sign in as an OrgAdmin and hit `GET /api/organizations/{orgId}/meetings/{meetingId}/transcript` for a `Completed` meeting — receive a 200 with `{ "segments": [], "pipelineStatus": "NotStarted" }`. Repeat as a non-admin (403) and against an `InProgress` meeting (409). All three shape invariants from FR-007/FR-012 hold.

### Tests for User Story 3 — write these FIRST; they must FAIL before implementation

- [ ] T009 [US3] Create the integration test file [tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs) reusing `LiveSessionTestSupport`. Add scenario **#1** (OrgAdmin + `Completed` meeting + default stub → 200, `pipelineStatus == "NotStarted"`, `segments == []`).
- [ ] T010 [US3] In [tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs), add scenarios **#2 and #3** for stub-injected non-default states: replace the DI-registered `ITranscriptReadService` with a test double returning `(Processing, 2 ordered segments)` and `(Failed, [])`; assert 200 + correct `pipelineStatus` + segments ordered by `StartTime` ascending (validates FR-007 + FR-008). Depends on T009.
- [ ] T011 [US3] In [tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs), add scenario **#4** (non-admin org member → 403) and scenario **#5** (meeting host who is not OrgAdmin → 403) validating FR-006 / SC-004. Depends on T009.
- [ ] T012 [US3] In [tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs), add scenario **#6** (OrgAdmin of a *different* org than the meeting's org → 404 or 403; caller cannot learn which) validating tenant isolation per constitution §III. Depends on T009.
- [ ] T013 [US3] In [tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs), add scenario **#7** (OrgAdmin + `InProgress` meeting → 409, problem body, `meetingStatus == "InProgress"`, no `segments`/`pipelineStatus` keys in body) validating FR-012 / SC-007. Depends on T009.
- [ ] T014 [US3] In [tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs), add scenario **#8** (OrgAdmin + `Scheduled` meeting → 409, `meetingStatus == "Scheduled"`) validating the data-model.md extension of FR-012 to the `Scheduled` state. Depends on T009.
- [ ] T015 [US3] In [tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs](../../tests/MeetingAssistant.Tests.Integration/LiveSession/TranscriptDebugEndpointTests.cs), add scenario **#9** (OrgAdmin + unknown `meetingId` → 404) and scenario **#10** (no `Authorization` header → 401). Depends on T009.

### Implementation for User Story 3

- [ ] T016 [P] [US3] Create the response-only enum file [MeetingAssistant/Features/LiveSession/Contracts/Responses/TranscriptPipelineStatus.cs](../../MeetingAssistant/Features/LiveSession/Contracts/Responses/TranscriptPipelineStatus.cs) with values `NotStarted = 0`, `Processing = 1`, `Completed = 2`, `Failed = 3`. Ensure `JsonStringEnumConverter` serializes it by name (verify global JSON options in [MeetingAssistant/Program.cs](../../MeetingAssistant/Program.cs); add a `[JsonConverter(typeof(JsonStringEnumConverter))]` attribute on the enum only if the global default is not `JsonStringEnumConverter`).
- [ ] T017 [P] [US3] Create the read-model record [MeetingAssistant/Features/LiveSession/Contracts/Responses/TranscriptSegmentView.cs](../../MeetingAssistant/Features/LiveSession/Contracts/Responses/TranscriptSegmentView.cs) with shape `(Guid Id, Guid SpeakerUserId, TimeSpan StartTime, TimeSpan EndTime, string Text, int SequenceNumber)` per data-model.md.
- [ ] T018 [US3] Create the response DTO [MeetingAssistant/Features/LiveSession/Contracts/Responses/TranscriptDebugResponse.cs](../../MeetingAssistant/Features/LiveSession/Contracts/Responses/TranscriptDebugResponse.cs) as `sealed record TranscriptDebugResponse(IReadOnlyList<TranscriptSegmentView> Segments, TranscriptPipelineStatus PipelineStatus)`. Depends on T016 + T017.
- [ ] T019 [P] [US3] Create the service seam [MeetingAssistant/Features/LiveSession/Services/ITranscriptReadService.cs](../../MeetingAssistant/Features/LiveSession/Services/ITranscriptReadService.cs) with the signature `Task<Result<TranscriptDebugResponse>> ReadAsync(Guid organizationId, Guid meetingId, CancellationToken cancellationToken)` per data-model.md.
- [ ] T020 [US3] Implement [MeetingAssistant/Features/LiveSession/Services/StubTranscriptReadService.cs](../../MeetingAssistant/Features/LiveSession/Services/StubTranscriptReadService.cs). Inject `ApplicationDbContext` (or a meeting reader abstraction already used in the LiveSession slice). Behavior:
  - Query `Meeting` by `(Id == meetingId && OrganizationId == organizationId)`. If none → `Result.NotFound(...)`.
  - If `Meeting.Status` is `Scheduled` or `InProgress` → `Result.Conflict(...)` with a problem payload including the meeting status string.
  - If `Meeting.Status` is `Completed`, `Cancelled`, or `Failed` → `Result.Success(new TranscriptDebugResponse(Array.Empty<TranscriptSegmentView>(), TranscriptPipelineStatus.NotStarted))`.
  - No persistence writes; no Hangfire interaction. Depends on T018 + T019.
- [ ] T021 [P] [US3] Create the partial controller base [MeetingAssistant/Features/LiveSession/Endpoints/Transcript/TranscriptController.cs](../../MeetingAssistant/Features/LiveSession/Endpoints/Transcript/TranscriptController.cs):
  - `[ApiController]`, `[Route("api/organizations/{orgId:guid}/meetings/{meetingId:guid}/transcript")]`, `[Authorize]`, `[EnforceOrgAccess]` — mirrors [SessionController](../../MeetingAssistant/Features/LiveSession/Endpoints/Session/SessionController.cs).
  - Constructor injects `ITranscriptReadService transcriptReadService` and `ICorrelationIdProvider correlationIdProvider` into protected readonly fields.
  - References `ITranscriptReadService` from T019; independent of T020. May run parallel with T020.
- [ ] T022 [US3] Create the single-action endpoint file [MeetingAssistant/Features/LiveSession/Endpoints/Transcript/GetTranscriptEndpoint.cs](../../MeetingAssistant/Features/LiveSession/Endpoints/Transcript/GetTranscriptEndpoint.cs) as `partial class TranscriptController`:
  - `[HttpGet("")]` + `[Authorize(Policy = "RequireOrgAdmin")]` on the action method.
  - Route-binds `[FromRoute] Guid orgId`, `[FromRoute] Guid meetingId`, `CancellationToken cancellationToken`.
  - Calls `_transcriptReadService.ReadAsync(orgId, meetingId, cancellationToken)` and returns `result.IsSuccess ? Ok(result.Value) : result.ToProblem(_correlationIdProvider)`.
  - Depends on T021.
- [ ] T023 [US3] Update [MeetingAssistant/Features/LiveSession/LiveSessionDI.cs](../../MeetingAssistant/Features/LiveSession/LiveSessionDI.cs) to register `services.AddScoped<ITranscriptReadService, StubTranscriptReadService>();`. Depends on T019 + T020.
- [ ] T024 [US3] Run the T009–T015 test suite against the implemented endpoint; fix defects until all ten scenarios pass. At that point SC-004, SC-005, SC-007 are green.

**Checkpoint**: Administrator-only transcript debug endpoint is fully functional; live-meeting rejection works; response shape invariants hold; non-admin denial is exhaustive. US3 is independently deployable.

---

## Phase 6: Polish & Cross-Cutting Concerns

- [ ] T025 [P] Cross-check [specs/004-realtime-pipeline/spec.md](../004-realtime-pipeline/spec.md) for any mention of live transcription; add a pointer line noting it is superseded by [spec.md](./spec.md). Surgical edit only — do not rewrite Phase 4's spec.
- [ ] T026 [P] Add a comment block at the top of [MeetingAssistant/Features/LiveSession/Services/StubTranscriptReadService.cs](../../MeetingAssistant/Features/LiveSession/Services/StubTranscriptReadService.cs) (one-line only, per repo style): `// Stub reader; Phase 5.5 replaces this with a TranscriptSegment-backed implementation.` This is the one case where a comment earns its keep — it flags a planned replacement that is not visible from the code alone.
- [ ] T027 Execute [quickstart.md](./quickstart.md) steps 1–6 against a fresh `docker-compose up -d` stack; file any deltas as follow-ups. Do not mark the phase complete until the six manual verifications pass.
- [ ] T028 Open [specs/005-opus-realtime-amendment/checklists/requirements.md](./checklists/requirements.md) and confirm every item is still checked given the as-built code. Tick-mark any items that were deferred.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies — can start immediately.
- **Phase 2 (Foundational)**: Empty; proceed directly to Phase 3.
- **Phase 3 (US1)**: Depends on Phase 1. Adds no production code — can run in parallel with US2 and US3 once scaffolding is confirmed.
- **Phase 4 (US2)**: Depends on Phase 1. Independent of US1 and US3.
- **Phase 5 (US3)**: Depends on Phase 1. Independent of US1 and US2 for most work; tests T009–T015 depend on T001 scaffolding.
- **Phase 6 (Polish)**: Depends on the three user-story phases being complete.

### User Story Dependencies

- **US1, US2, US3 are mutually independent.** Each can be delivered standalone.

### Within Each User Story

- Tests are written first (xUnit skeleton compiles and fails) and implementation is written to make them pass — this is enforced by the constitution.
- For US3: models → service interface → service impl → controller base → endpoint action → DI registration → green test suite.

### Parallel Opportunities

- **Phase 1**: T002 and T003 are parallel verifications [P] (independent files).
- **US1**: T004 creates the test file; T005 adds to the same file (sequential). T006 edits an unrelated doc and is [P] against T004/T005.
- **US2**: T007 creates the test file; T008 adds to the same file (sequential).
- **US3 tests**: T009 scaffolds the test file; T010–T015 each add scenarios to the same file and are therefore *sequential* with T009 and with each other at the edit level (same file). Different developers can still review them in parallel, but a single author should commit them in order to avoid merge conflicts.
- **US3 implementation**: T016, T017, T019, T021 are all [P] — four different new files with no cross-file dependencies at creation time. T018 depends on T016+T017 (same folder, different file). T020 depends on T018+T019. T022 depends on T021. T023 depends on T019+T020. T024 depends on T022+T023+all tests.
- **Polish**: T025 and T026 are [P] (different files). T027 and T028 are sequential after everything else.

### Cross-Story Integration

- US3's DI registration (T023) does not affect US1/US2 — their tests are read-only against SignalR, not the transcript endpoint.
- US1's reflection test (T005) trips only if someone adds a `Transcription` or `Caption` method to `ILiveSessionNotifier`; US3 does not add such a method.

---

## Parallel Example: User Story 3 Test Authoring

```bash
# Ten integration scenarios in one file — author them in any order in parallel.
Task: "scenario #1 OrgAdmin + Completed + stub default → 200 NotStarted []"   (T009)
Task: "scenarios #2,#3 OrgAdmin + stub overrides → 200 Processing/Failed"     (T010)
Task: "scenarios #4,#5 non-admin + meeting host → 403"                        (T011)
Task: "scenario #6 cross-org OrgAdmin → 404/403"                              (T012)
Task: "scenario #7 InProgress → 409"                                          (T013)
Task: "scenario #8 Scheduled → 409"                                           (T014)
Task: "scenarios #9,#10 unknown meeting → 404; no auth header → 401"          (T015)
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

US1 *is* the MVP of Phase 4.5: it asserts the scope boundary. Steps:

1. Complete Phase 1 (scaffolding verification).
2. Skip Phase 2 (empty).
3. Complete Phase 3 (US1 regression tests + doc edit).
4. Stop. The amendment's primary promise — "no live transcription" — is now enforceable and documented.

At this point the project can be deployed; US2 and US3 are additive.

### Incremental Delivery

1. Setup (Phase 1) → done.
2. US1 (negative regression guards + doc) → ship. MVP complete.
3. US2 (lifecycle regression guards) → ship.
4. US3 (admin debug endpoint) → ship. Phase 4.5 deliverable is now complete.
5. Polish → final documentation pass and quickstart rerun.

### Parallel Team Strategy

With three developers:

1. Developer A: US1 (T004 + T005 + T006).
2. Developer B: US2 (T007 + T008).
3. Developer C: US3 (T009 through T024). Highest task count — allocate the most capable / most available developer here.

All three tracks can land independently against `main` behind the same review cycle.

---

## Notes

- [P] tasks operate on different files with no incomplete dependencies.
- [Story] labels are required on Phase 3/4/5 tasks and omitted on Phase 1/2/6 tasks per the format rules.
- Every task cites the exact file path it creates or edits.
- Every test task is authored *before* the matching implementation tasks; running the suite early and seeing red is the signal that the tests are meaningful.
- Constitution §V is satisfied by the `Result.ToProblem(_correlationIdProvider)` chain inside T022 — do not invent a second error-shaping path.
- This phase ships **zero migrations, zero new entities, zero Hangfire jobs, zero audit records.** If any task tempts otherwise, it violates FR-009 or the Q3 clarification.

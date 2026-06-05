using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using MeetingAssistant.Api.Infrastructure.Hangfire;
using MeetingAssistant.Features.ActionItems.Models;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Rag.Jobs;
using MeetingAssistant.Infrastructure.AI;
using MeetingAssistant.Infrastructure.AI.DTOs;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.ActionItems.Jobs
{
    public class ExtractActionItemsJob
    {
        private const int MaxTitleLength = 200;
        private const int MaxDescriptionLength = 2000;
        private const int MaxAuditTextLength = 200;
        private const int MaxAuditReasonLength = 500;
        private const decimal MinConfidenceForAutoAccept = 0.70m;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ApplicationDbContext _dbContext;
        private readonly ILLMService _llmService;
        private readonly IPromptProvider _promptProvider;
        private readonly ILogger<ExtractActionItemsJob> _logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker;
        private readonly IHangfireJobContextAccessor? _hangfireJobContextAccessor;
        private readonly IBackgroundJobClient? _backgroundJobClient;
        private readonly string _model;

        private Guid? _pipelineGenerationId;
        private string? _currentHangfireJobId;

        public ExtractActionItemsJob(
            ApplicationDbContext dbContext,
            ILLMService llmService,
            IPromptProvider promptProvider,
            IOptions<OpenAiCompatibleOptions> options,
            ILogger<ExtractActionItemsJob> logger,
            IPostMeetingProcessingTracker? postMeetingProcessingTracker = null,
        IHangfireJobContextAccessor? hangfireJobContextAccessor = null,
            IBackgroundJobClient? backgroundJobClient = null)
        {
            _dbContext = dbContext;
            _llmService = llmService;
            _promptProvider = promptProvider;
            _logger = logger;
            _postMeetingProcessingTracker = postMeetingProcessingTracker;
            _hangfireJobContextAccessor = hangfireJobContextAccessor;
            _backgroundJobClient = backgroundJobClient;
            _model = options.Value.Llm.Model;
        }

        [Hangfire.AutomaticRetry(Attempts = 3)]
        public Task RunAsync(Guid meetingId, Guid organizationId, CancellationToken cancellationToken)
            => RunAsync(meetingId, organizationId, pipelineGenerationId: null, cancellationToken);

        public async Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            Guid? pipelineGenerationId,
            CancellationToken cancellationToken)
        {
            _currentHangfireJobId = _hangfireJobContextAccessor?.CurrentJobId;
            _pipelineGenerationId = pipelineGenerationId;
            if (_postMeetingProcessingTracker is not null && !_pipelineGenerationId.HasValue)
            {
                var run = await _postMeetingProcessingTracker.EnsureRunAsync(
                    organizationId,
                    meetingId,
                    relatedHangfireJobId: _currentHangfireJobId,
                    cancellationToken: cancellationToken);
                _pipelineGenerationId = run.PipelineGenerationId;
            }

            _logger.LogInformation("Starting action item extraction for meeting {MeetingId}", meetingId);
            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.StartStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    message: "Action item extraction started.",
                    cancellationToken: cancellationToken);
            }

            try
            {
                var transcript = await _dbContext.MeetingTranscripts
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.OrganizationId == organizationId && t.MeetingId == meetingId, cancellationToken);

                if (transcript == null || string.IsNullOrWhiteSpace(transcript.FullText))
                {
                    if (_postMeetingProcessingTracker is not null)
                    {
                        await _postMeetingProcessingTracker.FailStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    "transcript_unavailable",
                    "No transcript found for action item extraction.",
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    cancellationToken: cancellationToken);
                    }

                    _logger.LogWarning("No transcript found for meeting {MeetingId}", meetingId);
                    return;
                }

                if (!MeetingTranscriptCompletenessGuard.IsCompleteForDownstream(transcript))
                {
                    if (_postMeetingProcessingTracker is not null)
                    {
                        await _postMeetingProcessingTracker.SkipStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    message: MeetingTranscriptCompletenessGuard.BuildIncompleteMessage(transcript),
                            cancellationToken: cancellationToken);

                        await _postMeetingProcessingTracker.RecordEventAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingEventType.Info,
                    _pipelineGenerationId,
                    PostMeetingProcessingStepType.ActionExtraction,
                            PostMeetingProcessingStatus.Skipped,
                            message: "Action item extraction skipped because meeting transcript is incomplete.",
                            errorCode: MeetingTranscriptCompletenessGuard.IncompleteErrorCode,
                            errorMessage: MeetingTranscriptCompletenessGuard.BuildIncompleteMessage(transcript),
                            cancellationToken: cancellationToken);
                    }

                    _logger.LogWarning(
                        "Action item extraction skipped because meeting transcript is incomplete. MeetingId={MeetingId} CompletenessStatus={CompletenessStatus}",
                        meetingId,
                        transcript.CompletenessStatus);
                    return;
                }

                var transcriptIdentity = TranscriptSourceIdentity.Resolve(transcript);

                var existingActionItems = await _dbContext.ActionItems
                    .IgnoreQueryFilters()
                    .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                    .ToListAsync(cancellationToken);

                var activeActionItems = existingActionItems
                    .Where(x => x.SupersededAtUtc == null)
                    .ToList();

                if (activeActionItems.Count > 0
                    && activeActionItems.All(x =>
                        string.Equals(x.SourceTranscriptHash, transcriptIdentity.TranscriptHash, StringComparison.Ordinal)
                        && (!x.SourceTranscriptRevision.HasValue
                            || x.SourceTranscriptRevision.Value == transcriptIdentity.TranscriptRevision)))
                {
                    if (_postMeetingProcessingTracker is not null)
                    {
                        await _postMeetingProcessingTracker.CompleteStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    message: "Action items already match current transcript source. Skipping extraction.",
                            artifact: new PostMeetingArtifactLink(
                                "action_item",
                                ArtifactIds: activeActionItems.Select(x => x.Id).ToList()),
                            cancellationToken: cancellationToken);
                    }

                    await EnqueuePersonalizedSummariesAsync(
                        organizationId,
                        meetingId,
                        "Personalized summary generation job enqueued after action extraction found current-source items.",
                        cancellationToken);

                    await EnqueueKnowledgeReindexAsync(
                        organizationId,
                        meetingId,
                        "Knowledge indexing job enqueued after action extraction found current-source items.",
                        cancellationToken);

                    _logger.LogInformation(
                        "Action items already match current transcript source for meeting {MeetingId}. Skipping.",
                        meetingId);
                    return;
                }

                var meeting = await _dbContext.Meetings
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(m => m.OrganizationId == organizationId && m.Id == meetingId)
                    .Select(m => new { m.RoomActivatedAtUtc, m.ScheduledStartUtc })
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new InvalidOperationException($"Meeting {meetingId} not found for action item extraction.");

                var referenceDateUtc = meeting.RoomActivatedAtUtc ?? meeting.ScheduledStartUtc;
                const string timezone = "UTC";

                var participants = await _dbContext.MeetingParticipants
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(p => p.OrganizationId == organizationId && p.MeetingId == meetingId)
                    .Select(p => new ParticipantCandidate(
                        p.Id,
                        p.UserId,
                        p.User.DisplayName,
                        p.User.UserName,
                        p.MeetingRole))
                    .ToListAsync(cancellationToken);

                var participantByUserId = participants.ToDictionary(x => x.UserId, x => x);

                var orgMembers = await _dbContext.UserOrgMemberships
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(m => m.OrganizationId == organizationId && m.IsEnabled)
                    .Select(m => new OrgMemberCandidate(
                        m.UserId,
                        m.User.DisplayName,
                        m.User.UserName,
                        m.OrgRole,
                        m.JobRole,
                        m.Context,
                        m.ContextStatus))
                    .ToListAsync(cancellationToken);

                var extractionContext = new ActionExtractionContext(
                    organizationId,
                    meetingId,
                    referenceDateUtc,
                    timezone,
                    participants,
                    orgMembers,
                    participantByUserId);

                var meetingContext = BuildMeetingContext(referenceDateUtc, timezone);
                var peopleContext = BuildPeopleContext(participants, orgMembers, participantByUserId);
                var prompt = _promptProvider.GetTaskExtractionPrompt(
                    transcript.FullText,
                    meetingContext,
                    peopleContext);

                var llmRequest = new LLMRequest
                {
                    Model = _model,
                    Messages = new List<ChatMessage>
                    {
                        new() { Role = "user", Content = prompt }
                    }
                };

                var response = await _llmService.CompleteAsync(llmRequest, cancellationToken);
                var result = ParseResponse(response, meetingId);

                var now = DateTime.UtcNow;
                var staleUnreviewedItems = activeActionItems
                    .Where(x => x.Status == ActionItemStatus.PendingReview
                                && x.SyncedAtUtc == null
                                && string.IsNullOrWhiteSpace(x.ExternalTaskId))
                    .ToList();

                foreach (var staleItem in staleUnreviewedItems)
                {
                    staleItem.SupersededAtUtc = now;
                    staleItem.SupersededByTranscriptHash = transcriptIdentity.TranscriptHash;
                    staleItem.SupersededReason = "source_transcript_replaced";
                    staleItem.UpdatedAtUtc = now;
                }

                var createdCount = 0;
                foreach (var item in result.Tasks)
                {
                    if (string.IsNullOrWhiteSpace(item.Task))
                    {
                        _logger.LogWarning("Skipping extracted action item with empty task for meeting {MeetingId}", meetingId);
                        continue;
                    }

                    var processed = ProcessTask(item, extractionContext);
                    var actionItem = new ActionItem
                    {
                        OrganizationId = organizationId,
                        MeetingId = meetingId,
                        Title = Truncate(item.Task.Trim(), MaxTitleLength),
                        Description = BuildDescription(item, processed.Assignee, processed.DueDate),
                        AssignedToParticipantId = processed.Assignee.ParticipantId,
                        AssignedToUserId = processed.Assignee.UserId,
                        DueDateUtc = processed.DueDate.DueDateUtc,
                        Status = ActionItemStatus.PendingReview,
                        SyncMissingAssigneeReason = processed.ReviewReason,
                        ExtractedAtUtc = now,
                        AiRawAssigneeText = processed.Audit.RawAssigneeText,
                        AiRawDeadlineText = processed.Audit.RawDeadlineText,
                        AiAssigneeResolutionReason = processed.Audit.AssigneeReason,
                        AiDeadlineResolutionReason = processed.Audit.DeadlineReason,
                        AiAssigneeConfidence = processed.Audit.AssigneeConfidence,
                        AiDeadlineConfidence = processed.Audit.DeadlineConfidence,
                        AiSuggestedAssignedToUserId = processed.Audit.SuggestedUserId,
                        AiSuggestedAssignedToParticipantId = processed.Audit.SuggestedParticipantId,
                        AiSuggestedDueDateUtc = processed.Audit.SuggestedDueDateUtc
                    };
                    TranscriptSourceIdentity.ApplySourceFields(actionItem, transcriptIdentity);

                    _dbContext.ActionItems.Add(actionItem);
                    createdCount++;
                }

                await _dbContext.SaveChangesAsync(cancellationToken);

                var currentActiveIds = await _dbContext.ActionItems
                    .IgnoreQueryFilters()
                    .Where(x => x.MeetingId == meetingId
                                && x.OrganizationId == organizationId
                                && x.SupersededAtUtc == null)
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);

                if (_postMeetingProcessingTracker is not null)
                {
                    var message = createdCount == 0
                        ? staleUnreviewedItems.Count > 0
                            ? $"Superseded {staleUnreviewedItems.Count} stale action item(s); no new action items extracted."
                            : "No action items extracted."
                        : $"Extracted {createdCount} action item(s) and superseded {staleUnreviewedItems.Count} stale item(s).";

                    await _postMeetingProcessingTracker.CompleteStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    message: message,
                        artifact: new PostMeetingArtifactLink("action_item", ArtifactIds: currentActiveIds),
                        cancellationToken: cancellationToken);
                }

                await EnqueuePersonalizedSummariesAsync(
                    organizationId,
                    meetingId,
                    "Personalized summary generation job enqueued after action extraction.",
                    cancellationToken);

                await EnqueueKnowledgeReindexAsync(
                    organizationId,
                    meetingId,
                    "Knowledge indexing job enqueued after action extraction.",
                    cancellationToken);

                _logger.LogInformation("Extracted {Count} action items for meeting {MeetingId}", createdCount, meetingId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
                    "action_extraction_failed",
                    ex.GetBaseException().Message,
                    _pipelineGenerationId,
                    _currentHangfireJobId,
                    cancellationToken: cancellationToken);
                }

                await EnqueuePersonalizedSummariesAsync(
                    organizationId,
                    meetingId,
                    "Personalized summary generation job enqueued after action extraction failed; summaries will omit unavailable action item context.",
                    cancellationToken);

                _logger.LogError(ex, "Failed to extract action items for meeting {MeetingId}", meetingId);
                throw;
            }
        }

        private static ProcessedTaskResult ProcessTask(ExtractedTaskDto item, ActionExtractionContext context)
        {
            var rawAssignee = NormalizeAuditText(NormalizeSuggestedAssignee(item.Assignee));
            var rawDeadline = NormalizeAuditText(item.Deadline);
            var assigneeConfidence = NormalizeConfidence(item.AssigneeConfidence);
            var deadlineConfidence = NormalizeConfidence(item.DeadlineConfidence);
            var assigneeReason = NormalizeAuditReason(item.AssigneeReason);
            var deadlineReason = NormalizeAuditReason(item.DeadlineReason);
            var audit = new AuditSnapshot(
                rawAssignee,
                rawDeadline,
                item.AssignedUserId,
                item.AssignedParticipantId,
                assigneeConfidence,
                assigneeReason,
                deadlineConfidence,
                deadlineReason,
                TryParseSuggestedDueDateUtc(item));

            var assignee = ResolveAssignee(
                item,
                rawAssignee,
                assigneeConfidence,
                assigneeReason,
                context);
            var dueDate = ResolveDueDate(
                item,
                rawDeadline,
                deadlineConfidence,
                deadlineReason,
                context);
            var reviewReason = ActionItemReviewReasons.From(
                assignee.RequiresReview ? ActionItemReviewReasons.NeedsAssignee : string.Empty,
                dueDate.InvalidDueDate ? ActionItemReviewReasons.InvalidDueDate : string.Empty);

            return new ProcessedTaskResult(assignee, dueDate, audit, reviewReason);
        }

        private static AssigneeResolution ResolveAssignee(
            ExtractedTaskDto item,
            string? rawAssignee,
            decimal? assigneeConfidence,
            string? assigneeReason,
            ActionExtractionContext context)
        {
            var hasStructuredSuggestion = item.AssignedUserId.HasValue
                || item.AssignedParticipantId.HasValue
                || assigneeConfidence.HasValue;

            if (hasStructuredSuggestion
                && assigneeConfidence.GetValueOrDefault() >= MinConfidenceForAutoAccept)
            {
                var validated = ValidateSuggestedAssignee(
                    item.AssignedUserId,
                    item.AssignedParticipantId,
                    context);
                if (validated is not null)
                {
                    return AssigneeResolution.Matched(
                        rawAssignee,
                        validated.Value.ParticipantId,
                        validated.Value.UserId,
                        assigneeReason);
                }
            }

            if (!string.IsNullOrWhiteSpace(rawAssignee))
            {
                var textMatch = ResolveAssigneeByText(rawAssignee, context.Participants);
                if (!textMatch.RequiresReview)
                {
                    return textMatch with { Reason = assigneeReason ?? textMatch.Reason };
                }

                return AssigneeResolution.NeedsReview(rawAssignee, assigneeReason);
            }

            return AssigneeResolution.NeedsReview(rawAssignee, assigneeReason);
        }

        private static (Guid? ParticipantId, Guid UserId)? ValidateSuggestedAssignee(
            Guid? suggestedUserId,
            Guid? suggestedParticipantId,
            ActionExtractionContext context)
        {
            var enabledUserIds = context.OrgMembers.Select(x => x.UserId).ToHashSet();
            var participantsById = context.Participants.ToDictionary(x => x.ParticipantId);
            var participantsByUserId = context.ParticipantByUserId;

            Guid? acceptedUserId = null;
            Guid? acceptedParticipantId = null;

            if (suggestedUserId.HasValue)
            {
                if (!enabledUserIds.Contains(suggestedUserId.Value))
                {
                    return null;
                }

                acceptedUserId = suggestedUserId.Value;
            }

            if (suggestedParticipantId.HasValue)
            {
                if (!participantsById.TryGetValue(suggestedParticipantId.Value, out var participant))
                {
                    return null;
                }

                if (acceptedUserId.HasValue && acceptedUserId.Value != participant.UserId)
                {
                    return null;
                }

                acceptedParticipantId = participant.ParticipantId;
                acceptedUserId ??= participant.UserId;
            }

            if (acceptedParticipantId.HasValue && !acceptedUserId.HasValue)
            {
                acceptedUserId = participantsById[acceptedParticipantId.Value].UserId;
            }

            if (!acceptedUserId.HasValue)
            {
                return null;
            }

            if (!enabledUserIds.Contains(acceptedUserId.Value))
            {
                return null;
            }

            if (!acceptedParticipantId.HasValue
                && participantsByUserId.TryGetValue(acceptedUserId.Value, out var linkedParticipant))
            {
                acceptedParticipantId = linkedParticipant.ParticipantId;
            }

            return (acceptedParticipantId, acceptedUserId.Value);
        }

        private static AssigneeResolution ResolveAssigneeByText(
            string? rawAssignee,
            IReadOnlyList<ParticipantCandidate> participants)
        {
            var cleanedAssignee = NormalizeSuggestedAssignee(rawAssignee);
            if (string.IsNullOrWhiteSpace(cleanedAssignee))
            {
                return AssigneeResolution.NeedsReview(rawAssignee);
            }

            var matches = participants
                .Where(p => p.Matches(cleanedAssignee))
                .ToList();

            return matches.Count == 1
                ? AssigneeResolution.Matched(rawAssignee, matches[0].ParticipantId, matches[0].UserId)
                : AssigneeResolution.NeedsReview(rawAssignee);
        }

        private static DeadlineResolution ResolveDueDate(
            ExtractedTaskDto item,
            string? rawDeadline,
            decimal? deadlineConfidence,
            string? deadlineReason,
            ActionExtractionContext context)
        {
            var hasStructuredSuggestion = !string.IsNullOrWhiteSpace(item.DeadlineUtc)
                || !string.IsNullOrWhiteSpace(item.DeadlineDate)
                || deadlineConfidence.HasValue;

            if (hasStructuredSuggestion)
            {
                if (deadlineConfidence.GetValueOrDefault() >= MinConfidenceForAutoAccept)
                {
                    var structured = TryParseStructuredDueDate(item);
                    if (structured.DueDateUtc.HasValue)
                    {
                        return DeadlineResolution.Accepted(rawDeadline, structured.DueDateUtc, deadlineReason);
                    }
                }

                if (!string.IsNullOrWhiteSpace(rawDeadline))
                {
                    return DeadlineResolution.Invalid(rawDeadline, deadlineReason);
                }

                return DeadlineResolution.Empty(deadlineReason);
            }

            if (!string.IsNullOrWhiteSpace(rawDeadline))
            {
                var dueDateUtc = ParseDueDateUtc(rawDeadline, out var invalidDueDate);
                if (dueDateUtc.HasValue)
                {
                    return DeadlineResolution.Accepted(rawDeadline, dueDateUtc, deadlineReason);
                }

                return invalidDueDate
                    ? DeadlineResolution.Invalid(rawDeadline, deadlineReason)
                    : DeadlineResolution.Empty(deadlineReason);
            }

            return DeadlineResolution.Empty(deadlineReason);
        }

        private static (DateTime? DueDateUtc, bool InvalidDueDate) TryParseStructuredDueDate(ExtractedTaskDto item)
        {
            if (!string.IsNullOrWhiteSpace(item.DeadlineUtc)
                && DateTimeOffset.TryParse(
                    item.DeadlineUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var deadlineOffset))
            {
                return (deadlineOffset.UtcDateTime, false);
            }

            if (!string.IsNullOrWhiteSpace(item.DeadlineDate)
                && DateOnly.TryParseExact(
                    item.DeadlineDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var deadlineDate))
            {
                return (deadlineDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), false);
            }

            return (null, true);
        }

        private static DateTime? TryParseSuggestedDueDateUtc(ExtractedTaskDto item)
        {
            var structured = TryParseStructuredDueDate(item);
            return structured.DueDateUtc;
        }

        private static string BuildMeetingContext(DateTime referenceDateUtc, string timezone)
            => $"""
                - Meeting reference date (UTC): {referenceDateUtc:yyyy-MM-dd}
                - Meeting reference datetime (UTC): {referenceDateUtc:O}
                - Timezone for deadline normalization: {timezone}
                """;

        private static string BuildPeopleContext(
            IReadOnlyList<ParticipantCandidate> participants,
            IReadOnlyList<OrgMemberCandidate> orgMembers,
            IReadOnlyDictionary<Guid, ParticipantCandidate> participantByUserId)
        {
            var lines = new List<string> { "Meeting participants:" };
            foreach (var participant in participants)
            {
                lines.Add(
                    $"- participant_id={participant.ParticipantId}; user_id={participant.UserId}; display_name={participant.DisplayName}; username={participant.UserName}; meeting_role={participant.MeetingRole}");
            }

            lines.Add("Organization members:");
            foreach (var member in orgMembers)
            {
                var participantId = participantByUserId.TryGetValue(member.UserId, out var linkedParticipant)
                    ? linkedParticipant.ParticipantId.ToString()
                    : "null";
                lines.Add(
                    $"- user_id={member.UserId}; participant_id={participantId}; display_name={member.DisplayName}; username={member.UserName}; org_role={member.OrgRole}; job_role={member.JobRole}; context={member.Context}; context_status={member.ContextStatus}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private async Task EnqueuePersonalizedSummariesAsync(
            Guid organizationId,
            Guid meetingId,
            string message,
            CancellationToken cancellationToken)
        {
            if (_pipelineGenerationId.HasValue)
            {
                var hasActivePersonalizedSummaryStepInGeneration = await _dbContext.PostMeetingProcessingSteps
                    .IgnoreQueryFilters()
                    .AnyAsync(
                        x => x.OrganizationId == organizationId
                             && x.MeetingId == meetingId
                             && x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
                             && (x.Status == PostMeetingProcessingStatus.Pending
                                 || x.Status == PostMeetingProcessingStatus.InProgress)
                             && _dbContext.PostMeetingProcessingRuns.Any(run =>
                                 run.Id == x.RunId
                                 && run.PipelineGenerationId == _pipelineGenerationId.Value),
                        cancellationToken);

                if (hasActivePersonalizedSummaryStepInGeneration)
                {
                    _logger.LogInformation(
                        "Personalized summary generation already pending or in progress for pipeline generation {PipelineGenerationId} on meeting {MeetingId}. Skipping duplicate enqueue.",
                        _pipelineGenerationId,
                        meetingId);
                    return;
                }
            }

            var personalizedSummaryJobId = _backgroundJobClient?.Enqueue<GeneratePersonalizedMeetingSummariesJob>(
                job => job.RunAsync(meetingId, organizationId, _pipelineGenerationId, CancellationToken.None));

            if (personalizedSummaryJobId is null || _postMeetingProcessingTracker is null)
            {
                return;
            }

            await _postMeetingProcessingTracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.PersonalizedSummaryGeneration,
                    _pipelineGenerationId,
                    message: message,
                relatedHangfireJobId: personalizedSummaryJobId,
                cancellationToken: cancellationToken);
        }

        private async Task EnqueueKnowledgeReindexAsync(
            Guid organizationId,
            Guid meetingId,
            string message,
            CancellationToken cancellationToken)
        {
            var knowledgeJobId = _backgroundJobClient?.Enqueue<ReindexMeetingKnowledgeJob>(
                job => job.RunAsync(meetingId, organizationId, _pipelineGenerationId, CancellationToken.None));

            if (knowledgeJobId is null || _postMeetingProcessingTracker is null)
            {
                return;
            }

            await _postMeetingProcessingTracker.MarkStepPendingAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.KnowledgeIndexing,
                    _pipelineGenerationId,
                    message: message,
                relatedHangfireJobId: knowledgeJobId,
                cancellationToken: cancellationToken);
        }

        private ExtractedTasksDto ParseResponse(LLMResponse response, Guid meetingId)
        {
            var content = response.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("LLM returned no action item content for meeting {MeetingId}", meetingId);
                throw new InvalidOperationException("LLM returned no action item content.");
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                var tasksElement = root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (!root.TryGetProperty("tasks", out tasksElement))
                    {
                        throw new InvalidOperationException("LLM action item JSON must be an array or contain a tasks array.");
                    }
                }

                if (tasksElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("LLM action item JSON must be an array or contain a tasks array.");
                }

                var tasks = tasksElement.Deserialize<List<ExtractedTaskDto>>(JsonOptions)
                    ?? throw new InvalidOperationException("LLM action item JSON deserialized to null.");

                return new ExtractedTasksDto { Tasks = tasks };
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "LLM returned malformed action item JSON for meeting {MeetingId}", meetingId);
                throw new InvalidOperationException("LLM returned malformed action item JSON.", ex);
            }
        }

        private static string? NormalizeSuggestedAssignee(string? assignee)
        {
            if (string.IsNullOrWhiteSpace(assignee))
            {
                return null;
            }

            return assignee
                .Replace("(suggested)", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        private static string? NormalizeAuditText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= MaxAuditTextLength ? trimmed : trimmed[..MaxAuditTextLength];
        }

        private static string? NormalizeAuditReason(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= MaxAuditReasonLength ? trimmed : trimmed[..MaxAuditReasonLength];
        }

        private static decimal? NormalizeConfidence(decimal? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            return value.Value is >= 0m and <= 1m ? value : null;
        }

        private static DateTime? ParseDueDateUtc(string? dueDate, out bool invalidDueDate)
        {
            invalidDueDate = false;

            if (string.IsNullOrWhiteSpace(dueDate))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(
                    dueDate,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dateTimeOffset))
            {
                return dateTimeOffset.UtcDateTime;
            }

            invalidDueDate = true;
            return null;
        }

        private static string BuildDescription(
            ExtractedTaskDto item,
            AssigneeResolution assignee,
            DeadlineResolution dueDate)
        {
            var lines = new List<string> { item.Task.Trim() };

            if (!string.IsNullOrWhiteSpace(assignee.RawAssignee))
            {
                lines.Add($"AI assignee: {assignee.RawAssignee}");
            }

            if (!string.IsNullOrWhiteSpace(assignee.Reason))
            {
                lines.Add($"AI assignee reason: {assignee.Reason}");
            }

            if (!string.IsNullOrWhiteSpace(dueDate.RawDeadline))
            {
                var suffix = dueDate.InvalidDueDate ? " (could not parse to UTC)" : string.Empty;
                lines.Add($"AI due date: {dueDate.RawDeadline}{suffix}");
            }

            if (!string.IsNullOrWhiteSpace(dueDate.Reason))
            {
                lines.Add($"AI deadline reason: {dueDate.Reason}");
            }

            return Truncate(string.Join(Environment.NewLine, lines), MaxDescriptionLength);
        }

        private static string Truncate(string value, int maxLength)
        {
            return value.Length <= maxLength ? value : value[..maxLength];
        }

        private sealed class ExtractedTasksDto
        {
            [JsonPropertyName("tasks")]
            public List<ExtractedTaskDto> Tasks { get; set; } = null!;
        }

        private sealed class ExtractedTaskDto
        {
            [JsonIgnore]
            public string? Assignee => ResponsiblePerson ?? LegacyAssignee;

            [JsonPropertyName("task")]
            public string Task { get; set; } = string.Empty;

            [JsonIgnore]
            public string? Deadline => DeadlineText ?? LegacyDueDate;

            [JsonPropertyName("responsible_person")]
            public string? ResponsiblePerson { get; set; }

            [JsonPropertyName("deadline")]
            public string? DeadlineText { get; set; }

            [JsonPropertyName("assignee")]
            public string? LegacyAssignee { get; set; }

            [JsonPropertyName("due_date")]
            public string? LegacyDueDate { get; set; }

            [JsonPropertyName("assigned_user_id")]
            public Guid? AssignedUserId { get; set; }

            [JsonPropertyName("assigned_participant_id")]
            public Guid? AssignedParticipantId { get; set; }

            [JsonPropertyName("assignee_confidence")]
            public decimal? AssigneeConfidence { get; set; }

            [JsonPropertyName("assignee_reason")]
            public string? AssigneeReason { get; set; }

            [JsonPropertyName("deadline_date")]
            public string? DeadlineDate { get; set; }

            [JsonPropertyName("deadline_utc")]
            public string? DeadlineUtc { get; set; }

            [JsonPropertyName("deadline_confidence")]
            public decimal? DeadlineConfidence { get; set; }

            [JsonPropertyName("deadline_reason")]
            public string? DeadlineReason { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }
        }

        private sealed record ParticipantCandidate(
            Guid ParticipantId,
            Guid UserId,
            string? DisplayName,
            string? UserName,
            MeetingRole MeetingRole)
        {
            public bool Matches(string assignee)
            {
                var normalizedAssignee = NormalizeForMatch(assignee);
                var candidates = new[]
                    {
                        DisplayName,
                        UserName,
                        UserName?.Split('@', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    }
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                    .Select(candidate => NormalizeForMatch(candidate!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (candidates.Any(candidate => string.Equals(candidate, normalizedAssignee, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                return normalizedAssignee.Length >= 3
                    && candidates.Any(candidate => candidate.Contains(normalizedAssignee, StringComparison.OrdinalIgnoreCase));
            }

            private static string NormalizeForMatch(string value)
                => string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private sealed record OrgMemberCandidate(
            Guid UserId,
            string? DisplayName,
            string? UserName,
            OrganizationRole OrgRole,
            string? JobRole,
            string? Context,
            ContextStatus? ContextStatus);

        private sealed record ActionExtractionContext(
            Guid OrganizationId,
            Guid MeetingId,
            DateTime ReferenceDateUtc,
            string Timezone,
            IReadOnlyList<ParticipantCandidate> Participants,
            IReadOnlyList<OrgMemberCandidate> OrgMembers,
            IReadOnlyDictionary<Guid, ParticipantCandidate> ParticipantByUserId);

        private sealed record AssigneeResolution(
            string? RawAssignee,
            Guid? ParticipantId,
            Guid? UserId,
            bool RequiresReview,
            string? Reason = null)
        {
            public static AssigneeResolution Matched(
                string? rawAssignee,
                Guid? participantId,
                Guid userId,
                string? reason = null)
                => new(rawAssignee, participantId, userId, false, reason);

            public static AssigneeResolution NeedsReview(string? rawAssignee, string? reason = null)
                => new(rawAssignee, null, null, true, reason);
        }

        private sealed record DeadlineResolution(
            string? RawDeadline,
            DateTime? DueDateUtc,
            bool InvalidDueDate,
            string? Reason = null)
        {
            public static DeadlineResolution Accepted(string? rawDeadline, DateTime? dueDateUtc, string? reason = null)
                => new(rawDeadline, dueDateUtc, false, reason);

            public static DeadlineResolution Invalid(string? rawDeadline, string? reason = null)
                => new(rawDeadline, null, !string.IsNullOrWhiteSpace(rawDeadline), reason);

            public static DeadlineResolution Empty(string? reason = null)
                => new(null, null, false, reason);
        }

        private sealed record AuditSnapshot(
            string? RawAssigneeText,
            string? RawDeadlineText,
            Guid? SuggestedUserId,
            Guid? SuggestedParticipantId,
            decimal? AssigneeConfidence,
            string? AssigneeReason,
            decimal? DeadlineConfidence,
            string? DeadlineReason,
            DateTime? SuggestedDueDateUtc);

        private sealed record ProcessedTaskResult(
            AssigneeResolution Assignee,
            DeadlineResolution DueDate,
            AuditSnapshot Audit,
            string? ReviewReason);
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
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

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ApplicationDbContext _dbContext;
        private readonly ILLMService _llmService;
        private readonly IPromptProvider _promptProvider;
        private readonly ILogger<ExtractActionItemsJob> _logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker;
        private readonly IBackgroundJobClient? _backgroundJobClient;
        private readonly string _model;

        public ExtractActionItemsJob(
            ApplicationDbContext dbContext,
            ILLMService llmService,
            IPromptProvider promptProvider,
            IOptions<OpenAiCompatibleOptions> options,
            ILogger<ExtractActionItemsJob> logger,
            IPostMeetingProcessingTracker? postMeetingProcessingTracker = null,
            IBackgroundJobClient? backgroundJobClient = null)
        {
            _dbContext = dbContext;
            _llmService = llmService;
            _promptProvider = promptProvider;
            _logger = logger;
            _postMeetingProcessingTracker = postMeetingProcessingTracker;
            _backgroundJobClient = backgroundJobClient;
            _model = options.Value.Llm.Model;
        }

        [Hangfire.AutomaticRetry(Attempts = 3)]
        public async Task RunAsync(Guid meetingId, Guid organizationId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting action item extraction for meeting {MeetingId}", meetingId);
            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.StartStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.ActionExtraction,
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
                            message: MeetingTranscriptCompletenessGuard.BuildIncompleteMessage(transcript),
                            cancellationToken: cancellationToken);

                        await _postMeetingProcessingTracker.RecordEventAsync(
                            organizationId,
                            meetingId,
                            PostMeetingProcessingEventType.Info,
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

                var existingCount = await _dbContext.ActionItems
                    .IgnoreQueryFilters()
                    .CountAsync(x => x.OrganizationId == organizationId && x.MeetingId == meetingId, cancellationToken);

                if (existingCount > 0)
                {
                    if (_postMeetingProcessingTracker is not null)
                    {
                        var existingIds = await _dbContext.ActionItems
                            .IgnoreQueryFilters()
                            .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
                            .Select(x => x.Id)
                            .ToListAsync(cancellationToken);

                        await _postMeetingProcessingTracker.CompleteStepAsync(
                            organizationId,
                            meetingId,
                            PostMeetingProcessingStepType.ActionExtraction,
                            message: "Action items already exist for meeting. Skipping extraction.",
                            artifact: new PostMeetingArtifactLink("action_item", ArtifactIds: existingIds),
                            cancellationToken: cancellationToken);
                    }

                    await EnqueuePersonalizedSummariesAsync(
                        organizationId,
                        meetingId,
                        "Personalized summary generation job enqueued after action extraction found existing items.",
                        cancellationToken);

                    await EnqueueKnowledgeReindexAsync(
                        organizationId,
                        meetingId,
                        "Knowledge indexing job enqueued after action extraction found existing items.",
                        cancellationToken);

                    _logger.LogInformation("Action items already exist for meeting {MeetingId}. Skipping.", meetingId);
                    return;
                }

                var participants = await _dbContext.MeetingParticipants
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(p => p.OrganizationId == organizationId && p.MeetingId == meetingId)
                    .Select(p => new { p.Id, p.UserId, p.User.UserName, p.User.DisplayName })
                    .ToListAsync(cancellationToken);
                var participantCandidates = participants
                    .Select(p => new ParticipantCandidate(p.Id, p.UserId, p.DisplayName, p.UserName))
                    .ToList();

                var prompt = _promptProvider.GetTaskExtractionPrompt(transcript.FullText);

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

                if (result.Tasks.Count == 0)
                {
                    if (_postMeetingProcessingTracker is not null)
                    {
                        await _postMeetingProcessingTracker.CompleteStepAsync(
                            organizationId,
                            meetingId,
                            PostMeetingProcessingStepType.ActionExtraction,
                            message: "No action items extracted.",
                            artifact: new PostMeetingArtifactLink("action_item", ArtifactIds: Array.Empty<Guid>()),
                            cancellationToken: cancellationToken);
                    }

                    await EnqueuePersonalizedSummariesAsync(
                        organizationId,
                        meetingId,
                        "Personalized summary generation job enqueued after action extraction completed with no items.",
                        cancellationToken);

                    await EnqueueKnowledgeReindexAsync(
                        organizationId,
                        meetingId,
                        "Knowledge indexing job enqueued after action extraction completed with no items.",
                        cancellationToken);

                    _logger.LogInformation("No action items extracted for meeting {MeetingId}", meetingId);
                    return;
                }

                var createdCount = 0;
                foreach (var item in result.Tasks)
                {
                    if (string.IsNullOrWhiteSpace(item.Task))
                    {
                        _logger.LogWarning("Skipping extracted action item with empty task for meeting {MeetingId}", meetingId);
                        continue;
                    }

                    var assignee = ResolveAssignee(item.Assignee, participantCandidates);
                    var dueDateUtc = ParseDueDateUtc(item.DueDate, out var invalidDueDate);
                    var reviewReason = ActionItemReviewReasons.From(
                        assignee.RequiresReview ? ActionItemReviewReasons.NeedsAssignee : string.Empty,
                        invalidDueDate ? ActionItemReviewReasons.InvalidDueDate : string.Empty);

                    var actionItem = new ActionItem
                    {
                        OrganizationId = organizationId,
                        MeetingId = meetingId,
                        Title = Truncate(item.Task.Trim(), MaxTitleLength),
                        Description = BuildDescription(item, assignee, invalidDueDate),
                        AssignedToParticipantId = assignee.ParticipantId,
                        AssignedToUserId = assignee.UserId,
                        DueDateUtc = dueDateUtc,
                        Status = ActionItemStatus.PendingReview,
                        SyncMissingAssigneeReason = reviewReason,
                        ExtractedAtUtc = DateTime.UtcNow
                    };

                    _dbContext.ActionItems.Add(actionItem);
                    createdCount++;
                }

                if (createdCount == 0)
                {
                    if (_postMeetingProcessingTracker is not null)
                    {
                        await _postMeetingProcessingTracker.CompleteStepAsync(
                            organizationId,
                            meetingId,
                            PostMeetingProcessingStepType.ActionExtraction,
                            message: "No usable action items extracted.",
                            artifact: new PostMeetingArtifactLink("action_item", ArtifactIds: Array.Empty<Guid>()),
                            cancellationToken: cancellationToken);
                    }

                    await EnqueuePersonalizedSummariesAsync(
                        organizationId,
                        meetingId,
                        "Personalized summary generation job enqueued after action extraction completed with no usable items.",
                        cancellationToken);

                    await EnqueueKnowledgeReindexAsync(
                        organizationId,
                        meetingId,
                        "Knowledge indexing job enqueued after action extraction completed with no usable items.",
                        cancellationToken);

                    _logger.LogInformation("No usable action items extracted for meeting {MeetingId}", meetingId);
                    return;
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                if (_postMeetingProcessingTracker is not null)
                {
                    var createdIds = await _dbContext.ActionItems
                        .IgnoreQueryFilters()
                        .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
                        .Select(x => x.Id)
                        .ToListAsync(cancellationToken);

                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.ActionExtraction,
                        message: $"Extracted {createdCount} action item(s).",
                        artifact: new PostMeetingArtifactLink("action_item", ArtifactIds: createdIds),
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

        private async Task EnqueuePersonalizedSummariesAsync(
            Guid organizationId,
            Guid meetingId,
            string message,
            CancellationToken cancellationToken)
        {
            var hasActivePersonalizedSummaryStep = await _dbContext.PostMeetingProcessingSteps
                .IgnoreQueryFilters()
                .AnyAsync(
                    x => x.OrganizationId == organizationId
                         && x.MeetingId == meetingId
                         && x.StepType == PostMeetingProcessingStepType.PersonalizedSummaryGeneration
                         && (x.Status == PostMeetingProcessingStatus.Pending
                             || x.Status == PostMeetingProcessingStatus.InProgress
                             || x.Status == PostMeetingProcessingStatus.Completed),
                    cancellationToken);

            if (hasActivePersonalizedSummaryStep)
            {
                _logger.LogInformation(
                    "Personalized summary generation already pending, in progress, or completed for meeting {MeetingId}. Skipping duplicate enqueue.",
                    meetingId);
                return;
            }

            var personalizedSummaryJobId = _backgroundJobClient?.Enqueue<GeneratePersonalizedMeetingSummariesJob>(
                job => job.RunAsync(meetingId, organizationId, CancellationToken.None));

            if (personalizedSummaryJobId is null || _postMeetingProcessingTracker is null)
            {
                return;
            }

            await _postMeetingProcessingTracker.MarkStepPendingAsync(
                organizationId,
                meetingId,
                PostMeetingProcessingStepType.PersonalizedSummaryGeneration,
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
                job => job.RunAsync(meetingId, organizationId, CancellationToken.None));

            if (knowledgeJobId is null || _postMeetingProcessingTracker is null)
            {
                return;
            }

            await _postMeetingProcessingTracker.MarkStepPendingAsync(
                organizationId,
                meetingId,
                PostMeetingProcessingStepType.KnowledgeIndexing,
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

        private static AssigneeResolution ResolveAssignee(
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
            bool invalidDueDate)
        {
            var lines = new List<string> { item.Task.Trim() };

            if (!string.IsNullOrWhiteSpace(assignee.RawAssignee))
            {
                lines.Add($"AI assignee: {assignee.RawAssignee}");
            }

            if (!string.IsNullOrWhiteSpace(item.DueDate))
            {
                var suffix = invalidDueDate ? " (could not parse to UTC)" : string.Empty;
                lines.Add($"AI due date: {item.DueDate}{suffix}");
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
            public string? DueDate => Deadline ?? LegacyDueDate;

            [JsonPropertyName("responsible_person")]
            public string? ResponsiblePerson { get; set; }

            [JsonPropertyName("deadline")]
            public string? Deadline { get; set; }

            [JsonPropertyName("assignee")]
            public string? LegacyAssignee { get; set; }

            [JsonPropertyName("due_date")]
            public string? LegacyDueDate { get; set; }

            [JsonPropertyName("status")]
            public string? Status { get; set; }
        }

        private sealed record ParticipantCandidate(
            Guid ParticipantId,
            Guid UserId,
            string? DisplayName,
            string? UserName)
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

        private sealed record AssigneeResolution(
            string? RawAssignee,
            Guid? ParticipantId,
            Guid? UserId,
            bool RequiresReview)
        {
            public static AssigneeResolution Matched(string? rawAssignee, Guid participantId, Guid userId)
                => new(rawAssignee, participantId, userId, false);

            public static AssigneeResolution NeedsReview(string? rawAssignee)
                => new(rawAssignee, null, null, true);
        }
    }
}

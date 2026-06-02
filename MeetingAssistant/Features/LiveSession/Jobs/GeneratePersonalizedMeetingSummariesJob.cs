using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hangfire;
using MeetingAssistant.Features.ActionItems.Models.Entities;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.LiveSession.Jobs
{
    public class GeneratePersonalizedMeetingSummariesJob(
        ApplicationDbContext dbContext,
        ISummarizerService summarizerService,
        ILogger<GeneratePersonalizedMeetingSummariesJob> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null)
    {
        private const string ParticipantSpeakerRole = "participant";
        private const string AssistantDisplayName = "AI Assistant";
        private const string EligibilityReasonPersonalizationSignal = "personalization_signal";
        private const string EligibilityReasonParticipantSpoke = "participant_spoke";
        private const string EligibilityReasonParticipantMentioned = "participant_mentioned";
        private const string EligibilityReasonSkippedNoRelevance = "no_personalization_signal_or_transcript_relevance";

        private static readonly JsonSerializerOptions PersonalizationJsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly JsonSerializerOptions TranscriptSegmentJsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ISummarizerService _summarizerService = summarizerService;
        private readonly ILogger<GeneratePersonalizedMeetingSummariesJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;

        [AutomaticRetry(Attempts = 3)]
        [DisableConcurrentExecution(timeoutInSeconds: 3600)]
        public async Task RunAsync(
            Guid meetingId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            if (_postMeetingProcessingTracker is not null)
            {
                await _postMeetingProcessingTracker.StartStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.PersonalizedSummaryGeneration,
                    message: "Personalized summary generation started.",
                    cancellationToken: cancellationToken);
            }

            var transcript = await _dbContext.MeetingTranscripts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.MeetingId == meetingId && x.OrganizationId == organizationId, cancellationToken);

            if (transcript == null || string.IsNullOrWhiteSpace(transcript.FullText))
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.PersonalizedSummaryGeneration,
                        "transcript_unavailable",
                        "Personalized summary generation skipped because meeting transcript was unavailable.",
                        cancellationToken: cancellationToken);
                }

                _logger.LogInformation(
                    "Personalized summary generation skipped because meeting transcript was unavailable. MeetingId={MeetingId}",
                    meetingId);
                return;
            }

            var transcriptSegments = ResolveTranscriptSegments(transcript.SegmentsJson, transcript.FullText);

            var participants = await _dbContext.MeetingParticipants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.MeetingId == meetingId && x.OrganizationId == organizationId)
                .OrderBy(x => x.User.DisplayName)
                .ThenBy(x => x.UserId)
                .Select(x => new ParticipantTarget(
                    x.Id,
                    x.UserId,
                    x.User.DisplayName,
                    x.User.UserName,
                    x.User.Email))
                .ToListAsync(cancellationToken);

            if (participants.Count == 0)
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.PersonalizedSummaryGeneration,
                        message: "No meeting participants found for personalized summary generation.",
                        artifact: new PostMeetingArtifactLink("personalized_meeting_summary", ArtifactIds: Array.Empty<Guid>()),
                        cancellationToken: cancellationToken);
                }

                _logger.LogInformation(
                    "Personalized summary generation completed with no participants. MeetingId={MeetingId}",
                    meetingId);
                return;
            }

            var participantIds = participants.Select(x => x.MeetingParticipantId).ToHashSet();
            var userIds = participants.Select(x => x.UserId).ToHashSet();

            var memberships = await _dbContext.UserOrgMemberships
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && userIds.Contains(x.UserId) && x.IsEnabled)
                .Select(x => new MembershipContext(x.UserId, x.JobRole, x.Context))
                .ToListAsync(cancellationToken);
            var membershipsByUserId = memberships.ToDictionary(x => x.UserId);

            var actionItems = await _dbContext.ActionItems
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId
                            && x.MeetingId == meetingId
                            && ((x.AssignedToUserId.HasValue && userIds.Contains(x.AssignedToUserId.Value))
                                || (x.AssignedToParticipantId.HasValue && participantIds.Contains(x.AssignedToParticipantId.Value))))
                .Select(x => new AssignedActionItemContext(
                    x.Id,
                    x.Title,
                    x.Description,
                    x.AssignedToUserId,
                    x.AssignedToParticipantId,
                    x.DueDateUtc,
                    x.Status.ToString()))
                .ToListAsync(cancellationToken);

            var evaluatedSummaries = new List<EvaluatedPersonalizedSummary>();
            foreach (var participant in participants)
            {
                membershipsByUserId.TryGetValue(participant.UserId, out var membership);
                var participantActionItems = actionItems
                    .Where(x => x.AssignedToUserId == participant.UserId || x.AssignedToParticipantId == participant.MeetingParticipantId)
                    .GroupBy(x => x.Id)
                    .Select(x => x.First())
                    .OrderBy(x => x.Title)
                    .ToList();
                var targetDisplayName = ResolveDisplayName(participant);
                var personalization = BuildPersonalizationContext(targetDisplayName, membership, participantActionItems);
                var eligibility = EvaluateEligibility(participant, targetDisplayName, personalization, transcriptSegments);

                if (!eligibility.ShouldGenerate)
                {
                    evaluatedSummaries.Add(new EvaluatedPersonalizedSummary(
                        participant.MeetingParticipantId,
                        participant.UserId,
                        targetDisplayName,
                        PersonalizedMeetingSummaryStatus.Skipped,
                        null,
                        null,
                        eligibility.Reason,
                        eligibility.ContextJson));
                    continue;
                }

                SummaryResult summaryResult;
                try
                {
                    summaryResult = await _summarizerService.SummarizePersonalizedAsync(
                        transcript.FullText,
                        targetDisplayName,
                        personalization.PromptContext,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    if (_postMeetingProcessingTracker is not null)
                    {
                        await _postMeetingProcessingTracker.FailStepAsync(
                            organizationId,
                            meetingId,
                            PostMeetingProcessingStepType.PersonalizedSummaryGeneration,
                            "personalized_summary_failed",
                            ex.GetBaseException().Message,
                            cancellationToken: cancellationToken);
                    }

                    throw;
                }

                evaluatedSummaries.Add(new EvaluatedPersonalizedSummary(
                    participant.MeetingParticipantId,
                    participant.UserId,
                    targetDisplayName,
                    PersonalizedMeetingSummaryStatus.Generated,
                    summaryResult,
                    personalization.ContextJson,
                    eligibility.Reason,
                    eligibility.ContextJson));
            }

            var evaluatedAtUtc = DateTime.UtcNow;
            foreach (var evaluated in evaluatedSummaries)
            {
                var summary = await _dbContext.PersonalizedMeetingSummaries
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.MeetingId == meetingId && x.OrganizationId == organizationId && x.UserId == evaluated.UserId,
                        cancellationToken);

                if (summary == null)
                {
                    summary = new PersonalizedMeetingSummary
                    {
                        MeetingId = meetingId,
                        OrganizationId = organizationId,
                        MeetingParticipantId = evaluated.MeetingParticipantId,
                        UserId = evaluated.UserId
                    };

                    _dbContext.PersonalizedMeetingSummaries.Add(summary);
                }
                else
                {
                    summary.MeetingParticipantId = evaluated.MeetingParticipantId;
                }

                summary.Status = evaluated.Status;
                summary.TargetDisplayName = evaluated.TargetDisplayName;
                summary.EligibilityReason = evaluated.EligibilityReason;
                summary.EligibilityContextJson = evaluated.EligibilityContextJson;

                if (evaluated.Status == PersonalizedMeetingSummaryStatus.Generated)
                {
                    var summaryResult = evaluated.SummaryResult
                        ?? throw new InvalidOperationException("Generated personalized summary evaluation is missing a summary result.");

                    summary.SummaryText = summaryResult.SummaryText;
                    summary.LlmModel = summaryResult.Model;
                    summary.PromptTokens = summaryResult.PromptTokens;
                    summary.CompletionTokens = summaryResult.CompletionTokens;
                    summary.GeneratedAtUtc = evaluatedAtUtc;
                    summary.PromptName = summaryResult.PromptName;
                    summary.PromptVersion = summaryResult.PromptVersion;
                    summary.PersonalizationContextJson = evaluated.PersonalizationContextJson;
                }
                else
                {
                    summary.SummaryText = null;
                    summary.LlmModel = null;
                    summary.PromptTokens = null;
                    summary.CompletionTokens = null;
                    summary.GeneratedAtUtc = null;
                    summary.PromptName = null;
                    summary.PromptVersion = null;
                    summary.PersonalizationContextJson = null;
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var generatedCount = evaluatedSummaries.Count(x => x.Status == PersonalizedMeetingSummaryStatus.Generated);
            var skippedCount = evaluatedSummaries.Count - generatedCount;
            if (_postMeetingProcessingTracker is not null)
            {
                var summaryIds = await _dbContext.PersonalizedMeetingSummaries
                    .IgnoreQueryFilters()
                    .Where(x => x.OrganizationId == organizationId && x.MeetingId == meetingId)
                    .OrderBy(x => x.Id)
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);

                await _postMeetingProcessingTracker.CompleteStepAsync(
                    organizationId,
                    meetingId,
                    PostMeetingProcessingStepType.PersonalizedSummaryGeneration,
                    message: $"Generated {generatedCount} personalized meeting summary artifact(s); skipped {skippedCount} participant(s) without personalized relevance.",
                    artifact: new PostMeetingArtifactLink("personalized_meeting_summary", ArtifactIds: summaryIds),
                    cancellationToken: cancellationToken);
            }

            _logger.LogInformation(
                "Generated {GeneratedCount} personalized meeting summaries and skipped {SkippedCount} participants for meeting {MeetingId}",
                generatedCount,
                skippedCount,
                meetingId);
        }

        private static PersonalizationContext BuildPersonalizationContext(
            string targetDisplayName,
            MembershipContext? membership,
            IReadOnlyList<AssignedActionItemContext> actionItems)
        {
            var promptLines = new List<string>();
            var context = new Dictionary<string, object?>();
            var hasJobRole = false;
            var hasOrganizationContext = false;

            if (!string.IsNullOrWhiteSpace(membership?.JobRole))
            {
                promptLines.Add($"- Job role: {membership.JobRole.Trim()}");
                context["jobRole"] = membership.JobRole.Trim();
                hasJobRole = true;
            }

            if (!string.IsNullOrWhiteSpace(membership?.Context))
            {
                promptLines.Add($"- Context: {membership.Context.Trim()}");
                context["context"] = membership.Context.Trim();
                hasOrganizationContext = true;
            }

            if (actionItems.Count > 0)
            {
                promptLines.Add("- Assigned action items:");
                foreach (var actionItem in actionItems)
                {
                    var details = BuildActionItemDetails(actionItem);
                    promptLines.Add($"  - {actionItem.Title}{details}");
                }

                context["assignedActionItems"] = actionItems.Select(x => new
                {
                    id = x.Id,
                    title = x.Title,
                    description = string.IsNullOrWhiteSpace(x.Description) ? null : x.Description,
                    dueDateUtc = x.DueDateUtc,
                    status = x.Status
                }).ToArray();
            }

            if (promptLines.Count == 0)
            {
                return new PersonalizationContext(
                    null,
                    null,
                    hasJobRole,
                    hasOrganizationContext,
                    actionItems.Count);
            }

            var promptContext = $"Personalization context for {targetDisplayName}:{Environment.NewLine}{string.Join(Environment.NewLine, promptLines)}";
            var contextJson = JsonSerializer.Serialize(context, PersonalizationJsonOptions);
            return new PersonalizationContext(
                promptContext,
                contextJson,
                hasJobRole,
                hasOrganizationContext,
                actionItems.Count);
        }

        private static EligibilityDecision EvaluateEligibility(
            ParticipantTarget participant,
            string targetDisplayName,
            PersonalizationContext personalization,
            IReadOnlyList<TranscriptSegmentContext> transcriptSegments)
        {
            var spokenSegmentCount = transcriptSegments.Count(segment => IsParticipantSegment(segment, participant, targetDisplayName));
            var mentionTerms = BuildMentionTerms(participant, targetDisplayName);
            var matchedMentionTerms = transcriptSegments
                .Where(segment => !IsParticipantSegment(segment, participant, targetDisplayName))
                .SelectMany(segment => mentionTerms.Where(term => ContainsMention(segment.Text, term)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var reasons = new List<string>();
            if (personalization.HasStrongSignal)
            {
                reasons.Add(EligibilityReasonPersonalizationSignal);
            }

            if (spokenSegmentCount > 0)
            {
                reasons.Add(EligibilityReasonParticipantSpoke);
            }

            if (matchedMentionTerms.Length > 0)
            {
                reasons.Add(EligibilityReasonParticipantMentioned);
            }

            var shouldGenerate = reasons.Count > 0;
            var reason = shouldGenerate ? reasons[0] : EligibilityReasonSkippedNoRelevance;
            var contextJson = JsonSerializer.Serialize(new
            {
                decision = shouldGenerate ? "generate" : "skip",
                reason,
                reasons,
                targetDisplayName,
                hasJobRole = personalization.HasJobRole,
                hasOrganizationContext = personalization.HasOrganizationContext,
                assignedActionItemCount = personalization.AssignedActionItemCount,
                hasStrongPersonalizationSignal = personalization.HasStrongSignal,
                participantSpoke = spokenSegmentCount > 0,
                spokenSegmentCount,
                participantMentioned = matchedMentionTerms.Length > 0,
                matchedMentionTerms,
                transcriptSegmentCount = transcriptSegments.Count
            }, PersonalizationJsonOptions);

            return new EligibilityDecision(shouldGenerate, reason, contextJson);
        }

        private static IReadOnlyList<TranscriptSegmentContext> ResolveTranscriptSegments(string? segmentsJson, string fullText)
        {
            if (!string.IsNullOrWhiteSpace(segmentsJson))
            {
                try
                {
                    var persistedSegments = JsonSerializer.Deserialize<List<TranscriptSegmentContext>>(segmentsJson, TranscriptSegmentJsonOptions);
                    var usableSegments = persistedSegments?
                        .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
                        .ToArray();

                    if (usableSegments is { Length: > 0 })
                    {
                        return usableSegments;
                    }
                }
                catch (JsonException)
                {
                    // Fall back to parsing the line-oriented full transcript for older or malformed segment payloads.
                }
            }

            return ParseTranscriptLines(fullText);
        }

        private static IReadOnlyList<TranscriptSegmentContext> ParseTranscriptLines(string fullText)
        {
            var segments = new List<TranscriptSegmentContext>();
            foreach (var line in fullText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var match = Regex.Match(
                    line,
                    @"^\[[^\]\s]+\s+(?<speaker>[^\]]+)\]\s*(?<text>.*)$",
                    RegexOptions.CultureInvariant);

                if (!match.Success)
                {
                    segments.Add(new TranscriptSegmentContext
                    {
                        SpeakerRole = null,
                        SpeakerDisplayName = null,
                        Text = line
                    });
                    continue;
                }

                var speakerDisplayName = match.Groups["speaker"].Value.Trim();
                segments.Add(new TranscriptSegmentContext
                {
                    SpeakerRole = string.Equals(speakerDisplayName, AssistantDisplayName, StringComparison.OrdinalIgnoreCase)
                        ? "assistant"
                        : ParticipantSpeakerRole,
                    SpeakerDisplayName = speakerDisplayName,
                    Text = match.Groups["text"].Value.Trim()
                });
            }

            return segments;
        }

        private static bool IsParticipantSegment(
            TranscriptSegmentContext segment,
            ParticipantTarget participant,
            string targetDisplayName)
        {
            if (segment.ParticipantUserId == participant.UserId)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(segment.SpeakerDisplayName))
            {
                return false;
            }

            return string.Equals(segment.SpeakerDisplayName.Trim(), targetDisplayName, StringComparison.OrdinalIgnoreCase)
                   && (string.IsNullOrWhiteSpace(segment.SpeakerRole)
                       || string.Equals(segment.SpeakerRole, ParticipantSpeakerRole, StringComparison.OrdinalIgnoreCase));
        }

        private static IReadOnlyList<string> BuildMentionTerms(ParticipantTarget participant, string targetDisplayName)
        {
            var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddMentionTerm(terms, targetDisplayName);
            AddMentionTerm(terms, participant.DisplayName);
            AddMentionTerm(terms, participant.UserName);
            AddMentionTerm(terms, participant.Email);
            AddMentionTerm(terms, ExtractEmailLocalPart(participant.UserName));
            AddMentionTerm(terms, ExtractEmailLocalPart(participant.Email));
            return terms.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void AddMentionTerm(HashSet<string> terms, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            if (trimmed.Length < 3)
            {
                return;
            }

            terms.Add(trimmed);
        }

        private static string? ExtractEmailLocalPart(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var atIndex = value.IndexOf('@', StringComparison.Ordinal);
            return atIndex > 0 ? value[..atIndex] : null;
        }

        private static bool ContainsMention(string? text, string term)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
            {
                return false;
            }

            return Regex.IsMatch(
                text,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string BuildActionItemDetails(AssignedActionItemContext actionItem)
        {
            var details = new List<string>();
            if (actionItem.DueDateUtc.HasValue)
            {
                details.Add($"due {actionItem.DueDateUtc.Value:O}");
            }

            if (!string.IsNullOrWhiteSpace(actionItem.Status))
            {
                details.Add($"status {actionItem.Status}");
            }

            return details.Count == 0 ? string.Empty : $" ({string.Join(", ", details)})";
        }

        private static string ResolveDisplayName(ParticipantTarget participant)
        {
            if (!string.IsNullOrWhiteSpace(participant.DisplayName))
            {
                return participant.DisplayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(participant.UserName))
            {
                return participant.UserName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(participant.Email))
            {
                return participant.Email.Trim();
            }

            return participant.UserId.ToString();
        }

        private sealed record ParticipantTarget(
            Guid MeetingParticipantId,
            Guid UserId,
            string? DisplayName,
            string? UserName,
            string? Email);

        private sealed record MembershipContext(Guid UserId, string? JobRole, string? Context);

        private sealed record AssignedActionItemContext(
            Guid Id,
            string Title,
            string? Description,
            Guid? AssignedToUserId,
            Guid? AssignedToParticipantId,
            DateTime? DueDateUtc,
            string Status);

        private sealed record PersonalizationContext(
            string? PromptContext,
            string? ContextJson,
            bool HasJobRole,
            bool HasOrganizationContext,
            int AssignedActionItemCount)
        {
            public bool HasStrongSignal => HasJobRole || HasOrganizationContext || AssignedActionItemCount > 0;
        }

        private sealed record EligibilityDecision(
            bool ShouldGenerate,
            string Reason,
            string ContextJson);

        private sealed class TranscriptSegmentContext
        {
            public string? SpeakerRole { get; init; }
            public Guid? ParticipantUserId { get; init; }
            public string? SpeakerDisplayName { get; init; }
            public string? Text { get; init; }
        }

        private sealed record EvaluatedPersonalizedSummary(
            Guid MeetingParticipantId,
            Guid UserId,
            string TargetDisplayName,
            PersonalizedMeetingSummaryStatus Status,
            SummaryResult? SummaryResult,
            string? PersonalizationContextJson,
            string EligibilityReason,
            string EligibilityContextJson);
    }
}

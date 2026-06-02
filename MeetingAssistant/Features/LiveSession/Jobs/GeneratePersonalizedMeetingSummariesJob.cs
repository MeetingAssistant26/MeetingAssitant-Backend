using System.Text.Json;
using System.Text.Json.Serialization;
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
        private static readonly JsonSerializerOptions PersonalizationJsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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

            var generatedSummaries = new List<GeneratedPersonalizedSummary>();
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

                generatedSummaries.Add(new GeneratedPersonalizedSummary(
                    participant.MeetingParticipantId,
                    participant.UserId,
                    targetDisplayName,
                    summaryResult,
                    personalization.ContextJson));
            }

            foreach (var generated in generatedSummaries)
            {
                var summary = await _dbContext.PersonalizedMeetingSummaries
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.MeetingId == meetingId && x.OrganizationId == organizationId && x.UserId == generated.UserId,
                        cancellationToken);

                if (summary == null)
                {
                    summary = new PersonalizedMeetingSummary
                    {
                        MeetingId = meetingId,
                        OrganizationId = organizationId,
                        MeetingParticipantId = generated.MeetingParticipantId,
                        UserId = generated.UserId
                    };

                    _dbContext.PersonalizedMeetingSummaries.Add(summary);
                }
                else
                {
                    summary.MeetingParticipantId = generated.MeetingParticipantId;
                }

                summary.SummaryText = generated.SummaryResult.SummaryText;
                summary.LlmModel = generated.SummaryResult.Model;
                summary.PromptTokens = generated.SummaryResult.PromptTokens;
                summary.CompletionTokens = generated.SummaryResult.CompletionTokens;
                summary.GeneratedAtUtc = DateTime.UtcNow;
                summary.TargetDisplayName = generated.TargetDisplayName;
                summary.PromptName = generated.SummaryResult.PromptName;
                summary.PromptVersion = generated.SummaryResult.PromptVersion;
                summary.PersonalizationContextJson = generated.PersonalizationContextJson;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

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
                    message: $"Generated {generatedSummaries.Count} personalized meeting summary artifact(s).",
                    artifact: new PostMeetingArtifactLink("personalized_meeting_summary", ArtifactIds: summaryIds),
                    cancellationToken: cancellationToken);
            }

            _logger.LogInformation(
                "Generated {Count} personalized meeting summaries for meeting {MeetingId}",
                generatedSummaries.Count,
                meetingId);
        }

        private static PersonalizationContext BuildPersonalizationContext(
            string targetDisplayName,
            MembershipContext? membership,
            IReadOnlyList<AssignedActionItemContext> actionItems)
        {
            var promptLines = new List<string>();
            var context = new Dictionary<string, object?>();

            if (!string.IsNullOrWhiteSpace(membership?.JobRole))
            {
                promptLines.Add($"- Job role: {membership.JobRole.Trim()}");
                context["jobRole"] = membership.JobRole.Trim();
            }

            if (!string.IsNullOrWhiteSpace(membership?.Context))
            {
                promptLines.Add($"- Context: {membership.Context.Trim()}");
                context["context"] = membership.Context.Trim();
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
                return new PersonalizationContext(null, null);
            }

            var promptContext = $"Personalization context for {targetDisplayName}:{Environment.NewLine}{string.Join(Environment.NewLine, promptLines)}";
            var contextJson = JsonSerializer.Serialize(context, PersonalizationJsonOptions);
            return new PersonalizationContext(promptContext, contextJson);
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

        private sealed record PersonalizationContext(string? PromptContext, string? ContextJson);

        private sealed record GeneratedPersonalizedSummary(
            Guid MeetingParticipantId,
            Guid UserId,
            string TargetDisplayName,
            SummaryResult SummaryResult,
            string? PersonalizationContextJson);
    }
}

using Hangfire;
using MeetingAssistant.Features.LiveSession.Models.PostProcessing;
using MeetingAssistant.Features.LiveSession.Services.PostProcessing;
using MeetingAssistant.Features.Meetings.Services.TagSuggestions;

namespace MeetingAssistant.Features.Meetings.Jobs
{
    public sealed class SuggestMeetingTagsJob(
        IMeetingTagSuggestionService meetingTagSuggestionService,
        ILogger<SuggestMeetingTagsJob> logger,
        IPostMeetingProcessingTracker? postMeetingProcessingTracker = null)
    {
        private readonly IMeetingTagSuggestionService _meetingTagSuggestionService = meetingTagSuggestionService;
        private readonly ILogger<SuggestMeetingTagsJob> _logger = logger;
        private readonly IPostMeetingProcessingTracker? _postMeetingProcessingTracker = postMeetingProcessingTracker;

        [Hangfire.AutomaticRetry(Attempts = 3)]
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
                    PostMeetingProcessingStepType.TagSuggestion,
                    message: "Meeting tag suggestion started.",
                    cancellationToken: cancellationToken);
            }

            try
            {
                var result = await _meetingTagSuggestionService.SuggestTagsAsync(
                    organizationId,
                    meetingId,
                    cancellationToken);

                if (_postMeetingProcessingTracker is not null)
                {
                    var message = result.SkippedBecauseNoOrgTags
                        ? "Tag suggestion skipped because no active organization tags exist."
                        : $"Suggested {result.PersistedSuggestionCount} meeting tag(s); ignored {result.IgnoredSuggestionCount} invalid suggestion(s).";

                    await _postMeetingProcessingTracker.CompleteStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.TagSuggestion,
                        message: message,
                        artifact: new PostMeetingArtifactLink(
                            "meeting_tag_suggestion",
                            ArtifactIds: result.SuggestionIds),
                        cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                if (_postMeetingProcessingTracker is not null)
                {
                    await _postMeetingProcessingTracker.FailStepAsync(
                        organizationId,
                        meetingId,
                        PostMeetingProcessingStepType.TagSuggestion,
                        "tag_suggestion_failed",
                        ex.GetBaseException().Message,
                        cancellationToken: cancellationToken);
                }

                _logger.LogError(
                    ex,
                    "Meeting tag suggestion failed. MeetingId={MeetingId} OrganizationId={OrganizationId}",
                    meetingId,
                    organizationId);
                throw;
            }
        }
    }
}

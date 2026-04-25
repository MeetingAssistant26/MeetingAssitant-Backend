using Hangfire;
using MediatR;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models.Events;

namespace MeetingAssistant.Features.LiveSession.Handlers
{
    public class GenerateMeetingSummaryHandler(
        IBackgroundJobClient backgroundJobClient) : INotificationHandler<MeetingTranscriptReadyEvent>
    {
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;

        public Task Handle(MeetingTranscriptReadyEvent notification, CancellationToken cancellationToken)
        {
            _backgroundJobClient.Enqueue<GenerateMeetingSummaryJob>(
                job => job.RunAsync(
                    notification.MeetingId,
                    notification.OrganizationId,
                    CancellationToken.None));

            return Task.CompletedTask;
        }
    }
}

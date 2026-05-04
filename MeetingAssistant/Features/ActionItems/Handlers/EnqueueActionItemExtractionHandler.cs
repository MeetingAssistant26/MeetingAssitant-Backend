using Hangfire;
using MediatR;
using MeetingAssistant.Features.ActionItems.Jobs;
using MeetingAssistant.Features.LiveSession.Models.Events;

namespace MeetingAssistant.Features.ActionItems.Handlers
{
    public class EnqueueActionItemExtractionHandler(
        IBackgroundJobClient backgroundJobClient) : INotificationHandler<MeetingTranscriptReadyEvent>
    {
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;

        public Task Handle(MeetingTranscriptReadyEvent notification, CancellationToken cancellationToken)
        {
            _backgroundJobClient.Enqueue<ExtractActionItemsJob>(
                job => job.RunAsync(
                    notification.MeetingId,
                    notification.OrganizationId,
                    CancellationToken.None));

            return Task.CompletedTask;
        }
    }
}

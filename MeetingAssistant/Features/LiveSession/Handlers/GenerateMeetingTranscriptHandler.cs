using Hangfire;
using MediatR;
using MeetingAssistant.Features.LiveSession.Jobs;
using MeetingAssistant.Features.LiveSession.Models.Events;

namespace MeetingAssistant.Features.LiveSession.Handlers
{
    public class GenerateMeetingTranscriptHandler(
        IBackgroundJobClient backgroundJobClient) : INotificationHandler<ParticipantAudioReadyEvent>
    {
        private readonly IBackgroundJobClient _backgroundJobClient = backgroundJobClient;

        public Task Handle(ParticipantAudioReadyEvent notification, CancellationToken cancellationToken)
        {
            _backgroundJobClient.Enqueue<GenerateMeetingTranscriptJob>(
                job => job.RunAsync(
                    notification.MeetingId,
                    notification.OrganizationId,
                    CancellationToken.None));

            return Task.CompletedTask;
        }
    }
}

using MediatR;
using MeetingAssistant.Api.Infrastructure.Services;

namespace MeetingAssistant.Infrastructure.Events
{
    public sealed class InfrastructureReadyEventHandler(
        ILogger<InfrastructureReadyEventHandler> logger,
        ICorrelationIdProvider correlationIdProvider) : INotificationHandler<InfrastructureReadyEvent>
    {
        public Task Handle(InfrastructureReadyEvent notification, CancellationToken cancellationToken)
        {
            logger.LogInformation(
                "[CorrelationId: {CorrelationId}] Infrastructure ready: {Message}",
                correlationIdProvider.CorrelationId,
                notification.Message);

            return Task.CompletedTask;
        }
    }
}

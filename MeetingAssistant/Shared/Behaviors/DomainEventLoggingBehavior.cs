using MediatR;
using Microsoft.Extensions.Logging;
using MeetingAssistant.Shared.Abstractions;

namespace MeetingAssistant.Shared.Behaviors
{
    public class DomainEventLoggingBehavior<TRequest, TResponse>(ILogger<DomainEventLoggingBehavior<TRequest, TResponse>> logger) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly ILogger<DomainEventLoggingBehavior<TRequest, TResponse>> _logger = logger;

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            if (request is IDomainEvent domainEvent)
            {
                _logger.LogInformation("Publishing Domain Event: {DomainEvent}", domainEvent.GetType().Name);
            }
            
            var response = await next();
            
            return response;
        }
    }
}

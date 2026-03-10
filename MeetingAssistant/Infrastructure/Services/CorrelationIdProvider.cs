using System;

namespace MeetingAssistant.Api.Infrastructure.Services
{
    public class CorrelationIdProvider : ICorrelationIdProvider
    {
        public string? CorrelationId { get; set; } = Guid.NewGuid().ToString();
    }
}

using System;

namespace MeetingAssistant.Api.Infrastructure.Services
{
    public interface ITenantProvider
    {
        Guid? CurrentOrganizationId { get; }
    }
}

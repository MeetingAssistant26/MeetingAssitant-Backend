using System;

namespace MeetingAssistant.Shared.Abstractions
{
    public interface IHasOrganizationId
    {
        Guid OrganizationId { get; set; }
    }
}

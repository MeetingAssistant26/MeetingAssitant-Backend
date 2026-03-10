using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Identity.Entites;

namespace MeetingAssistant.Shared
{
    public class AuditableEntity:BaseEntity
    {
        public string CreatedById { get; set; } = string.Empty;

        public string? UpdatedById { get; set; }


    }
}

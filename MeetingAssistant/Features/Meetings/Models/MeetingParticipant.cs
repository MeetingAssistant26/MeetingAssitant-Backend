using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Identity.Entites;

namespace MeetingAssistant.Features.Meetings.Models
{
    public class MeetingParticipant : BaseEntity, IHasOrganizationId
    {
        public Guid MeetingId { get; set; }
        public Meeting Meeting { get; set; } = default!;

        public Guid OrganizationId { get; set; }

        public Guid UserId { get; set; }
        public ApplicationUser User { get; set; } = default!;

        public MeetingRole MeetingRole { get; set; } = MeetingRole.Participant;
    }
}
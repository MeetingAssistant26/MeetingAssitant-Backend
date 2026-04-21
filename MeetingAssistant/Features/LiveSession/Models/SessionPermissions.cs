using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.LiveSession.Models
{
    public readonly record struct SessionPermissions(
        bool CanPublish,
        bool CanSubscribe,
        bool CanModerate,
        bool CanPublishData)
    {
        public static SessionPermissions ForRole(MeetingRole role)
        {
            return role switch
            {
                MeetingRole.Host => new SessionPermissions(true, true, true, true),
                MeetingRole.CoHost => new SessionPermissions(true, true, true, true),
                MeetingRole.Participant => new SessionPermissions(true, true, false, true),
                MeetingRole.Observer => new SessionPermissions(false, true, false, false),
                _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported meeting role.")
            };
        }
    }
}

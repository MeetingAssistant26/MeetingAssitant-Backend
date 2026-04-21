using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using FluentAssertions;
using Xunit;

namespace tests.Unit.LiveSession
{
    public class RolePermissionMappingTests
    {
        [Theory]
        [InlineData(MeetingRole.Host, true, true, true, true)]
        [InlineData(MeetingRole.CoHost, true, true, true, true)]
        [InlineData(MeetingRole.Participant, true, true, false, true)]
        [InlineData(MeetingRole.Observer, false, true, false, false)]
        public void ForRole_ReturnsExpectedMatrix(
            MeetingRole role,
            bool canPublish,
            bool canSubscribe,
            bool canModerate,
            bool canPublishData)
        {
            var permissions = SessionPermissions.ForRole(role);

            permissions.CanPublish.Should().Be(canPublish);
            permissions.CanSubscribe.Should().Be(canSubscribe);
            permissions.CanModerate.Should().Be(canModerate);
            permissions.CanPublishData.Should().Be(canPublishData);
        }
    }
}

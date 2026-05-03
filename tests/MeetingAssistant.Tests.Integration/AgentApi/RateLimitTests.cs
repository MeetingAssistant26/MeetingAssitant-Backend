using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.AgentApi;

public class RateLimitTests : IntegrationTestBase
{
    public RateLimitTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task AgentEndpoints_ShouldReturn429AndRetryAfter_WhenPerMeetingLimitExceeded()
    {
        // Arrange
        var meetingId = await SeedMeetingAsync(TestOrganizationId, MeetingStatus.InProgress);
        SetAgentAuthorization(TestOrganizationId, meetingId);

        HttpResponseMessage? throttledResponse = null;

        // Act
        for (var i = 0; i < 150; i++)
        {
            var response = await Client.GetAsync("/api/agent/organization");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttledResponse = response;
                break;
            }
        }

        // Assert
        throttledResponse.Should().NotBeNull("requests should exceed the per-meeting rate limit");
        throttledResponse!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttledResponse.Headers.Should().Contain(h => h.Key == "Retry-After");

        var retryAfterRaw = throttledResponse.Headers.GetValues("Retry-After").Single();
        int.Parse(retryAfterRaw, CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
    }

    private void SetAgentAuthorization(Guid organizationId, Guid meetingId)
    {
        var token = TestJwtTokenHelper.GenerateAgentToken(organizationId, meetingId);
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<Guid> SeedMeetingAsync(Guid organizationId, MeetingStatus status)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var meetingId = Guid.NewGuid();
        var start = DateTime.UtcNow;
        db.Meetings.Add(new Meeting
        {
            Id = meetingId,
            OrganizationId = organizationId,
            Title = "Rate limit meeting",
            ScheduledStartUtc = start,
            ScheduledEndUtc = start.AddHours(1),
            Status = status
        });

        await db.SaveChangesAsync();
        return meetingId;
    }
}

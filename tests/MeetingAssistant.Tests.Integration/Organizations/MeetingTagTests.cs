using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Organizations;

public class MeetingTagTests : IntegrationTestBase
{
    public MeetingTagTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Theory]
    [InlineData("#ccc", "#CCCCCC")]
    [InlineData("ccc", "#CCCCCC")]
    [InlineData("#c3d", "#CC33DD")]
    [InlineData("c3d", "#CC33DD")]
    [InlineData("#aabbcc", "#AABBCC")]
    [InlineData("aabbcc", "#AABBCC")]
    public async Task CreateMeetingTag_WithAcceptedHexColorFormats_ShouldNormalizeToSixCharacterHex(
        string input,
        string expected)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/meeting-tags",
            new { name = $"Tag {input}", color = input });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var tag = await response.Content.ReadFromJsonAsync<MeetingTagResponse>();
        tag.Should().NotBeNull();
        tag!.Color.Should().Be(expected);

        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storedColor = db.MeetingTags.IgnoreQueryFilters().Single(t => t.Id == tag.Id).Color;
        storedColor.Should().Be(expected);
    }

    [Theory]
    [InlineData("#ccc", "#CCCCCC")]
    [InlineData("ccc", "#CCCCCC")]
    public async Task UpdateMeetingTag_WithShorthandHexColor_ShouldNormalizeToSixCharacterHex(
        string input,
        string expected)
    {
        var tagId = await SeedMeetingTagAsync("Existing", "#112233");

        var response = await Client.PutAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/meeting-tags/{tagId}",
            new { color = input });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tag = await response.Content.ReadFromJsonAsync<MeetingTagResponse>();
        tag.Should().NotBeNull();
        tag!.Color.Should().Be(expected);
    }

    [Theory]
    [InlineData("#cc")]
    [InlineData("#cccc")]
    [InlineData("not-a-color")]
    public async Task CreateMeetingTag_WithInvalidColor_ShouldReturnBadRequest(string input)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/organizations/{TestOrganizationId}/meeting-tags",
            new { name = $"Invalid {input}", color = input });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<Guid> SeedMeetingTagAsync(string name, string? color)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var tag = new MeetingTag
        {
            Id = Guid.NewGuid(),
            OrganizationId = TestOrganizationId,
            Name = name,
            Color = color,
            IsActive = true
        };

        db.MeetingTags.Add(tag);
        await db.SaveChangesAsync();

        return tag.Id;
    }
}


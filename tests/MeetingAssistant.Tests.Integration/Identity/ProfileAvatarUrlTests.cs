using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Features.Identity.Models.Responses;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Identity;

public class ProfileAvatarUrlTests : IntegrationTestBase
{
    public ProfileAvatarUrlTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    private async Task SetAvatarAsync(string? avatarUrl)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(x => x.Id == TestUserId);
        user.ProfileAvatarUrl = avatarUrl;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UpdateProfile_OmittedAvatarUrl_ShouldPreserveExistingAvatarUrl()
    {
        await SetAvatarAsync("https://cdn.example.com/existing.png");

        var response = await Client.PutAsJsonAsync("/api/auth/profile", new { displayName = "Updated User" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UserProfileResponse>();
        body.Should().NotBeNull();
        body!.ProfileAvatarUrl.Should().Be("https://cdn.example.com/existing.png");
    }

    [Fact]
    public async Task UpdateProfile_NullAvatarUrl_ShouldClearExistingAvatarUrl()
    {
        await SetAvatarAsync("https://cdn.example.com/existing.png");

        var response = await Client.PutAsJsonAsync("/api/auth/profile", new
        {
            displayName = "Updated User",
            profileAvatarUrl = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UserProfileResponse>();
        body.Should().NotBeNull();
        body!.ProfileAvatarUrl.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProfile_BlankAvatarUrl_ShouldClearExistingAvatarUrl()
    {
        await SetAvatarAsync("https://cdn.example.com/existing.png");

        var response = await Client.PutAsJsonAsync("/api/auth/profile", new
        {
            displayName = "Updated User",
            profileAvatarUrl = "   "
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UserProfileResponse>();
        body.Should().NotBeNull();
        body!.ProfileAvatarUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("http://cdn.example.com/avatar.png")]
    [InlineData("https://cdn.example.com/avatar.png")]
    public async Task UpdateProfile_HttpOrHttpsAvatarUrl_ShouldUpdateAvatarUrl(string avatarUrl)
    {
        await SetAvatarAsync(null);

        var response = await Client.PutAsJsonAsync("/api/auth/profile", new
        {
            displayName = "Updated User",
            profileAvatarUrl = avatarUrl
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UserProfileResponse>();
        body.Should().NotBeNull();
        body!.ProfileAvatarUrl.Should().Be(avatarUrl);
    }

    [Theory]
    [InlineData("ftp://cdn.example.com/avatar.png")]
    [InlineData("not-a-url")]
    public async Task UpdateProfile_NonHttpAvatarUrl_ShouldReturnBadRequest(string avatarUrl)
    {
        var response = await Client.PutAsJsonAsync("/api/auth/profile", new
        {
            displayName = "Updated User",
            profileAvatarUrl = avatarUrl
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

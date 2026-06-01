using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using MeetingAssistant.Api.Shared;
using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeetingAssistant.Tests.Integration.Organizations;

public class OrganizationDiscoveryTests : IntegrationTestBase
{
    public OrganizationDiscoveryTests(MeetingAssistantWebFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task ListOrganizations_Unauthenticated_ShouldReturnUnauthorized()
    {
        using var anonymousClient = Factory.CreateClient();

        var response = await anonymousClient.GetAsync("/api/organizations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListOrganizations_SignedInUser_ShouldReturnOnlyActiveMembershipsForCurrentUser()
    {
        var otherUserOrgId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.Users.Add(new ApplicationUser
            {
                Id = otherUserId,
                Email = $"other-{otherUserId:N}@test.com",
                UserName = $"other-{otherUserId:N}@test.com",
                SecurityStamp = Guid.NewGuid().ToString()
            });

            db.Organizations.Add(new Organization { Id = otherUserOrgId, Name = "Other User Organization", Slug = "other-user-org" });

            db.UserOrgMemberships.Add(new UserOrgMembership
            {
                UserId = otherUserId,
                OrganizationId = otherUserOrgId,
                OrgRole = OrganizationRole.Admin,
                IsEnabled = true
            });

            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync("/api/organizations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<OrganizationListResponse>();
        result.Should().NotBeNull();
        result!.Items.Should().ContainSingle();
        result.Items.Should().Contain(o => o.Id == TestOrganizationId && o.Role == OrganizationRole.Admin.ToString());
        result.Items.Should().NotContain(o => o.Id == otherUserOrgId);
        result.Items.Should().OnlyContain(o => o.CreatedAtUtc != default && o.UpdatedAtUtc != default);
    }

    [Fact]
    public async Task GetOrganization_SignedInMember_ShouldReturnSameCoreFieldsAsListItem()
    {
        var response = await Client.GetAsync("/api/organizations");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<OrganizationListResponse>();
        var listItem = list!.Items.Single(o => o.Id == TestOrganizationId);

        var detailResponse = await Client.GetAsync($"/api/organizations/{TestOrganizationId}");

        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await detailResponse.Content.ReadFromJsonAsync<OrganizationResponse>();
        detail.Should().NotBeNull();
        detail!.Id.Should().Be(listItem.Id);
        detail.Name.Should().Be(listItem.Name);
        detail.Slug.Should().Be(listItem.Slug);
        detail.Role.Should().Be(listItem.Role);
        detail.CreatedAtUtc.Should().Be(listItem.CreatedAtUtc);
        detail.UpdatedAtUtc.Should().Be(listItem.UpdatedAtUtc);
    }

    [Fact]
    public async Task GetOrganization_Unauthenticated_ShouldReturnUnauthorized()
    {
        using var anonymousClient = Factory.CreateClient();

        var response = await anonymousClient.GetAsync($"/api/organizations/{TestOrganizationId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetOrganization_ExistingOrgWithoutMembership_ShouldReturnForbiddenProblem()
    {
        var otherOrgId = Guid.NewGuid();
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Organizations.Add(new Organization { Id = otherOrgId, Name = "No Access Org", Slug = "no-access-org" });
            await db.SaveChangesAsync();
        }

        var response = await Client.GetAsync($"/api/organizations/{otherOrgId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetOrganization_MissingOrg_ShouldReturnNotFoundProblem()
    {
        var missingOrgId = Guid.NewGuid();

        var response = await Client.GetAsync($"/api/organizations/{missingOrgId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<StandardErrorResponse>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)HttpStatusCode.NotFound);
    }
}

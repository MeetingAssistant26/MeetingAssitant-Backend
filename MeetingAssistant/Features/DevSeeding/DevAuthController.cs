using MeetingAssistant.Features.Identity.Entites;
using MeetingAssistant.Features.Identity.Services;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MeetingAssistant.Features.DevSeeding;

[ApiController]
[Route("api/dev/auth")]
public sealed class DevAuthController(
    UserManager<ApplicationUser> userManager,
    ITokenService tokenService,
    ApplicationDbContext dbContext,
    IHostEnvironment environment,
    ILogger<DevAuthController> logger) : ControllerBase
{
    private static readonly Dictionary<int, string> UserEmails = new()
    {
        [1] = "dev-user-1@example.com",
        [2] = "dev-user-2@example.com",
    };

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] DevLoginRequest request, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return NotFound();

        if (!UserEmails.TryGetValue(request.User, out var email))
        {
            return BadRequest(new { error = "Invalid user. Use 1 or 2." });
        }

        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            logger.LogWarning("Dev user not found: {Email}. Run the seeder first.", email);
            return BadRequest(new { error = "Dev user not found. Seed the database first." });
        }

        // Find active org membership (project to avoid entity materialization issues)
        var membership = await dbContext.UserOrgMemberships
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.UserId == user.Id && m.IsEnabled)
            .Select(m => new { m.OrganizationId, m.OrgRole })
            .FirstOrDefaultAsync(cancellationToken);

        if (membership is null)
        {
            logger.LogWarning("Dev user has no org membership: {Email}", email);
            return BadRequest(new { error = "Dev user has no organization membership." });
        }

        // Find the dev meeting
        var meeting = await dbContext.Meetings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.OrganizationId == membership.OrganizationId && m.Status == MeetingStatus.Scheduled)
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => new { m.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (meeting is null)
        {
            logger.LogWarning("No dev meeting found for org: {OrgId}", membership.OrganizationId);
            return BadRequest(new { error = "No dev meeting found. Seed the database first." });
        }

        var (accessToken, expiresIn) = tokenService.GenerateAccessToken(
            user,
            membership.OrganizationId,
            membership.OrgRole.ToString());

        logger.LogInformation(
            "Dev login: User={UserId} Email={Email} Org={OrgId} Meeting={MeetingId}",
            user.Id, email, membership.OrganizationId, meeting.Id);

        return Ok(new DevLoginResponse(
            accessToken,
            expiresIn,
            user.Id,
            user.Email!,
            user.DisplayName!,
            membership.OrganizationId,
            meeting.Id));
    }
}

public sealed record DevLoginRequest(int User);

public sealed record DevLoginResponse(
    string AccessToken,
    int ExpiresIn,
    Guid UserId,
    string Email,
    string DisplayName,
    Guid OrganizationId,
    Guid MeetingId);

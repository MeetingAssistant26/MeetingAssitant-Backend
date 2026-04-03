using System.Security.Claims;
using MeetingAssistant.Features.Organizations.Contracts.Requests;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Organizations.Endpoints.Organization
{
    public partial class OrganizationController
    {
        [HttpPost]
        public async Task<IActionResult> CreateOrganization(
            [FromBody] CreateOrganizationRequest request,
            CancellationToken cancellationToken)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await _organizationService.CreateOrganizationAsync(request, userId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}

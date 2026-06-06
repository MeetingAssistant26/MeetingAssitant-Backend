using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Admin
{
    public partial class IntegrationAdminController
    {
        [HttpGet("{provider}/workspaces")]
        public async Task<IActionResult> ListProviderWorkspaces(
            Guid organizationId,
            string provider,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.TryParse<ExternalProvider>(provider, true, out var providerEnum))
                return BadRequest(new { error = "Invalid provider." });

            var result = await _integrationAdminService.GetWorkspacesAsync(organizationId, providerEnum, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}

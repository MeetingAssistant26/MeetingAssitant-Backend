using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Admin
{
    public partial class IntegrationAdminController
    {
        [HttpGet("{provider}/workspaces/{workspaceId}/boards")]
        public async Task<IActionResult> ListWorkspaceBoards(
            Guid organizationId,
            string provider,
            string workspaceId,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.TryParse<ExternalProvider>(provider, true, out var providerEnum))
                return BadRequest(new { error = "Invalid provider." });

            var result = await _integrationAdminService.GetBoardsAsync(organizationId, providerEnum, workspaceId, cancellationToken);

            return result.IsSuccess
                ? Ok(result.Value)
                : result.ToProblem(_correlationIdProvider);
        }
    }
}

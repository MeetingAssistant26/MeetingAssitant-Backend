using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Models.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Admin
{
    public partial class IntegrationAdminController
    {
        [HttpPut("{provider}/destination")]
        public async Task<IActionResult> SaveIntegrationDestination(
            Guid organizationId,
            string provider,
            [FromBody] SaveIntegrationDestinationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.TryParse<ExternalProvider>(provider, true, out var providerEnum))
                return BadRequest(new { error = "Invalid provider." });

            var result = await _integrationAdminService.SaveDestinationAsync(request, organizationId, providerEnum, cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}

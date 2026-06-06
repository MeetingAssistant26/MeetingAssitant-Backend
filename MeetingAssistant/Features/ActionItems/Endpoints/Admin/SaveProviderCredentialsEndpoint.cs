using MeetingAssistant.Features.ActionItems.Models.Enums;
using MeetingAssistant.Features.ActionItems.Models.Requests;
using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.ActionItems.Endpoints.Admin
{
    public partial class IntegrationAdminController
    {
        [HttpPut("{provider}/credentials")]
        public async Task<IActionResult> SaveProviderCredentials(
            Guid organizationId,
            string provider,
            [FromBody] SaveProviderCredentialsRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!Enum.TryParse<ExternalProvider>(provider, true, out var providerEnum))
                return BadRequest(new { error = "Invalid provider." });

            var result = await _integrationAdminService.SaveCredentialsAsync(request, organizationId, providerEnum, cancellationToken);

            return result.IsSuccess
                ? NoContent()
                : result.ToProblem(_correlationIdProvider);
        }
    }
}

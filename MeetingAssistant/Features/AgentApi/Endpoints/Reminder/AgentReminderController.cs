using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.AgentApi.Services;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MeetingAssistant.Features.AgentApi.Endpoints.Reminder
{
    [ApiController]
    [Route("api/agent")]
    [Authorize(Policy = AgentAuthenticationDefaults.Policy)]
    [EnableRateLimiting("AgentPerMeeting")]
    public partial class AgentReminderController(
        IAgentReminderService agentReminderService,
        IAgentContextProvider agentContextProvider,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IAgentReminderService _agentReminderService = agentReminderService;
        protected readonly IAgentContextProvider _agentContextProvider = agentContextProvider;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;

        protected Result<Guid> TryGetOrganizationId()
        {
            return _agentContextProvider.IsAgentRequest
                   && _agentContextProvider.CurrentOrganizationId is Guid organizationId
                ? Result.Success(organizationId)
                : Result.Failure<Guid>(AgentApiErrors.MissingClaims);
        }

        protected Result<(Guid OrganizationId, Guid MeetingId)> TryGetScopedMeeting(Guid meetingId)
        {
            if (!_agentContextProvider.IsAgentRequest
                || _agentContextProvider.CurrentOrganizationId is not Guid organizationId
                || _agentContextProvider.CurrentMeetingId is not Guid tokenMeetingId)
            {
                return Result.Failure<(Guid, Guid)>(AgentApiErrors.MissingClaims);
            }

            if (meetingId != tokenMeetingId)
            {
                return Result.Failure<(Guid, Guid)>(AgentApiErrors.AccessDenied);
            }

            return Result.Success((organizationId, tokenMeetingId));
        }
    }
}

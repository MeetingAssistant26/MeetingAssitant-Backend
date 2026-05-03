using System.Security.Claims;
using MeetingAssistant.Features.AgentApi.Services;

namespace MeetingAssistant.Api.Infrastructure.Services
{
    public class AgentContextProvider(IHttpContextAccessor httpContextAccessor) : IAgentContextProvider
    {
        private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

        public bool IsAgentRequest
        {
            get
            {
                var user = _httpContextAccessor.HttpContext?.User;
                if (user?.Identity?.IsAuthenticated != true)
                {
                    return false;
                }

                var agentClaim = user.FindFirstValue(AgentAuthenticationDefaults.AgentClaim);
                return string.Equals(agentClaim, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        public Guid? CurrentOrganizationId => TryGetGuidClaim("organizationId");

        public Guid? CurrentMeetingId => TryGetGuidClaim("meetingId");

        private Guid? TryGetGuidClaim(string claimType)
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var claimValue = user.FindFirstValue(claimType);
            return Guid.TryParse(claimValue, out var parsed) ? parsed : null;
        }
    }
}

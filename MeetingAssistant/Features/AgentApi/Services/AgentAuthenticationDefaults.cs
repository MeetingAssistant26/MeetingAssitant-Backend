namespace MeetingAssistant.Features.AgentApi.Services
{
    public static class AgentAuthenticationDefaults
    {
        public const string Scheme = "AgentJwt";
        public const string Policy = "AgentOnly";
        public const string AgentClaim = "agent";
    }
}

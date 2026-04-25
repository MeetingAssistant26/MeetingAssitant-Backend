using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MeetingAssistant.Features.LiveSession.Hubs
{
    [Authorize]
    public class LiveSessionHub(ILogger<LiveSessionHub> logger) : Hub
    {
        private readonly ILogger<LiveSessionHub> _logger = logger;

        public override async Task OnConnectedAsync()
        {
            var orgClaim = Context.User?.FindFirst("organizationId")?.Value;
            if (Guid.TryParse(orgClaim, out var orgId))
            {
                await base.OnConnectedAsync();
                await Groups.AddToGroupAsync(Context.ConnectionId, $"org:{orgId}");
                return;
            }

            _logger.LogWarning(
                "LiveSessionHub connection {ConnectionId} missing valid organizationId claim",
                Context.ConnectionId);

            Context.Abort();
        }
    }
}

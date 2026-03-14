using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MeetingAssistant.Infrastructure.SignalR
{
    [Authorize]
    public class NotificationHub(ILogger<NotificationHub> logger) : Hub
    {
        private readonly ILogger<NotificationHub> _logger = logger;

        public override async Task OnConnectedAsync()
        {
            var orgClaim = Context.User?.FindFirst("organizationId")?.Value;
            if (Guid.TryParse(orgClaim, out var organizationId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, $"org:{organizationId}");
            }
            else
            {
                _logger.LogWarning(
                    "SignalR connection {ConnectionId} missing valid organizationId claim.",
                    Context.ConnectionId);
                Context.Abort();
                return;
            }

            await base.OnConnectedAsync();
        }
    }
}
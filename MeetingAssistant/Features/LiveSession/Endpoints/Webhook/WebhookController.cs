using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.LiveSession.Services;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.LiveSession.Endpoints.Webhook
{
    [ApiController]
    [Route("api/webhooks")]
    public partial class WebhookController(
        IWebhookService webhookService,
        ILiveKitWebhookValidator webhookValidator,
        ICorrelationIdProvider correlationIdProvider,
        ILogger<WebhookController> logger) : ControllerBase
    {
        protected readonly IWebhookService _webhookService = webhookService;
        protected readonly ILiveKitWebhookValidator _webhookValidator = webhookValidator;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
        protected readonly ILogger<WebhookController> _logger = logger;
    }
}

using MeetingAssistant.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MeetingAssistant.Features.LiveSession.Endpoints.Webhook
{
    public partial class WebhookController
    {
        [AllowAnonymous]
        [HttpPost("livekit")]
        public async Task<IActionResult> LiveKitWebhook(CancellationToken cancellationToken)
        {
            Request.EnableBuffering();

            string rawBody;
            using (var reader = new StreamReader(Request.Body, leaveOpen: true))
            {
                rawBody = await reader.ReadToEndAsync(cancellationToken);
            }

            Request.Body.Position = 0;

            var authHeader = Request.Headers.Authorization.ToString();
            var validationResult = _webhookValidator.Validate(rawBody, authHeader);
            if (validationResult.IsFailure)
            {
                var details = ParseWebhookDetails(rawBody);
                _logger.LogWarning(
                    "Rejected LiveKit webhook. Reason={Reason} ExternalEventId={ExternalEventId} MeetingRoom={MeetingRoom}",
                    validationResult.Error.Code,
                    details.ExternalEventId,
                    details.MeetingRoom);

                return validationResult.ToProblem(_correlationIdProvider);
            }

            var result = await _webhookService.ProcessAsync(
                validationResult.Value,
                rawBody,
                cancellationToken);

            return result.IsSuccess
                ? Ok()
                : result.ToProblem(_correlationIdProvider);
        }

        private static (string? ExternalEventId, string? MeetingRoom) ParseWebhookDetails(string rawBody)
        {
            if (string.IsNullOrWhiteSpace(rawBody))
            {
                return (null, null);
            }

            try
            {
                using var document = JsonDocument.Parse(rawBody);
                var root = document.RootElement;

                string? externalEventId = null;
                if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                {
                    externalEventId = idElement.GetString();
                }

                string? roomName = null;
                if (root.TryGetProperty("room", out var roomElement)
                    && roomElement.ValueKind == JsonValueKind.Object
                    && roomElement.TryGetProperty("name", out var roomNameElement)
                    && roomNameElement.ValueKind == JsonValueKind.String)
                {
                    roomName = roomNameElement.GetString();
                }

                if (roomName == null
                    && root.TryGetProperty("egressInfo", out var egressElement)
                    && egressElement.ValueKind == JsonValueKind.Object
                    && egressElement.TryGetProperty("roomName", out var egressRoomName)
                    && egressRoomName.ValueKind == JsonValueKind.String)
                {
                    roomName = egressRoomName.GetString();
                }

                return (externalEventId, roomName);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }
    }
}

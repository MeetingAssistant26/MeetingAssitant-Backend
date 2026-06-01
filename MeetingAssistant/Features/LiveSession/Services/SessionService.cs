using MeetingAssistant.Features.LiveSession.Contracts.Responses;
using MeetingAssistant.Features.LiveSession.Models;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Infrastructure.Persistence.DbContext;
using MeetingAssistant.Shared.Abstractions;
using MeetingAssistant.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class SessionService(
        ApplicationDbContext dbContext,
        ILiveKitTokenIssuer tokenIssuer,
        IAiAssistantDispatchService? aiAssistantDispatchService = null,
        ILogger<SessionService>? logger = null) : ISessionService
    {
        private readonly ApplicationDbContext _dbContext = dbContext;
        private readonly ILiveKitTokenIssuer _tokenIssuer = tokenIssuer;
        private readonly IAiAssistantDispatchService? _aiAssistantDispatchService = aiAssistantDispatchService;
        private readonly ILogger<SessionService>? _logger = logger;

        public async Task<Result<JoinTokenResponse>> IssueJoinTokenAsync(
            Guid meetingId,
            Guid callerUserId,
            string? displayName,
            CancellationToken cancellationToken = default)
        {
            var meetingData = await _dbContext.Meetings
                .Where(m => m.Id == meetingId)
                .Select(m => new
                {
                    m.Id,
                    m.Status,
                    m.OrganizationId,
                    m.AiAssistantEnabled,
                    Participant = m.Participants
                        .Where(p => p.UserId == callerUserId)
                        .Select(p => new
                        {
                            p.UserId,
                            p.MeetingRole,
                            DisplayName = p.User.DisplayName,
                            UserName = p.User.UserName
                        })
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (meetingData == null)
            {
                return Result.Failure<JoinTokenResponse>(LiveSessionErrors.MeetingNotFound);
            }

            if (meetingData.Status is MeetingStatus.Cancelled or MeetingStatus.Completed)
            {
                return Result.Failure<JoinTokenResponse>(LiveSessionErrors.MeetingNotJoinable);
            }

            var participant = meetingData.Participant;
            if (participant == null)
            {
                return Result.Failure<JoinTokenResponse>(LiveSessionErrors.NotAParticipant);
            }

            var effectiveDisplayName = ResolveDisplayName(
                displayName,
                participant.DisplayName,
                participant.UserName,
                callerUserId);
            var permissions = SessionPermissions.ForRole(participant.MeetingRole);

            var issueResult = _tokenIssuer.Issue(
                meetingId,
                meetingData.OrganizationId,
                callerUserId,
                effectiveDisplayName,
                participant.MeetingRole,
                permissions);

            if (issueResult.IsFailure)
            {
                return Result.Failure<JoinTokenResponse>(issueResult.Error);
            }

            if (meetingData.AiAssistantEnabled && _aiAssistantDispatchService is not null)
            {
                var dispatchResult = await _aiAssistantDispatchService.EnsureDispatchedForMeetingAsync(
                    meetingData.OrganizationId,
                    meetingData.Id,
                    callerUserId,
                    cancellationToken);

                if (dispatchResult.IsFailure)
                {
                    _logger?.LogWarning(
                        "Failed to auto-dispatch LiveKit AI assistant while issuing join token. OrganizationId={OrganizationId} MeetingId={MeetingId} UserId={UserId} ErrorCode={ErrorCode}",
                        meetingData.OrganizationId,
                        meetingData.Id,
                        callerUserId,
                        dispatchResult.Error.Code);
                }
            }

            return Result.Success(new JoinTokenResponse(
                issueResult.Value.AccessToken,
                issueResult.Value.RoomName,
                issueResult.Value.ServerUrl,
                issueResult.Value.ExpiresAtUtc,
                permissions));
        }

        private static string ResolveDisplayName(
            string? requestedDisplayName,
            string? participantDisplayName,
            string? participantUserName,
            Guid callerUserId)
        {
            if (!string.IsNullOrWhiteSpace(requestedDisplayName))
            {
                return requestedDisplayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(participantDisplayName))
            {
                return participantDisplayName;
            }

            if (!string.IsNullOrWhiteSpace(participantUserName))
            {
                return participantUserName;
            }

            return $"user:{callerUserId}";
        }
    }
}

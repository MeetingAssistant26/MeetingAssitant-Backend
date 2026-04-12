using MeetingAssistant.Api.Infrastructure.Services;
using MeetingAssistant.Features.Meetings.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetingAssistant.Features.Meetings.Endpoints.Participant
{
    [ApiController]
    [Route("api/meetings/{meetingId:guid}/participants")]
    [Authorize]
    public partial class ParticipantController(
        IParticipantService participantService,
        ICorrelationIdProvider correlationIdProvider) : ControllerBase
    {
        protected readonly IParticipantService _participantService = participantService;
        protected readonly ICorrelationIdProvider _correlationIdProvider = correlationIdProvider;
    }
}
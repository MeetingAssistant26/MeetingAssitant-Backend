using Mapster;
using MeetingAssistant.Features.Meetings.Contracts.Responses;
using MeetingAssistant.Features.Meetings.Models;

namespace MeetingAssistant.Features.Meetings.Mapping
{
    public class MeetingMappingConfig : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            config.NewConfig<Meeting, MeetingResponse>()
                .Map(dest => dest.Id, src => src.Id)
                .Map(dest => dest.Participants, src => src.Participants)
                .Map(dest => dest.TagIds, src => src.Tags.Select(t => t.MeetingTagId).ToList());

            config.NewConfig<MeetingParticipant, ParticipantResponse>()
                .Map(dest => dest.Id, src => src.Id)
                .Map(dest => dest.MeetingId, src => src.MeetingId)
                .Map(dest => dest.UserId, src => src.UserId)
                .Map(dest => dest.DisplayName, src => src.User != null ? src.User.DisplayName ?? string.Empty : string.Empty)
                .Map(dest => dest.Email, src => src.User != null ? src.User.Email ?? string.Empty : string.Empty)
                .Map(dest => dest.CreatedAtUtc, src => src.CreatedAtUtc)
                .Map(dest => dest.Role, src => src.MeetingRole);
        }
    }
}
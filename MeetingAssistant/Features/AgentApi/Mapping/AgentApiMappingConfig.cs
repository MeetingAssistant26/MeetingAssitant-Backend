using Mapster;
using MeetingAssistant.Features.AgentApi.Models.Responses;
using MeetingAssistant.Features.Meetings.Models;
using MeetingAssistant.Features.Organizations.Contracts.Responses;
using MeetingAssistant.Features.Organizations.Models;
using MeetingAssistant.Features.Tasks.Models.Entities;

namespace MeetingAssistant.Features.AgentApi.Mapping
{
    public class AgentApiMappingConfig : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
            config.NewConfig<Organization, AgentOrganizationResponse>()
                .Map(dest => dest.Id, src => src.Id)
                .Map(dest => dest.Name, src => src.Name)
                .Map(dest => dest.Slug, src => src.Slug)
                .Map(dest => dest.MemberCount, src => 0);

            config.NewConfig<MeetingParticipant, AgentMemberResponse>()
                .Map(dest => dest.UserId, src => src.UserId)
                .Map(dest => dest.DisplayName, src => src.User != null ? src.User.DisplayName ?? string.Empty : string.Empty)
                .Map(dest => dest.JobRole, src => (string?)null)
                .Map(dest => dest.Context, src => (string?)null);

            config.NewConfig<Reminder, AgentReminderResponse>()
                .Map(dest => dest.Id, src => src.Id)
                .Map(dest => dest.Text, src => src.Text)
                .Map(dest => dest.Scope, src => src.Scope.ToString())
                .Map(dest => dest.TargetUserId, src => src.TargetUserId)
                .Map(dest => dest.ReminderAtUtc, src => src.ReminderAtUtc)
                .Map(dest => dest.Status, src => src.Status.ToString());

            config.NewConfig<Meeting, AgentMeetingResponse>()
                .Map(dest => dest.Id, src => src.Id)
                .Map(dest => dest.Title, src => src.Title)
                .Map(dest => dest.ScheduledStartUtc, src => src.ScheduledStartUtc)
                .Map(dest => dest.ScheduledEndUtc, src => src.ScheduledEndUtc)
                .Map(dest => dest.Status, src => src.Status.ToString())
                .Map(dest => dest.RecurrenceConfig, src => src.RecurrenceConfig)
                .Map(dest => dest.TagIds, src => src.Tags.Select(t => t.MeetingTagId).ToList());

            config.NewConfig<Meeting, AgentMeetingDetailResponse>()
                .Map(dest => dest.Id, src => src.Id)
                .Map(dest => dest.Title, src => src.Title)
                .Map(dest => dest.ScheduledStartUtc, src => src.ScheduledStartUtc)
                .Map(dest => dest.ScheduledEndUtc, src => src.ScheduledEndUtc)
                .Map(dest => dest.Status, src => src.Status.ToString())
                .Map(dest => dest.RecurrenceConfig, src => src.RecurrenceConfig)
                .Map(dest => dest.TagIds, src => src.Tags.Select(t => t.MeetingTagId).ToList())
                .Map(dest => dest.Participants, src => new List<AgentMemberResponse>());

            config.NewConfig<MeetingTag, MeetingTagResponse>()
                .Map(dest => dest.Id, src => src.Id)
                .Map(dest => dest.OrganizationId, src => src.OrganizationId)
                .Map(dest => dest.Name, src => src.Name)
                .Map(dest => dest.Color, src => src.Color)
                .Map(dest => dest.CreatedAtUtc, src => src.CreatedAtUtc)
                .Map(dest => dest.UpdatedAtUtc, src => src.UpdatedAtUtc);
        }
    }
}

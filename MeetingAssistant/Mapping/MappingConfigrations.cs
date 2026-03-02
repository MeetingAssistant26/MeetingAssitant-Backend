
using Mapster;
using MeetingAssistant.Contracts.Authentication;
using MeetingAssistant.Entities;

namespace MeetingAssistant.Mapping
{
    public class MappingConfigrations : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
          

            config.NewConfig<RegisterRequest, ApplicationUser>()
                .Map(dest => dest.UserName, src => src.Email);
        }

    }
}


using Mapster;
using MeetingAssistant.Features.Identity.DTOs;
using MeetingAssistant.Features.Identity.Entites;

namespace MeetingAssistant.Features.Identity.Mapping
{
    public class IdentityMappingConfig : IRegister
    {
        public void Register(TypeAdapterConfig config)
        {
          

            config.NewConfig<RegisterRequest, ApplicationUser>()
                .Map(dest => dest.UserName, src => src.Email);
        }

    }
}

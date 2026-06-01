using System.Text.Json.Serialization;

namespace MeetingAssistant.Features.Identity.Models.Requests
{
    public class UpdateProfileRequest
    {
        private string? _profileAvatarUrl;

        public string DisplayName { get; init; } = string.Empty;

        public string? ProfileAvatarUrl
        {
            get => _profileAvatarUrl;
            init
            {
                _profileAvatarUrl = value;
                ProfileAvatarUrlWasProvided = true;
            }
        }

        [JsonIgnore]
        public bool ProfileAvatarUrlWasProvided { get; private set; }
    }
}

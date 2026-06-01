using FluentValidation;
using MeetingAssistant.Features.Identity.Models.Requests;

namespace MeetingAssistant.Features.Identity.Validators
{
    public class UpdateProfileRequestValidator: AbstractValidator<UpdateProfileRequest>
    {
        public UpdateProfileRequestValidator()
        {
            RuleFor(x => x.DisplayName)
                .Cascade(CascadeMode.Stop)
                .NotEmpty()
                .Matches(@"^[a-zA-Z0-9\s\.\-_]+$")
                .Length(3, 100);

            RuleFor(x => x.ProfileAvatarUrl)
                .Must(value =>
                    string.IsNullOrWhiteSpace(value) ||
                    Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                .WithMessage("Profile avatar URL must be an absolute HTTP or HTTPS URL.");
        }
    }
}

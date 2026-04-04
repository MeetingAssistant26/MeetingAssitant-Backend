namespace MeetingAssistant.Features.Organizations.Services
{
    public interface ISlugGenerator
    {
        Task<string> GenerateUniqueSlugAsync(string name, CancellationToken cancellationToken = default);
    }
}

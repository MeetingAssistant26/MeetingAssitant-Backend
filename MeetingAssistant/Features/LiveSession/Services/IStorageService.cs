namespace MeetingAssistant.Features.LiveSession.Services
{
    public interface IStorageService
    {
        Task<long?> UploadFromUrlAsync(string sourceUrl, string objectKey, CancellationToken cancellationToken = default);
        Task EnsureBucketExistsAsync(CancellationToken cancellationToken = default);
    }
}

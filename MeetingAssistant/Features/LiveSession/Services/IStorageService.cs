namespace MeetingAssistant.Features.LiveSession.Services
{
    public sealed record StorageUploadResult(long? SizeBytes, string StorageLocation);

    public interface IStorageService
    {
        Task<long?> UploadFromUrlAsync(string sourceUrl, string objectKey, CancellationToken cancellationToken = default);
        Task<StorageUploadResult> UploadFileAsync(string sourceFilePath, string objectKey, CancellationToken cancellationToken = default);
        Task EnsureBucketExistsAsync(CancellationToken cancellationToken = default);
    }
}

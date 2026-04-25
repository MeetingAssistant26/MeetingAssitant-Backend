using MeetingAssistant.Features.LiveSession.Infrastructure;
using Minio;
using Minio.DataModel.Args;
using Microsoft.Extensions.Options;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class StorageService(
        IOptions<StorageOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<StorageService> logger) : IStorageService
    {
        private readonly StorageOptions _options = options.Value;
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly ILogger<StorageService> _logger = logger;

        public async Task<long?> UploadFromUrlAsync(string sourceUrl, string objectKey, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                throw new ArgumentException("sourceUrl must be provided for upload.", nameof(sourceUrl));
            }

            var minioClient = CreateClient();
            await EnsureBucketExistsAsync(cancellationToken);

            using var httpClient = _httpClientFactory.CreateClient();
            using var response = await httpClient.GetAsync(sourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var objectSize = response.Content.Headers.ContentLength ?? -1;

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_options.Bucket)
                .WithObject(objectKey)
                .WithStreamData(stream)
                .WithObjectSize(objectSize)
                .WithContentType(response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");

            await minioClient.PutObjectAsync(putObjectArgs, cancellationToken);

            _logger.LogInformation(
                "Uploaded recording object to storage bucket {Bucket} key {ObjectKey}",
                _options.Bucket,
                objectKey);

            return objectSize >= 0 ? objectSize : null;
        }

        public async Task EnsureBucketExistsAsync(CancellationToken cancellationToken = default)
        {
            var minioClient = CreateClient();

            var bucketExists = await minioClient.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(_options.Bucket),
                cancellationToken);

            if (bucketExists)
            {
                _logger.LogDebug("Storage bucket {Bucket} already exists", _options.Bucket);
                return;
            }

            _logger.LogInformation(
                "Storage bucket {Bucket} does not exist — creating it now",
                _options.Bucket);

            await minioClient.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(_options.Bucket),
                cancellationToken);

            _logger.LogInformation(
                "Storage bucket {Bucket} created successfully",
                _options.Bucket);
        }

        private IMinioClient CreateClient()
        {
            var endpoint = _options.Endpoint?.Trim() ?? string.Empty;
            var secure = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            endpoint = endpoint
                .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

            return new MinioClient()
                .WithEndpoint(endpoint)
                .WithCredentials(_options.AccessKey, _options.SecretKey)
                .WithSSL(secure)
                .Build();
        }
    }
}

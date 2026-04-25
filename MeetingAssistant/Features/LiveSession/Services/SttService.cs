using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeetingAssistant.Features.LiveSession.Infrastructure;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace MeetingAssistant.Features.LiveSession.Services
{
    public class SttService(
        IOptions<OpenAiCompatibleOptions> options,
        IOptions<StorageOptions> storageOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<SttService> logger) : ISttService
    {
        private const long MaxSingleRequestBytes = 24_000_000;
        private const int MaxChunkDurationSeconds = 600;
        private const int CopyBufferSize = 81920;

        private static readonly Regex SilenceEndRegex = new(@"silence_end:\s*(?<seconds>\d+(\.\d+)?)", RegexOptions.Compiled);
        private static readonly Regex DurationRegex = new(@"Duration:\s*(?<hours>\d{2}):(?<minutes>\d{2}):(?<seconds>\d{2}(\.\d+)?)", RegexOptions.Compiled);

        private readonly OpenAiCompatibleOptions.ProviderConfig _stt = options.Value.Stt;
        private readonly StorageOptions _storage = storageOptions.Value;
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly ILogger<SttService> _logger = logger;

        public async Task<TrackTranscriptionResult> TranscribeTrackAsync(
            Guid participantUserId,
            string storageObjectKey,
            CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(storageObjectKey);

            var minioClient = CreateClient();
            var objectSize = await GetObjectSizeAsync(minioClient, storageObjectKey, ct);

            IReadOnlyList<TranscriptSegment> segments;
            if (objectSize <= MaxSingleRequestBytes)
            {
                segments = await TranscribeSingleObjectAsync(
                    minioClient,
                    participantUserId,
                    storageObjectKey,
                    ct);
            }
            else
            {
                segments = await TranscribeChunkedAsync(
                    minioClient,
                    participantUserId,
                    storageObjectKey,
                    ct);
            }

            return new TrackTranscriptionResult(_stt.Model, segments);
        }

        private async Task<IReadOnlyList<TranscriptSegment>> TranscribeSingleObjectAsync(
            IMinioClient minioClient,
            Guid participantUserId,
            string storageObjectKey,
            CancellationToken ct)
        {
            using var multipart = new MultipartFormDataContent();
            using var fileContent = new MinioObjectContent(minioClient, _storage.Bucket, storageObjectKey, ct);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");

            multipart.Add(fileContent, "file", Path.GetFileName(storageObjectKey));
            multipart.Add(new StringContent(_stt.Model), "model");
            multipart.Add(new StringContent("verbose_json"), "response_format");
            multipart.Add(new StringContent("segment"), "timestamp_granularities[]");

            return await SendTranscriptionRequestAsync(participantUserId, multipart, 0, ct);
        }

        private async Task<IReadOnlyList<TranscriptSegment>> TranscribeChunkedAsync(
            IMinioClient minioClient,
            Guid participantUserId,
            string storageObjectKey,
            CancellationToken ct)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"stt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);

            try
            {
                var extension = Path.GetExtension(storageObjectKey);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".ogg";
                }

                var sourceFile = Path.Combine(tempRoot, $"source{extension}");
                await DownloadObjectToFileAsync(minioClient, storageObjectKey, sourceFile, ct);

                var windows = await BuildChunkWindowsAsync(sourceFile, ct);
                var results = new List<TranscriptSegment>();

                for (var i = 0; i < windows.Count; i++)
                {
                    var window = windows[i];
                    var chunkFile = Path.Combine(tempRoot, $"chunk-{i:000}{extension}");
                    await ExtractChunkAsync(sourceFile, chunkFile, window.StartSeconds, window.EndSeconds, ct);

                    using var multipart = new MultipartFormDataContent();
                    await using var chunkStream = File.OpenRead(chunkFile);
                    using var fileContent = new StreamContent(chunkStream, CopyBufferSize);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");

                    multipart.Add(fileContent, "file", Path.GetFileName(chunkFile));
                    multipart.Add(new StringContent(_stt.Model), "model");
                    multipart.Add(new StringContent("verbose_json"), "response_format");
                    multipart.Add(new StringContent("segment"), "timestamp_granularities[]");

                    var chunkSegments = await SendTranscriptionRequestAsync(
                        participantUserId,
                        multipart,
                        (long)Math.Round(window.StartSeconds * 1000),
                        ct);

                    results.AddRange(chunkSegments);
                }

                return results
                    .OrderBy(x => x.StartMs)
                    .ToList();
            }
            finally
            {
                try
                {
                    Directory.Delete(tempRoot, true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to clean temporary STT directory {Directory}", tempRoot);
                }
            }
        }

        private async Task<List<ChunkWindow>> BuildChunkWindowsAsync(string sourceFile, CancellationToken ct)
        {
            var durationSeconds = await ReadDurationSecondsAsync(sourceFile, ct);
            if (durationSeconds <= MaxChunkDurationSeconds)
            {
                return [new ChunkWindow(0, durationSeconds)];
            }

            var silenceBoundaries = await DetectSilenceBoundariesAsync(sourceFile, ct);
            var windows = new List<ChunkWindow>();
            var currentStart = 0d;

            while (currentStart < durationSeconds)
            {
                var targetEnd = Math.Min(durationSeconds, currentStart + MaxChunkDurationSeconds);

                var silenceEnd = silenceBoundaries
                    .Where(x => x > currentStart && x <= targetEnd)
                    .DefaultIfEmpty(0d)
                    .Max();

                var chunkEnd = silenceEnd > currentStart ? silenceEnd : targetEnd;
                windows.Add(new ChunkWindow(currentStart, chunkEnd));
                currentStart = chunkEnd;
            }

            return windows;
        }

        private async Task ExtractChunkAsync(
            string sourceFile,
            string chunkFile,
            double startSeconds,
            double endSeconds,
            CancellationToken ct)
        {
            var args = new[]
            {
                "-y",
                "-i", sourceFile,
                "-ss", startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-to", endSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-c", "copy",
                chunkFile
            };

            await RunProcessAsync("ffmpeg", args, ct);
        }

        private async Task<double> ReadDurationSecondsAsync(string sourceFile, CancellationToken ct)
        {
            var processOutput = await RunProcessAsync("ffmpeg", ["-i", sourceFile], ct, ignoreExitCode: true);
            var durationMatch = DurationRegex.Match(processOutput);

            if (!durationMatch.Success)
            {
                throw new InvalidOperationException("Unable to determine audio duration for STT chunking.");
            }

            var hours = int.Parse(durationMatch.Groups["hours"].Value);
            var minutes = int.Parse(durationMatch.Groups["minutes"].Value);
            var seconds = double.Parse(durationMatch.Groups["seconds"].Value, System.Globalization.CultureInfo.InvariantCulture);

            return (hours * 3600) + (minutes * 60) + seconds;
        }

        private async Task<List<double>> DetectSilenceBoundariesAsync(string sourceFile, CancellationToken ct)
        {
            var processOutput = await RunProcessAsync(
                "ffmpeg",
                ["-i", sourceFile, "-af", "silencedetect=n=-35dB:d=0.5", "-f", "null", "-"],
                ct,
                ignoreExitCode: true);

            var boundaries = SilenceEndRegex.Matches(processOutput)
                .Select(match => match.Groups["seconds"].Value)
                .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            return boundaries;
        }

        private async Task<IReadOnlyList<TranscriptSegment>> SendTranscriptionRequestAsync(
            Guid participantUserId,
            MultipartFormDataContent multipartContent,
            long offsetMs,
            CancellationToken ct)
        {
            var baseUrl = NormalizeBaseUrl(_stt.BaseUrl);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/transcriptions")
            {
                Content = multipartContent
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _stt.ApiKey);

            using var client = _httpClientFactory.CreateClient("openai-stt");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);
            return ParseSegments(participantUserId, document.RootElement, offsetMs);
        }

        private static IReadOnlyList<TranscriptSegment> ParseSegments(
            Guid participantUserId,
            JsonElement responseRoot,
            long offsetMs)
        {
            var segments = new List<TranscriptSegment>();

            if (!responseRoot.TryGetProperty("segments", out var segmentsElement)
                || segmentsElement.ValueKind != JsonValueKind.Array)
            {
                return segments;
            }

            foreach (var segmentElement in segmentsElement.EnumerateArray())
            {
                if (!TryReadDouble(segmentElement, "start", out var startSeconds)
                    || !TryReadDouble(segmentElement, "end", out var endSeconds))
                {
                    continue;
                }

                var text = segmentElement.TryGetProperty("text", out var textElement)
                    && textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString() ?? string.Empty
                        : string.Empty;

                double? confidence = TryReadDouble(segmentElement, "avg_logprob", out var avgLogProb)
                    ? avgLogProb
                    : null;

                var startMs = offsetMs + (long)Math.Round(startSeconds * 1000);
                var endMs = offsetMs + (long)Math.Round(endSeconds * 1000);

                segments.Add(new TranscriptSegment(
                    participantUserId,
                    startMs,
                    endMs,
                    text,
                    confidence));
            }

            return segments;
        }

        private async Task<long> GetObjectSizeAsync(
            IMinioClient minioClient,
            string storageObjectKey,
            CancellationToken ct)
        {
            var stat = await minioClient.StatObjectAsync(
                new StatObjectArgs()
                    .WithBucket(_storage.Bucket)
                    .WithObject(storageObjectKey),
                ct);

            return stat.Size;
        }

        private async Task DownloadObjectToFileAsync(
            IMinioClient minioClient,
            string storageObjectKey,
            string localPath,
            CancellationToken ct)
        {
            await using var destination = File.OpenWrite(localPath);
            var getObjectArgs = new GetObjectArgs()
                .WithBucket(_storage.Bucket)
                .WithObject(storageObjectKey)
                .WithCallbackStream(stream => stream.CopyTo(destination));

            await minioClient.GetObjectAsync(getObjectArgs, ct);
        }

        private async Task<string> RunProcessAsync(
            string fileName,
            IReadOnlyList<string> args,
            CancellationToken ct,
            bool ignoreExitCode = false)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!ignoreExitCode && process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Process '{fileName}' failed with code {process.ExitCode}: {stderr}");
            }

            return string.Join(Environment.NewLine, stdout, stderr);
        }

        private IMinioClient CreateClient()
        {
            var endpoint = _storage.Endpoint?.Trim() ?? string.Empty;
            var secure = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            endpoint = endpoint
                .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

            return new MinioClient()
                .WithEndpoint(endpoint)
                .WithCredentials(_storage.AccessKey, _storage.SecretKey)
                .WithSSL(secure)
                .Build();
        }

        private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
        {
            value = 0;
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            if (property.ValueKind == JsonValueKind.Number)
            {
                return property.TryGetDouble(out value);
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(
                    property.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            }

            return false;
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return (baseUrl ?? string.Empty).TrimEnd('/');
        }

        private readonly record struct ChunkWindow(double StartSeconds, double EndSeconds);

        private sealed class MinioObjectContent(
            IMinioClient minioClient,
            string bucketName,
            string objectKey,
            CancellationToken cancellationToken) : HttpContent
        {
            private readonly IMinioClient _minioClient = minioClient;
            private readonly string _bucketName = bucketName;
            private readonly string _objectKey = objectKey;
            private readonly CancellationToken _cancellationToken = cancellationToken;

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                var getObjectArgs = new GetObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(_objectKey)
                    .WithCallbackStream(source => source.CopyTo(stream));

                await _minioClient.GetObjectAsync(getObjectArgs, _cancellationToken);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }
        }
    }
}

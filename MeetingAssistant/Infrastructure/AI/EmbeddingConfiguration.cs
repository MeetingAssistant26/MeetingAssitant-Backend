using MeetingAssistant.Features.LiveSession.Infrastructure;

namespace MeetingAssistant.Infrastructure.AI
{
    public static class EmbeddingConfiguration
    {
        public const string SectionName = "OpenAiCompatible:Embedding";

        public static bool IsValid(OpenAiCompatibleOptions.ProviderConfig? config) =>
            TryGetValidationError(config) is null;

        public static string? TryGetValidationError(OpenAiCompatibleOptions.ProviderConfig? config)
        {
            if (config is null)
            {
                return $"{SectionName} must be configured.";
            }

            var provider = NormalizeProvider(config.Provider);
            if (string.IsNullOrWhiteSpace(provider))
            {
                return $"{SectionName}:Provider must be configured. Set '{EmbeddingProviderNames.DeterministicTest}' explicitly for local/tests or '{EmbeddingProviderNames.OpenAiCompatible}' for a production embeddings endpoint.";
            }

            if (!IsSupportedProvider(provider))
            {
                return $"Unsupported {SectionName}:Provider '{config.Provider}'. Supported providers are '{EmbeddingProviderNames.DeterministicTest}' and '{EmbeddingProviderNames.OpenAiCompatible}'.";
            }

            if (string.IsNullOrWhiteSpace(config.Model))
            {
                return $"{SectionName}:Model must be configured so knowledge chunks record embedding metadata.";
            }

            if (!config.Dimension.HasValue || config.Dimension.Value <= 0)
            {
                return $"{SectionName}:Dimension must be greater than zero so vector length is explicit and queryable.";
            }

            if (provider == EmbeddingProviderNames.OpenAiCompatible)
            {
                if (string.IsNullOrWhiteSpace(config.BaseUrl))
                {
                    return $"{SectionName}:BaseUrl must be configured when Provider is '{EmbeddingProviderNames.OpenAiCompatible}'.";
                }

                if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return $"{SectionName}:BaseUrl must be an absolute HTTP(S) URI when Provider is '{EmbeddingProviderNames.OpenAiCompatible}'.";
                }
            }

            return null;
        }

        public static EmbeddingMetadata GetMetadata(OpenAiCompatibleOptions.ProviderConfig config)
        {
            ThrowIfInvalid(config);
            return new EmbeddingMetadata(
                NormalizeProvider(config.Provider),
                config.Model.Trim(),
                config.Dimension!.Value);
        }

        public static Uri GetOpenAiCompatibleEndpoint(OpenAiCompatibleOptions.ProviderConfig config)
        {
            ThrowIfInvalid(config);
            var provider = NormalizeProvider(config.Provider);
            if (provider != EmbeddingProviderNames.OpenAiCompatible)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:Provider must be '{EmbeddingProviderNames.OpenAiCompatible}' to call an embeddings API.");
            }

            return new Uri($"{config.BaseUrl.TrimEnd('/')}/embeddings", UriKind.Absolute);
        }

        public static void ThrowIfInvalid(OpenAiCompatibleOptions.ProviderConfig? config)
        {
            var error = TryGetValidationError(config);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }
        }

        public static string NormalizeProvider(string? provider) =>
            (provider ?? string.Empty).Trim().ToLowerInvariant();

        private static bool IsSupportedProvider(string provider) =>
            provider is EmbeddingProviderNames.DeterministicTest or EmbeddingProviderNames.OpenAiCompatible;
    }
}

namespace MeetingAssistant.Features.Organizations
{
    internal static class MeetingTagColor
    {
        public const string ValidationPattern = "^#?(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$";

        public static string? Normalize(string? color)
        {
            var trimmed = color?.Trim();

            if (string.IsNullOrEmpty(trimmed))
                return null;

            var hex = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;

            if (hex.Length == 3)
                hex = string.Concat(hex.Select(character => $"{character}{character}"));

            return $"#{hex.ToUpperInvariant()}";
        }
    }
}


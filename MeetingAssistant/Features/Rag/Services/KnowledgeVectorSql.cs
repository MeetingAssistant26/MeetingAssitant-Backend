using System.Globalization;

namespace MeetingAssistant.Features.Rag.Services
{
    public static class KnowledgeVectorSql
    {
        public static string ToVectorLiteral(IReadOnlyList<float> embedding)
        {
            if (embedding.Count == 0)
            {
                throw new ArgumentException("Embedding must contain at least one dimension.", nameof(embedding));
            }

            var values = new string[embedding.Count];
            for (var i = 0; i < embedding.Count; i++)
            {
                var value = embedding[i];
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    throw new ArgumentException("Embedding dimensions must be finite numbers.", nameof(embedding));
                }

                values[i] = value.ToString("R", CultureInfo.InvariantCulture);
            }

            return $"[{string.Join(',', values)}]";
        }
    }
}

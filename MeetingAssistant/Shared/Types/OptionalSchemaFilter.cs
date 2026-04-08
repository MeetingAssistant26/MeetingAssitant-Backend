using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MeetingAssistant.Shared.Types
{
    public class OptionalSchemaFilter : ISchemaFilter
    {
        public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
        {
            if (context.Type.IsGenericType && context.Type.GetGenericTypeDefinition() == typeof(Optional<>))
            {
                var innerType = context.Type.GetGenericArguments()[0];
                var innerSchema = context.SchemaGenerator.GenerateSchema(innerType, context.SchemaRepository);

                if (schema is OpenApiSchema openApiSchema && innerSchema is OpenApiSchema innerOpenApiSchema)
                {
                    openApiSchema.Type = innerOpenApiSchema.Type;
                    openApiSchema.Format = innerOpenApiSchema.Format;
                    openApiSchema.Properties = innerOpenApiSchema.Properties;
                    openApiSchema.Items = innerOpenApiSchema.Items;
                }
            }
        }
    }
}

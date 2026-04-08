using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingAssistant.Shared.Types;

[JsonConverter(typeof(OptionalJsonConverter))]
public readonly struct Optional<T>
{
    public bool HasValue { get; }
    public T? Value { get; }

    public Optional(T? value)
    {
        HasValue = true;
        Value = value;
    }

    public static implicit operator Optional<T>(T? value) => new Optional<T>(value);
    public static explicit operator T?(Optional<T> optional) => optional.Value;
}

public class OptionalJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsGenericType)
            return false;

        return typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];

        var converterType = typeof(OptionalConverterInner<>).MakeGenericType(valueType);

        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private class OptionalConverterInner<TValue> : JsonConverter<Optional<TValue>>
    {
        public override Optional<TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
            return new Optional<TValue>(value);
        }

        public override void Write(Utf8JsonWriter writer, Optional<TValue> value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                JsonSerializer.Serialize(writer, value.Value, options);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}

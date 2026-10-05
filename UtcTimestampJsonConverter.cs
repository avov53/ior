using System.Text.Json;
using System.Text.Json.Serialization;

namespace HappBox;

/// <summary>Legacy *Utc values without an offset are UTC; explicit local offsets retain their instant.</summary>
public sealed class UtcTimestampJsonConverter : JsonConverter<DateTime>
{
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => AsUtc(reader.GetDateTime());
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue(AsUtc(value));
}

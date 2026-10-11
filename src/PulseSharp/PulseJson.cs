using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PulseSharp;

internal sealed class PulseJson
{
    private JsonSerializerOptions _options = WithResolver(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public void Use(JsonSerializerOptions options)
    {
        _options = WithResolver(new JsonSerializerOptions(options));
    }

    public byte[] Serialize(object value)
    {
        return JsonSerializer.SerializeToUtf8Bytes(value, _options.GetTypeInfo(value.GetType()));
    }

    public T Deserialize<T>(byte[] data)
    {
        var typeInfo = (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T));
        return JsonSerializer.Deserialize(data, typeInfo)
            ?? throw new JsonException($"The stored data is null and can't become a {typeof(T).Name}.");
    }

    private static JsonSerializerOptions WithResolver(JsonSerializerOptions options)
    {
        options.TypeInfoResolver ??= DefaultResolver();
        return options;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The reflection resolver is used only when JsonSerializer.IsReflectionEnabledByDefault is true. Trimmed and Native AOT apps turn it off and pass a source-generated resolver through PulseSharpBuilder.JsonOptions."
    )]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The reflection resolver is used only when JsonSerializer.IsReflectionEnabledByDefault is true. Trimmed and Native AOT apps turn it off and pass a source-generated resolver through PulseSharpBuilder.JsonOptions."
    )]
    private static IJsonTypeInfoResolver DefaultResolver()
    {
        return JsonSerializer.IsReflectionEnabledByDefault
            ? new DefaultJsonTypeInfoResolver()
            : JsonTypeInfoResolver.Combine();
    }
}

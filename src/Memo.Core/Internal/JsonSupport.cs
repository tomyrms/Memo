using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memo.Core;

internal static class JsonSupport
{
    public static JsonSerializerOptions Options { get; } = Create(true);

    public static JsonSerializerOptions Compact { get; } = Create(false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

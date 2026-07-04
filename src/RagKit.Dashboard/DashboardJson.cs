using System.Text.Json;
using System.Text.Json.Serialization;

namespace RagKit.Dashboard;

/// <summary>Shared JSON options for the dashboard's API responses — camelCase
/// property names and string-valued enums (so <see cref="GuardrailStage"/> reads
/// as <c>"Input"</c>/<c>"Output"</c>, not <c>0</c>/<c>1</c>), independent of
/// whatever JSON options the host app itself has configured.</summary>
internal static class DashboardJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

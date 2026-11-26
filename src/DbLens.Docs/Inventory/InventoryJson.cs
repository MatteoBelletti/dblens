using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbLens.Docs.Inventory;

/// <summary>
/// The inventory as JSON, for the document generator and for a diff in a pull request: indented, no
/// timestamp, items and attributes already in order, so the same inventory is the same bytes.
/// </summary>
public static class InventoryJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(ProjectInventory inventory) => JsonSerializer.Serialize(inventory, Options) + "\n";
}

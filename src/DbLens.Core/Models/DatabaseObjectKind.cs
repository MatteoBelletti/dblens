using System.Text.Json.Serialization;

namespace DbLens.Core.Models;

/// <summary>Discriminates tables from views in schema overview results.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DatabaseObjectKind>))]
public enum DatabaseObjectKind
{
    [JsonStringEnumMemberName("table")]
    Table,
    [JsonStringEnumMemberName("view")]
    View,
}

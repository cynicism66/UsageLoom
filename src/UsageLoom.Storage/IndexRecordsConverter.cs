using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageLoom.Storage;

// Compatibility for the two user-confirmed entries written as objects by the repair tool.
// Normalize only this known shape; unrelated corruption must still fail visibly.
public sealed class IndexRecordsConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {
        using var document=JsonDocument.ParseValue(ref reader);
        if(document.RootElement.ValueKind!=JsonValueKind.Array)throw new JsonException("Invalid index records");
        var records=new List<string>();
        foreach(var row in document.RootElement.EnumerateArray())
        {
            if(row.ValueKind==JsonValueKind.String){records.Add(row.GetString()!);continue;}
            if(row.ValueKind==JsonValueKind.Object&&row.TryGetProperty("account_attribution",out var attribution)&&attribution.ValueKind==JsonValueKind.String&&attribution.GetString()=="user-confirmed"&&
                row.TryGetProperty("type",out var kind)&&kind.ValueKind==JsonValueKind.String&&kind.GetString()=="event_msg"&&
                row.TryGetProperty("payload",out var payload)&&payload.ValueKind==JsonValueKind.Object&&payload.TryGetProperty("type",out var eventType)&&eventType.ValueKind==JsonValueKind.String&&eventType.GetString()=="token_count")
            {records.Add(row.GetRawText());continue;}
            throw new JsonException("Unexpected non-string index record");
        }
        return records;
    }
    public override void Write(Utf8JsonWriter writer,List<string> value,JsonSerializerOptions options)
    {
        writer.WriteStartArray();foreach(var row in value)writer.WriteStringValue(row);writer.WriteEndArray();
    }
}

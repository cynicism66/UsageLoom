using System.Text.Json;

namespace UsageLoom.Core;

public sealed record UsageUncertainty(DateTimeOffset? From,DateTimeOffset? To)
{
    public bool IsUnknown=>From is null||To is null;
    public bool Overlaps(DateTimeOffset from,DateTimeOffset to)=>IsUnknown||To>from&&From<=to;
    public static UsageUncertainty FromRecords(IEnumerable<string> records)
    {
        var times=new List<DateTimeOffset>();
        try
        {
            foreach(var text in records)
            {
                using var doc=JsonDocument.Parse(text);var row=doc.RootElement;
                if(row.TryGetProperty("timestamp",out var timestamp)&&DateTimeOffset.TryParse(timestamp.GetString(),out var at))times.Add(at);
                else if(row.TryGetProperty("type",out var type)&&type.GetString()=="event_msg"&&
                    row.GetProperty("payload").TryGetProperty("type",out var kind)&&kind.GetString()=="token_count")return new(null,null);
            }
        }
        catch(Exception ex)when(ex is JsonException or InvalidOperationException or KeyNotFoundException){return new(null,null);}
        return times.Count==0?new(null,null):new(times.Min(),times.Max());
    }
}

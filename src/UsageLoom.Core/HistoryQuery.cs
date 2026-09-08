namespace UsageLoom.Core;

public sealed record HistoryFilter(DateOnly? From=null,DateOnly? Through=null,string Search="",string? Day=null);
public enum HistoryRangeKind { Day, Rolling7Days, Week, Month, Custom, All }
public sealed record HistoryDateRange(DateOnly? From,DateOnly? Through,string Label)
{
    public bool IsBounded=>From is not null&&Through is not null;
    public bool IsSingleDay=>IsBounded&&From==Through;
}
public sealed record HistoryTrendBucket(DateOnly From,DateOnly Through,string Label,long Tokens,int Requests,decimal EstimatedCost);
public sealed record SessionSummary(string Session,string Name,string Project,long Tokens,DateTimeOffset? LastActivity,IReadOnlyList<UsageEvent> Events)
{
    public string ShortId=>Session.Length<=12?Session:Session[..8]+"…";
}
public static class HistoryQuery
{
    public static HistoryDateRange ResolveRange(HistoryRangeKind kind,DateOnly today,DateOnly? customFrom=null,DateOnly? customThrough=null)
    {
        return kind switch
        {
            HistoryRangeKind.Day=>new(today,today,L10n.T("sD5F5A7A01073")),
            HistoryRangeKind.Rolling7Days=>new(today.AddDays(-6),today,L10n.T("s2261B06712A3")),
            HistoryRangeKind.Week=>new(today.AddDays(-(((int)today.DayOfWeek+6)%7)),today,L10n.T("sB4C6C3EB0BCE")),
            HistoryRangeKind.Month=>new(new DateOnly(today.Year,today.Month,1),today,L10n.T("s0EEECD26F2BA")),
            HistoryRangeKind.Custom when customFrom is {} from&&customThrough is {} through&&from<=through=>new(from,through,L10n.F("s614210C5CD54", from, through)),
            HistoryRangeKind.Custom=>new(null,null,L10n.T("s62B0452FA255")),
            _=>new(null,null,L10n.T("s9713A5277376"))
        };
    }
    public static List<HistoryTrendBucket> HourlyTrend(IEnumerable<UsageEvent> events,DateOnly day)
    {
        var rows=events.Where(item=>item.LocalDate==day.ToString("yyyy-MM-dd")).ToList();
        var grouped=rows.GroupBy(item=>item.Timestamp is {} stamp&&DateOnly.FromDateTime(stamp.LocalDateTime)==day?stamp.LocalDateTime.Hour:-1)
            .ToDictionary(group=>group.Key,group=>group.ToList());
        var buckets=new List<HistoryTrendBucket>();
        foreach(var hour in Enumerable.Range(0,24).Concat(grouped.ContainsKey(-1)?new[]{-1}:Array.Empty<int>()))
        {
            var items=grouped.GetValueOrDefault(hour)??[];
            buckets.Add(new(day,day,hour<0?L10n.T("s664939A1FA2E"):$"{hour:00}:00",items.Sum(item=>item.Tokens.Total),items.Count,Pricing.Summarize(items).Cost));
        }
        return buckets;
    }
    public static List<HistoryTrendBucket> Trend(IEnumerable<UsageEvent> events,HistoryDateRange range,int maximumBuckets=60)
    {
        if(maximumBuckets<1)throw new ArgumentOutOfRangeException(nameof(maximumBuckets));
        var dated=events.Select(item=>(Item:item,Valid:DateOnly.TryParseExact(item.LocalDate,"yyyy-MM-dd",out var day),Day:DateOnly.TryParseExact(item.LocalDate,"yyyy-MM-dd",out var parsed)?parsed:default))
            .Where(item=>item.Valid).ToList();
        if(dated.Count==0)return [];
        var from=range.From??dated.Min(item=>item.Day);var through=range.Through??dated.Max(item=>item.Day);
        if(through<from)return [];
        var totalDays=through.DayNumber-from.DayNumber+1;
        var bucketDays=Math.Max(1,(int)Math.Ceiling(totalDays/(double)maximumBuckets));
        var buckets=new List<HistoryTrendBucket>((totalDays+bucketDays-1)/bucketDays);
        for(var start=from;start<=through;start=start.AddDays(bucketDays))
        {
            var end=start.AddDays(bucketDays-1);if(end>through)end=through;
            var items=dated.Where(item=>item.Day>=start&&item.Day<=end).Select(item=>item.Item).ToList();
            var label=start==end?$"{start:MM/dd}":$"{start:MM/dd}–{end:MM/dd}";
            // One persisted usage event is one locally observable, token-bearing
            // completed request. Failed/cancelled/zero-token network attempts are
            // absent from Codex session logs and therefore cannot be reconstructed.
            buckets.Add(new(start,end,label,items.Sum(item=>item.Tokens.Total),items.Count,Pricing.Summarize(items).Cost));
        }
        return buckets;
    }
    public static string SessionName(string session,IReadOnlyDictionary<string,string>? names)
    {
        if(names is not null&&names.TryGetValue(session,out var title)&&!string.IsNullOrWhiteSpace(title))return title;
        return Guid.TryParse(session,out _)||session.Length>24?L10n.T("sA91AE6035092"):session;
    }
    public static List<UsageEvent> Filter(IEnumerable<UsageEvent> events,HistoryFilter filter,IReadOnlyDictionary<string,string>? names=null)
    {
        var query=(filter.Search??"").Trim();
        return events.Where(e=>
        {
            if(filter.Day is not null&&e.LocalDate!=filter.Day)return false;
            if(filter.From is not null||filter.Through is not null)
            {
                if(!DateOnly.TryParseExact(e.LocalDate,"yyyy-MM-dd",out var date))return false;
                if(filter.From is {} from&&date<from||filter.Through is {} through&&date>through)return false;
            }
            return query.Length==0||new[]{e.Session,SessionName(e.Session,names),e.Project,e.Model,e.Agent}.Any(v=>v.Contains(query,StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }
    public static List<SessionSummary> Sessions(IEnumerable<UsageEvent> events,string order="tokens",IReadOnlyDictionary<string,string>? names=null)
    {
        var groups=events.GroupBy(e=>e.Session).Select(g=>new SessionSummary(g.Key,SessionName(g.Key,names),g.First().Project,g.Sum(e=>e.Tokens.Total),g.Max(e=>e.Timestamp),g.OrderBy(e=>e.Timestamp).ThenBy(e=>e.Id).ToList()));
        return (order switch
        {
            "recent"=>groups.OrderByDescending(g=>g.LastActivity).ThenBy(g=>g.Session,StringComparer.Ordinal),
            "name"=>groups.OrderBy(g=>g.Name,StringComparer.OrdinalIgnoreCase).ThenBy(g=>g.Session,StringComparer.Ordinal),
            _=>groups.OrderByDescending(g=>g.Tokens).ThenBy(g=>g.Session,StringComparer.Ordinal)
        }).ToList();
    }
}

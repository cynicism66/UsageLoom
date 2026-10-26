using System.Text.Json;

namespace UsageLoom.Core;

/// <summary>
/// Reads the rate-limit snapshot written by the bundled Claude Code status line relay
/// (usageloom-statusline.js). Claude Code passes these percentages only to its status
/// line command, so this file is the sole local copy. No transcript, network or credential access.
/// </summary>
public static class ClaudeStatusLineReader
{
    public const string FileName="usageloom-rate-limits.json",ScriptName="usageloom-statusline.js";
    private const int MaxBytes=16384;

    public static string SnapshotPath(string? home)=>Path.Combine(string.IsNullOrWhiteSpace(home)?ClaudeCodeReader.DefaultHome:home.Trim(),FileName);

    public static ClaudeQuotaSnapshot Read(string? home,DateTimeOffset now)
    {
        try
        {
            home=string.IsNullOrWhiteSpace(home)?ClaudeCodeReader.DefaultHome:home.Trim();
            if(!Path.IsPathFullyQualified(home)||home.StartsWith(@"\\"))return ClaudeQuotaSnapshot.Empty("statusLine.missing");
            var file=new FileInfo(Path.Combine(home,FileName));
            if(!file.Exists)return ClaudeQuotaSnapshot.Empty("statusLine.missing");
            // Never follow junctions/symlinks, including the Claude home and its ancestors.
            if((file.Attributes&FileAttributes.ReparsePoint)!=0)return ClaudeQuotaSnapshot.Empty("statusLine.invalid");
            for(var parent=file.Directory;parent is not null;parent=parent.Parent)
                if((parent.Attributes&FileAttributes.ReparsePoint)!=0)return ClaudeQuotaSnapshot.Empty("statusLine.invalid");
            if(file.Length>MaxBytes)return ClaudeQuotaSnapshot.Empty("statusLine.invalid");
            byte[] data;
            using(var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            {
                data=new byte[Math.Min(stream.Length,MaxBytes+1)];
                var read=0;while(read<data.Length&&stream.Read(data,read,data.Length-read) is var n&&n>0)read+=n;
                if(read>MaxBytes)return ClaudeQuotaSnapshot.Empty("statusLine.invalid");
                Array.Resize(ref data,read);
            }
            return Parse(data,ClaudeQuotaParser.ScopeKey(home,"claude-code-statusline"),now);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or FormatException or ArgumentException)
        {return ClaudeQuotaSnapshot.Empty("statusLine.invalid");}
    }

    public static ClaudeQuotaSnapshot Parse(byte[] data,string scope,DateTimeOffset now)
    {
        using var doc=JsonDocument.Parse(data,new JsonDocumentOptions{MaxDepth=8});
        var root=doc.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("version",out var version)||version.ValueKind!=JsonValueKind.Number||version.GetInt32()!=1)
            return ClaudeQuotaSnapshot.Empty("statusLine.invalid");
        if(!root.TryGetProperty("captured_at",out var captured)||captured.ValueKind!=JsonValueKind.Number||!captured.TryGetInt64(out var ms))
            throw new InvalidDataException("Missing capture time");
        var at=DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if(at>now.AddMinutes(2))throw new InvalidDataException("Future snapshot");
        // Same retention as other Claude quota sources: older files are not a current source.
        if(at<now.AddDays(-31))return ClaudeQuotaSnapshot.Empty("statusLine.missing");
        var windows=new List<ClaudeQuotaWindow>();
        foreach(var (key,duration) in new[]{("five_hour",TimeSpan.FromHours(5)),("seven_day",TimeSpan.FromDays(7))})
        {
            if(!root.TryGetProperty(key,out var value)||value.ValueKind==JsonValueKind.Null)continue;
            if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty("used_percentage",out var usage)||
                usage.ValueKind!=JsonValueKind.Number||!usage.TryGetDouble(out var used)||!double.IsFinite(used)||used<0||used>100)
                throw new InvalidDataException("Invalid utilization");
            DateTimeOffset? reset=null;
            if(value.TryGetProperty("resets_at",out var time)&&time.ValueKind!=JsonValueKind.Null)
            {
                if(time.ValueKind!=JsonValueKind.Number||!time.TryGetDouble(out var seconds)||!double.IsFinite(seconds)||seconds<=0||seconds>253402300799)
                    throw new InvalidDataException("Invalid reset");
                var parsed=DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds*1000));
                if(parsed>at+duration+TimeSpan.FromMinutes(5)||parsed<at.AddDays(-8))throw new InvalidDataException("Invalid reset");
                reset=parsed;
            }
            windows.Add(new(key,used,reset));
        }
        if(windows.Count==0)throw new InvalidDataException("No windows");
        return new ClaudeQuotaSnapshot("snapshot",windows,at,scope){Source="statusline"};
    }

    /// <summary>
    /// Picks the newer of the desktop cache and the status line snapshot as a whole; windows from
    /// different sources are never mixed. The desktop scope list stays available for its settings.
    /// </summary>
    public static ClaudeQuotaSnapshot Choose(ClaudeQuotaSnapshot desktop,ClaudeQuotaSnapshot statusLine)
    {
        if(statusLine.Status!="snapshot")return desktop;
        if(desktop.Status=="snapshot"&&desktop.ObservedAt>=statusLine.ObservedAt)return desktop;
        return statusLine with{Scopes=desktop.Scopes};
    }
}

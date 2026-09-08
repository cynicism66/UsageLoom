using System.Text.Json;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed class Settings
{
    public string? CliPath { get; set; }
    // Retired UI mode: ignore legacy JSON values and never persist it again.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ReuseBackend { get => false; set { } }
    public bool AuthorizedAccount { get; set; }
    public string CodexHome { get; set; } = Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
    public bool AutoRefresh { get; set; }=true;
    public bool CapacityEnabled { get; set; }=true;
    public int BackgroundSeconds { get; set; }=300;
    public int ForegroundSeconds { get; set; }=30;
    public bool LowNotify { get; set; }
    public bool ResetNotify { get; set; }
    public int LowPercent { get; set; }=20;
    public int ResetMinutes { get; set; }=10;
    public string Theme { get; set; }="Default";
    public string Language { get; set; }="zh-CN";
    public bool AppearanceConfigured { get; set; }
    public HashSet<string> SentNotifications { get; set; }=[];
    public List<WindowNotificationState> NotificationWindows { get; set; } = [];
    public static Settings Load()
    {
        try
        {
            var p=Path.Combine(Program.DataPath,"settings.json");
            var settings=File.Exists(p)?JsonSerializer.Deserialize<Settings>(File.ReadAllText(p))??new():new();
            settings.BackgroundSeconds=Math.Clamp(settings.BackgroundSeconds,30,3600);
            settings.ForegroundSeconds=Math.Clamp(settings.ForegroundSeconds,15,3600);
            settings.LowPercent=Math.Clamp(settings.LowPercent,1,99);
            settings.ResetMinutes=Math.Clamp(settings.ResetMinutes,1,120);
            settings.SentNotifications??=[];
            settings.NotificationWindows??=[];
            if(string.IsNullOrWhiteSpace(settings.CodexHome))settings.CodexHome=new Settings().CodexHome;
            if(settings.Theme is not ("Default" or "Light" or "Dark"))settings.Theme="Default";
            if(settings.Language is not ("zh-CN" or "en-US"))settings.Language="zh-CN";
            // 0.3.8 and earlier stored the old hard-coded dark default, so an
            // upgrade would otherwise remain dark even after the default changed.
            if(!settings.AppearanceConfigured&&settings.Theme=="Dark")settings.Theme="Default";
            return settings;
        }
        catch(Exception ex)when(ex is JsonException or IOException or UnauthorizedAccessException){Program.Log.Write("WARN","Settings","设置读取失败，使用默认值");return new();}
    }
    public void Save()
    {
        Directory.CreateDirectory(Program.DataPath);
        var temporary=Path.Combine(Program.DataPath,"settings.tmp");
        File.WriteAllText(temporary,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temporary,Path.Combine(Program.DataPath,"settings.json"),true);
    }
}

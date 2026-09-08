using System.Globalization;
using System.Text.Json;

namespace UsageLoom.Core;

/// <summary>Presentation resources only. Never use localized text as a persisted identity or protocol key.</summary>
public static class L10n
{
    private static readonly Dictionary<string,string> Chinese=Load("zh-CN");
    private static readonly Dictionary<string,string> English=Load("en-US");
    public static string Language { get; set; }="zh-CN";
    public static string T(string key)=>(Language=="en-US"?English:Chinese).GetValueOrDefault(key)??Chinese.GetValueOrDefault(key)??key;
    public static string F(string key,params object?[] values)=>string.Format(CultureInfo.CurrentCulture,T(key),values);
    private static Dictionary<string,string> Load(string language)
    {
        using var stream=typeof(L10n).Assembly.GetManifestResourceStream($"UsageLoom.Core.Localization.{language}.json")??throw new InvalidOperationException("Missing language resource: "+language);
        return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)??throw new InvalidOperationException("Empty language resource: "+language);
    }
}

using System.Globalization;
namespace UsageLoom.Core;
public static class UsageNumbers
{
    public static string Compact(double value)
    {
        if(!double.IsFinite(value)||value<0)return "—";
        var scale=value>=999_995_000?1e9:value>=999_995?1e6:value>=999.995?1e3:1;
        return (value/scale).ToString(scale==1?"N0":"0.##",CultureInfo.InvariantCulture)+(scale==1e9?"B":scale==1e6?"M":scale==1e3?"K":"");
    }
}

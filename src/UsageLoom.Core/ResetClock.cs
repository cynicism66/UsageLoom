using System.Globalization;

namespace UsageLoom.Core;

// The local clock time a window resets: "today 14:30", "tomorrow 05:00", or "11-16 05:00" further out.
public static class ResetClock
{
    public static string Format(DateTimeOffset reset,DateTimeOffset now)
    {
        var local=reset.ToLocalTime();var days=(local.Date-now.ToLocalTime().Date).Days;
        var day=days switch{0=>L10n.T("reset.today"),1=>L10n.T("reset.tomorrow"),_=>local.ToString("MM-dd",CultureInfo.InvariantCulture)};
        return L10n.F("reset.at",day,local.ToString("HH:mm",CultureInfo.InvariantCulture));
    }
}

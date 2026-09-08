using System.Security.Cryptography;
using System.Text;

namespace UsageLoom.Core;

public sealed record NotificationOptions(bool LowEnabled = false, bool ResetEnabled = false, double LowPercent = 20, int ResetMinutes = 10);
public sealed record NotificationDecision(string Key, string Title, string Body);
public sealed record WindowNotificationState(string Account, string Window, DateTimeOffset CycleEnd, bool LowSent, bool ResetSent);

/// <summary>纯数据策略；不会发起查询，不保留原始账号标识。</summary>
public sealed class NotificationPolicy
{
    private readonly Dictionary<string, WindowNotificationState> windows = new(StringComparer.Ordinal);
    public IReadOnlyList<WindowNotificationState> Export() => windows.Values.ToList();

    public void Restore(IEnumerable<WindowNotificationState>? saved, DateTimeOffset now)
    {
        windows.Clear();
        foreach (var item in saved ?? [])
        {
            if (item.CycleEnd <= now || string.IsNullOrEmpty(item.Account) || item.Account.Length != 64 || string.IsNullOrEmpty(item.Window) || item.Window.Length > 256) continue;
            if (windows.Count >= 128) break;
            windows[item.Account + ":" + item.Window] = item;
        }
    }

    public List<NotificationDecision> Evaluate(QuotaState quota, NotificationOptions options, DateTimeOffset now, TimeSpan maxAge)
    {
        var result = new List<NotificationDecision>();
        if (!quota.Fresh || string.IsNullOrWhiteSpace(quota.AccountKey) || quota.FetchedAt is not {} fetched ||
            fetched > now || now - fetched > maxAge || maxAge <= TimeSpan.Zero) return result;
        if (!double.IsFinite(options.LowPercent) || options.LowPercent is < 0 or > 100 || options.ResetMinutes <= 0) return result;
        var account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(quota.AccountKey)));
        foreach (var old in windows.Where(p => p.Value.CycleEnd < now.AddDays(-1)).Select(p => p.Key).ToList()) windows.Remove(old);
        foreach (var window in quota.PrimaryWindows)
        {
            if (window.ResetsAt is not {} reset || reset <= now || !double.IsFinite(window.Used) || window.Used is < 0 or > 100 || window.Key.Length > 256) continue;
            var key = account + ":" + window.Key;
            if (!windows.TryGetValue(key, out var state))
            {
                if (windows.Count >= 128) continue;
                state = new(account, window.Key, reset, false, false);
            }
            // 周期尚未结束时的服务端时间修正不能重新触发；到期后须看到新的未来周期。
            else if (state.CycleEnd <= now && reset > state.CycleEnd.AddSeconds(90)) state = new(account, window.Key, reset, false, false);
            else if (reset > state.CycleEnd) state = state with { CycleEnd = reset };
            if (options.LowEnabled && !state.LowSent && window.Remaining <= options.LowPercent)
            {
                result.Add(new(key + ":low", L10n.T("s29699203C5E8"), L10n.F("sF046F7443A51", window.Label, window.Remaining)));
                state = state with { LowSent = true };
            }
            if (options.ResetEnabled && !state.ResetSent && reset - now <= TimeSpan.FromMinutes(options.ResetMinutes))
            {
                result.Add(new(key + ":reset", L10n.T("s128BCFD4AA85"), L10n.F("s947E67403276", window.Label, reset.ToLocalTime())));
                state = state with { ResetSent = true };
            }
            windows[key] = state;
        }
        return result;
    }
}

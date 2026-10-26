using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    private void UpdateTrayQuota()
    {
        if(quitting||tray is null)return;
        tray.Update(TrayQuotaSelection.Select(Config.TrayQuotaSource,CodexShown,ClaudeShown,
            Quota,ClaudeQuota,DateTimeOffset.UtcNow),Config.UseFixedTrayIcon);
    }
}

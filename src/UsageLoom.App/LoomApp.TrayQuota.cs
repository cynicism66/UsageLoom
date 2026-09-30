using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    private void UpdateTrayQuota()
    {
        if(quitting||tray is null)return;
        tray.Update(TrayQuotaSelection.Select(Config.TrayQuotaSource,Config.CodexEnabled,Config.ClaudeEnabled,
            Quota,ClaudeQuota,DateTimeOffset.UtcNow),Config.UseFixedTrayIcon);
    }
}

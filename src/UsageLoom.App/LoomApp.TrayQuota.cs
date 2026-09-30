using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    private TaskbarQuotaStrip? taskbarStrip;
    private void UpdateTrayQuota()
    {
        if(quitting||tray is null)return;
        var state=TrayQuotaSelection.Select(Config.TrayQuotaSource,Config.CodexEnabled,Config.ClaudeEnabled,
            Quota,ClaudeQuota,DateTimeOffset.UtcNow);
        tray.Update(state,Config.UseFixedTrayIcon);
        if(!Config.TaskbarStripEnabled)
        {
            taskbarStrip?.Dispose();taskbarStrip=null;
            return;
        }
        if(taskbarStrip?.Faulted==true)return;
        try
        {
            taskbarStrip??=new TaskbarQuotaStrip(queue,OpenPanelFromTaskbarStrip);
            taskbarStrip.Update(state);
        }
        catch(Exception ex)
        {
            Program.Log.Write("ERROR","TaskbarStrip",ex.Message);
            taskbarStrip?.Dispose();taskbarStrip=null;
            Config.TaskbarStripEnabled=false;
        }
    }
    private void OpenPanelFromTaskbarStrip()
    {
        flyout??=new Dashboard(this,true);
        flyout.ShowPanel();
    }
}

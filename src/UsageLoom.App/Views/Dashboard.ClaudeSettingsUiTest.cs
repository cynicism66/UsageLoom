using Microsoft.UI.Xaml.Controls;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private async Task VerifyClaudeSettingsAsync()
    {
        try
        {
            await CapacityTestDispatcherSettled(this);
            var fixtureDirectory=Path.Combine(Program.DataPath,"Claude fixture");
            void CheckSaved(bool enabled,string? directory,string? scope)
            {
                var saved=Settings.Load();
                if(app.Config.ClaudeEnabled!=enabled||saved.ClaudeEnabled!=enabled||saved.ClaudeDataDirectory!=directory||saved.ClaudeScope!=scope)
                    throw new InvalidOperationException("Claude settings did not persist correctly");
            }
            void Reenter(bool enabled)
            {
                Navigate("overview");Navigate("settings");
                if(claudeEnabledChoice?.IsOn!=enabled)throw new InvalidOperationException("Claude toggle reverted after navigation");
            }
            if(app.ClaudeSettingsRestartCheck)
            {
                CheckSaved(true,fixtureDirectory,null);
                if(claudeEnabledChoice?.IsOn!=true||claudeDirectoryChoice?.Text!=fixtureDirectory)throw new InvalidOperationException("Claude settings reverted after process restart");
                Program.Log.Write("INFO","ClaudeSettingsTest","Claude settings process restart passed");
                return;
            }
            var eventCount=app.Events.Count;var source=app.Config.CodexHome;
            claudeEnabledChoice!.IsOn=true;
            claudeScopeChoice!.Items.Add(new ComboBoxItem{Content="Fixture",Tag="fixture-scope"});
            claudeScopeChoice.SelectedIndex=claudeScopeChoice.Items.Count-1;
            claudeScopesKey="force-test-refresh";UpdateClaudeViews();
            if((claudeScopeChoice.SelectedItem as ComboBoxItem)?.Tag as string!="fixture-scope")throw new InvalidOperationException("Refresh overwrote unsaved source choice");
            CapacityTestInvoke(settingsSaveButton!);
            await CapacityTestWait(()=>app.Config.ClaudeEnabled&&settingsSaveButton!.IsEnabled,"Global settings save did not enable Claude");
            CheckSaved(true,null,"fixture-scope");Reenter(true);
            claudeEnabledChoice!.IsOn=false;
            CapacityTestInvoke(claudeApplyButton!);
            await CapacityTestWait(()=>!app.Config.ClaudeEnabled&&claudeApplyButton!.IsEnabled,"Section save did not disable Claude");
            CheckSaved(false,null,"fixture-scope");Reenter(false);
            claudeEnabledChoice!.IsOn=true;claudeDirectoryChoice!.Text="  "+fixtureDirectory+"  ";
            foreach(ComboBoxItem item in claudeScopeChoice!.Items)if((string)item.Tag=="fixture-scope")claudeScopeChoice.SelectedItem=item;
            CapacityTestInvoke(settingsSaveButton!);
            await CapacityTestWait(()=>app.Config.ClaudeEnabled&&settingsSaveButton!.IsEnabled,"Global save did not persist changed directory");
            CheckSaved(true,fixtureDirectory,null);
            if(readClaudeSettings!().Scope is not null)throw new InvalidOperationException("Old directory scope returned on a repeated save");
            Reenter(true);
            var snapshot=app.ClaudeQuota;
            CapacityTestInvoke(settingsSaveButton!);
            await CapacityTestDispatcherSettled(this);
            if(!ReferenceEquals(snapshot,app.ClaudeQuota))throw new InvalidOperationException("Unchanged settings reset the Claude snapshot");
            try{await app.ConfigureClaudeAsync(new(false,"relative-path",null));throw new InvalidOperationException("Invalid directory accepted");}
            catch(ArgumentException){CheckSaved(true,fixtureDirectory,null);}
            // Isolated preview directory only: provoke an atomic settings write failure.
            var temporary=Path.Combine(Program.DataPath,"settings.tmp");
            Directory.CreateDirectory(temporary);
            try
            {
                try{await app.ConfigureClaudeAsync(new(false,fixtureDirectory,null));throw new InvalidOperationException("Failed settings write was accepted");}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){CheckSaved(true,fixtureDirectory,null);}
            }
            finally{Directory.Delete(temporary);}
            if(app.Events.Count!=eventCount||app.Config.CodexHome!=source)throw new InvalidOperationException("Claude save changed Codex history or source");
            Program.Log.Write("INFO","ClaudeSettingsTest","Claude global/section save, navigation, source draft, validation and write rollback passed");
        }
        catch(Exception ex){Program.Log.Write("ERROR","ClaudeSettingsTest",ex.ToString());}
    }
}

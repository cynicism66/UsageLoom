using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private async Task VerifyClaudeSettingsAsync()
    {
        try
        {
            await CapacityTestDispatcherSettled(this);
            if(System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"ClaudeEnabled\":true}")?.CodexEnabled!=true)
                throw new InvalidOperationException("Legacy settings disabled Codex during migration");
            void CheckGrouping()
            {
                if(dataSourcesSettings?.Content is not StackPanel sources||claudeSettingsPanel is null||sources.Children.OfType<Border>().SingleOrDefault(card=>card.Child==claudeSettingsPanel) is not {} claudeCard)
                    throw new InvalidOperationException("Claude settings are not inside Data sources");
                if(codexSettingsPanel is null||sources.Children.OfType<Border>().SingleOrDefault(card=>card.Child==codexSettingsPanel) is not {} codexCard||codexSettingsPanel.Children[1]!=codexEnabledChoice||claudeSettingsPanel.Children[1]!=claudeEnabledChoice||!Equals(codexEnabledChoice!.Header,claudeEnabledChoice!.Header))
                    throw new InvalidOperationException("Provider settings formats or enable controls differ");
                if(ReferenceEquals(codexCard,claudeCard)||sources.Spacing<12||sources.Children.IndexOf(codexCard)>=sources.Children.IndexOf(claudeCard))
                    throw new InvalidOperationException("Provider setting cards are not independently separated");
                foreach(var card in new[]{codexCard,claudeCard})
                    if(card.BorderThickness.Left<1||card.CornerRadius.TopLeft<8||card.Padding.Left<16||card.Background is null||card.BorderBrush is null)
                        throw new InvalidOperationException("Provider setting card lost its border, spacing or theme brushes");
                if(sources.Children.OfType<Expander>().Any()||((StackPanel)scrollContent.Content).Children.OfType<Expander>().Any(e=>Equals(e.Header,L10n.T("claude.title"))))
                    throw new InvalidOperationException("Claude settings retained a separate expander");
                dataSourcesSettings.IsExpanded=true;dataSourcesSettings.UpdateLayout();
                if(!StableExpander.HasVisibleContent(dataSourcesSettings))throw new InvalidOperationException("Data sources did not reveal Claude settings");
            }
            CheckGrouping();
            Program.Log.Write("INFO","ClaudeSettingsTest","Claude settings contained in Data sources without nested expander passed");
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
                CheckGrouping();
                if(claudeEnabledChoice?.IsOn!=enabled)throw new InvalidOperationException("Claude toggle reverted after navigation");
            }
            if(app.ClaudeSettingsRestartCheck)
            {
                CheckSaved(true,fixtureDirectory,null);
                if(app.Config.ClaudeManualPlan!="pro"||(claudePlanChoice?.SelectedItem as ComboBoxItem)?.Tag as string!="pro")throw new InvalidOperationException("Claude manual plan reverted on restart");
                if(!app.Config.ClaudeCodeEnabled||app.Config.ClaudeCodeHome!=Path.Combine(Program.DataPath,"code-fixture"))throw new InvalidOperationException("Claude Code settings reverted on restart");
                if(app.Config.CodexEnabled||codexEnabledChoice!.IsOn)throw new InvalidOperationException("Disabled Codex source reverted on restart");
                await app.VerifyDisabledSourceAsync();
                Program.Log.Write("INFO","ClaudeSettingsTest","Data sources independent switches, stop guards, retained history and safe boundary passed");
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
            var retainedEvents=app.Events;
            codexEnabledChoice!.IsOn=false;
            CapacityTestInvoke(settingsSaveButton!);
            await CapacityTestWait(()=>!app.Config.CodexEnabled&&settingsSaveButton!.IsEnabled,"Global save did not disable Codex");
            await app.VerifyDisabledSourceAsync();
            Reenter(true);
            if(codexEnabledChoice!.IsOn||Settings.Load().CodexEnabled)throw new InvalidOperationException("Codex disable not persisted");
            Navigate("overview");
            if(overviewQuotaCard.Visibility!=Visibility.Collapsed||claudeOverview is null)throw new InvalidOperationException("Claude-only overview not isolated");
            var small=app.DataSourceTestFlyout!.compactContent!;
            if(small.QuotaCard.Visibility!=Visibility.Collapsed||small.ClaudeCard.Visibility!=Visibility.Visible)
                throw new InvalidOperationException("Claude-only compact view not isolated");
            Navigate("settings");CheckGrouping();
            codexEnabledChoice!.IsOn=true;CapacityTestInvoke(codexApplyButton!);
            await CapacityTestWait(()=>app.CodexActive&&codexApplyButton!.IsEnabled,"Section save did not enable Codex");
            claudeEnabledChoice!.IsOn=false;CapacityTestInvoke(claudeApplyButton!);
            await CapacityTestWait(()=>!app.Config.ClaudeEnabled&&claudeApplyButton!.IsEnabled,"Codex-only mode failed");
            if(!app.CodexActive||small.QuotaCard.Visibility!=Visibility.Visible||small.ClaudeCard.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("Codex-only compact view not isolated");
            codexEnabledChoice!.IsOn=false;CapacityTestInvoke(settingsSaveButton!);
            await CapacityTestWait(()=>!app.Config.CodexEnabled&&settingsSaveButton!.IsEnabled,"Both-disabled mode failed");
            if(small.Disabled.Visibility!=Visibility.Visible||small.QuotaCard.Visibility!=Visibility.Collapsed||small.ClaudeCard.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("Both-disabled guidance missing");
            Directory.CreateDirectory(temporary);
            try
            {
                app.Config.CodexHome=Path.Combine(Program.DataPath,"uncommitted-source");
                try{await app.SaveSettingsAsync(new(true,fixtureDirectory,null),true);throw new Exception("Failed provider save accepted");}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){if(app.Config.CodexEnabled||app.Config.ClaudeEnabled||app.Config.CodexHome!=source)throw new InvalidOperationException("Provider switches or source path did not roll back");}
            }
            finally{Directory.Delete(temporary);}
            await app.VerifyPendingSourceBoundaryAsync();
            await app.ConfigureClaudeAsync(new(true,fixtureDirectory,null));
            Reenter(true);
            void ChoosePlan(string value)
            {
                foreach(ComboBoxItem item in claudePlanChoice!.Items)if((string)item.Tag==value)claudePlanChoice.SelectedItem=item;
            }
            ChoosePlan("pro");
            snapshot=app.ClaudeQuota;
            CapacityTestInvoke(settingsSaveButton!);
            await CapacityTestWait(()=>app.Config.ClaudeManualPlan=="pro"&&settingsSaveButton!.IsEnabled,"Global save did not persist manual plan");
            if(Settings.Load().ClaudeManualPlan!="pro"||!ReferenceEquals(snapshot,app.ClaudeQuota))throw new InvalidOperationException("Display-only plan reset snapshot or did not persist");
            Reenter(true);ChoosePlan("max20");
            CapacityTestInvoke(claudeApplyButton!);
            await CapacityTestWait(()=>app.Config.ClaudeManualPlan=="max20"&&claudeApplyButton!.IsEnabled,"Section save did not persist manual plan");
            if(Settings.Load().ClaudeManualPlan!="max20")throw new InvalidOperationException("Section plan not saved");
            Directory.CreateDirectory(temporary);
            try
            {
                try{await app.ConfigureClaudeAsync(new(true,fixtureDirectory,null,"pro"));throw new InvalidOperationException("Plan write failure accepted");}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){if(app.Config.ClaudeManualPlan!="max20"||Settings.Load().ClaudeManualPlan!="max20")throw new InvalidOperationException("Plan write failure did not roll back");}
            }
            finally{Directory.Delete(temporary);}
            await app.ConfigureClaudeAsync(new(true,fixtureDirectory,null,"pro"));
            Navigate("quota");
            if(claudeDetails?.Children[0] is not Grid planHeader||planHeader.Children[1] is not TextBlock planText||planText.Text!=ClaudePlanLabel.Badge("pro")||small.ClaudePlan.Text!=planText.Text)
                throw new InvalidOperationException("Claude manual plan is missing or inconsistent across details and compact views");
            Program.Log.Write("INFO","ClaudeSettingsTest","Claude manual plan: both save paths, navigation, rollback and matching card labels passed");
            Navigate("settings");
            var codeSwitch=CapacityTestDescendants(claudeSettingsPanel!).OfType<ToggleSwitch>().Single(t=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t)=="claude-code-enabled");
            var codePath=CapacityTestDescendants(claudeSettingsPanel!).OfType<TextBox>().Single(t=>Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t)=="claude-code-home");
            codeSwitch.IsOn=true;codePath.Text=Path.Combine(Program.DataPath,"code-fixture");CapacityTestInvoke(settingsSaveButton!);
            await CapacityTestWait(()=>app.Config.ClaudeCodeEnabled&&settingsSaveButton!.IsEnabled,"Claude Code global save failed");
            Reenter(true);
            if(!readClaudeSettings!().CodeEnabled||readClaudeSettings!().CodeHome!=codePath.Text||!Settings.Load().ClaudeCodeEnabled)throw new InvalidOperationException("Claude Code draft/save/navigation mismatch");
            Directory.CreateDirectory(temporary);
            try
            {
                try{await app.ConfigureClaudeAsync(new(true,fixtureDirectory,null,"pro",false));throw new InvalidOperationException("Claude Code write failure accepted");}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){if(!app.Config.ClaudeCodeEnabled||!Settings.Load().ClaudeCodeEnabled)throw new InvalidOperationException("Claude Code settings rollback failed");}
            }
            finally{Directory.Delete(temporary);}
            Program.Log.Write("INFO","ClaudeSettingsTest","Claude Code enable, custom path, persistence and rollback passed");
            if(!ReferenceEquals(retainedEvents,app.Events)||app.Config.CodexHome!=source)throw new InvalidOperationException("Source switches removed history or changed source");
            Program.Log.Write("INFO","ClaudeSettingsTest","Data sources independent switches, stop guards, retained history and safe boundary passed");
            Program.Log.Write("INFO","ClaudeSettingsTest","Claude global/section save, navigation, source draft, validation and write rollback passed");
        }
        catch(Exception ex){Program.Log.Write("ERROR","ClaudeSettingsTest",ex.ToString());}
    }
}

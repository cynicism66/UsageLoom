using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UsageLoom.App;

// Keep the updater UI separate from usage/account settings.
internal sealed partial class Dashboard
{
    private UIElement AppUpdatePanel()
    {
        string T(string zh, string en) => LoomApp.UpdateText(zh, en);
        var group = new StackPanel { Spacing = 12 };
        var automatic = new ToggleSwitch { Header = T("自动检查更新（每天一次）", "Check for updates automatically (daily)"), IsOn = app.Config.AutoUpdateCheck,
            OnContent=T("开", "On"), OffContent=T("关", "Off") };
        automatic.Toggled += (_, _) => { if (!app.IsDemo) { app.Config.AutoUpdateCheck = automatic.IsOn; app.Config.Save(); } };
        group.Children.Add(automatic);
        group.Children.Add(new TextBlock { Text = T("只查询 GitHub Release，不上传账号或用量。点击更新后将下载、校验、退出并替换程序，然后重新启动；用户数据保持不变。请先保存其他设置。", "Checks GitHub Releases without sending account or usage data. Updating downloads and verifies the package, exits, upgrades and restarts the app. User data is preserved. Save other settings first."), TextWrapping = TextWrapping.Wrap });
        var label = new TextBlock { Text = app.UpdateStatus, TextWrapping = TextWrapping.Wrap };
        var notes = new TextBlock { Text = app.AvailableRelease?.Notes ?? "", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        var install = Button(T("下载更新并重启", "Download update and restart"), async () =>
        {
            var dialog = new ContentDialog { XamlRoot = ((FrameworkElement)Content).XamlRoot,
                Title = T("更新并重启 UsageLoom？", "Update and restart UsageLoom?"),
                Content = T("下载完成后会自动退出并升级。安装版可能需要 Windows 权限确认；免安装版保留旧程序备份。", "After downloading, the app will exit and upgrade. Windows may request permission for MSI installation. Portable updates keep a backup."),
                PrimaryButtonText = T("更新并重启", "Update and restart"), CloseButtonText = T("取消", "Cancel"), DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            progress.Visibility = Visibility.Visible;
            try { await app.InstallAppUpdateAsync(new Progress<double>(value => { progress.Value = value; label.Text = T($"下载中 {value:0}%（完成后校验）", $"Downloading {value:0}% (verification follows)"); })); }
            catch { label.Text = T("更新未完成，原版本仍保留，请重试。", "Update did not complete. The existing version is retained; please retry."); throw; }
            finally { progress.Visibility = Visibility.Collapsed; }
        });
        install.IsEnabled = app.AvailableRelease is not null && !app.IsDemo;
        group.Children.Add(Button(T("检查更新", "Check for updates"), async () =>
        {
            label.Text = T("正在检查…", "Checking…");
            await app.CheckAppUpdateAsync(true);
            label.Text = app.UpdateStatus; notes.Text = app.AvailableRelease?.Notes ?? "";
            install.IsEnabled = app.AvailableRelease is not null && !app.IsDemo;
        }));
        group.Children.Add(Button(T("打开发布页", "Open releases page"), async () =>
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/cynicism66/UsageLoom/releases/latest"));
        }));
        group.Children.Add(label); group.Children.Add(install); group.Children.Add(progress);
        group.Children.Add(new ScrollViewer { MaxHeight = 280, Content = notes, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return StableExpander.Configure(new Expander { Header = T("软件更新", "Software updates"), Content = group,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
    }
}

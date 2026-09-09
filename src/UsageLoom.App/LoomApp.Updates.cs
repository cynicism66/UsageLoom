using UsageLoom.Core;

namespace UsageLoom.App;

public sealed partial class LoomApp
{
    internal UpdateRelease? AvailableRelease { get; private set; }
    internal string UpdateStatus { get; private set; } = "";
    private bool updateChecking, updateInstalling;
    private bool updateResultChecked;
    internal static string UpdateText(string chinese, string english) => L10n.Language == "en-US" ? english : chinese;

    internal async Task CheckAppUpdateAsync(bool manual)
    {
        if (IsDemo || quitting || updateChecking || updateInstalling) return;
        if (!updateResultChecked)
        {
            updateResultChecked = true;
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageLoom", "updates");
                var result = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "result.txt", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
                if (result is not null && Config.LastUpdateResult != result)
                {
                    UpdateStatus = File.ReadAllText(result);
                    Config.LastUpdateResult = result; Config.Save();
                    tray?.Notify("UsageLoom", UpdateStatus.StartsWith("Update completed") ? UpdateText("更新完成。", "Update completed.") : UpdateText("更新失败，详情见设置中的更新结果。", "Update failed. See the update result in Settings."));
                    return;
                }
            }
            catch (Exception ex) { Program.Log.Write("WARN", "AppUpdate", ex.Message); }
        }
        if (!manual && (!Config.AutoUpdateCheck || DateTimeOffset.UtcNow - (Config.LastUpdateCheck ?? DateTimeOffset.MinValue) < TimeSpan.FromDays(1))) return;
        updateChecking = true;
        try
        {
            Config.LastUpdateCheck = DateTimeOffset.UtcNow;
            Config.Save();
            UpdateStatus = UpdateText("正在检查更新…", "Checking for updates…");
            AvailableRelease = await AppUpdates.CheckAsync();
            UpdateStatus = AvailableRelease is { } release
                ? UpdateText($"发现新版本 {release.Version}，可在设置中更新。", $"Version {release.Version} is available in Settings.")
                : UpdateText("当前已是最新正式版。", "You are using the latest stable version.");
            if (!manual && AvailableRelease is not null && !quitting) tray?.Notify("UsageLoom", UpdateStatus);
        }
        catch (Exception ex)
        {
            AvailableRelease ??= AppUpdates.CachedRelease();
            UpdateStatus = ex is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests }
                ? UpdateText("GitHub 暂时限制此网络的访问，请稍后重试，或打开发布页手动下载。", "GitHub has temporarily limited this network. Retry later or open the releases page.")
                : UpdateText("暂时无法获取更新信息，请检查网络，或打开发布页手动下载。", "Update information is unavailable. Check your connection or open the releases page.");
            if(AvailableRelease is {} cached)UpdateStatus+=UpdateText($" 已保留此前获取的 {cached.Version}，仍可尝试下载；文件会重新校验。", $" Previously retrieved {cached.Version} remains available to download and verify.");
            Program.Log.Write("WARN", "AppUpdate", ex.Message);
        }
        finally { updateChecking = false; }
    }

    internal async Task InstallAppUpdateAsync(IProgress<double> progress)
    {
        if (IsDemo || quitting || updateInstalling || AvailableRelease is null) return;
        updateInstalling = true;
        try
        {
            var stage = await AppUpdates.DownloadAsync(AvailableRelease, progress);
            if (quitting) return;
            AppUpdates.Launch(stage);
            await QuitAsync();
        }
        finally { updateInstalling = false; }
    }
}

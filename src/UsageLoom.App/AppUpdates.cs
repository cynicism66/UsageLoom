using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using UsageLoom.Core;

namespace UsageLoom.App;

internal static class AppUpdates
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UsageLoom-Updater/1.0");
        return client;
    }

    internal static bool IsInstalled()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(name);
                if (key?.GetValue("DisplayName") as string != "UsageLoom") continue;
                var path = key.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetFullPath(path).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    internal static async Task<UpdateRelease?> CheckAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await Http.GetAsync("https://api.github.com/repos/cynicism66/UsageLoom/releases/latest", timeout.Token);
        response.EnsureSuccessStatusCode();
        return UpdateRelease.Parse(await response.Content.ReadAsStringAsync(timeout.Token), Program.Version, IsInstalled());
    }

    internal static async Task<string> DownloadAsync(UpdateRelease release, IProgress<double> progress)
    {
        if (!IsInstalled() && !File.Exists(Path.Combine(AppContext.BaseDirectory, "update-manifest.txt")))
            throw new InvalidOperationException("This build has no update manifest. Install a packaged release before using automatic replacement.");
        if (release.Size <= 0 || release.Size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid package size");
        var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageLoom", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var package = Path.Combine(stage, "package" + Path.GetExtension(new Uri(release.Url).AbsolutePath));
        using var response = await Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var output = File.Create(package + ".partial"))
        {
            var buffer = new byte[128 * 1024]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
            {
                total += count;
                if (total > release.Size) throw new InvalidDataException("Package exceeds expected size");
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                progress.Report(100.0 * total / release.Size);
            }
            if (total != release.Size) throw new InvalidDataException("Incomplete download");
        }
        await using (var stream = File.OpenRead(package + ".partial"))
        {
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 mismatch");
        }
        File.Move(package + ".partial", package);
        // The helper is shipped with this app, never downloaded as executable script.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Update-UsageLoom.ps1"), Path.Combine(stage, "update.ps1"));
        File.WriteAllText(Path.Combine(stage, "job.json"), JsonSerializer.Serialize(new {
            Target = AppContext.BaseDirectory, Package = package, Hash = release.Digest,
            ProcessId = Environment.ProcessId, Version = release.Version, Installed = IsInstalled()
        }));
        return stage;
    }

    internal static void Launch(string stage)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(stage, "update.ps1") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start updater");
    }
}

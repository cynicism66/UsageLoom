using System.Text.Json;
using System.Text.RegularExpressions;

namespace UsageLoom.Core;

public sealed record UpdateRelease(string Version, string Notes, string Url, string Digest, long Size)
{
    public static UpdateRelease? Parse(string json, string currentVersion, bool installed)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$")) throw new InvalidDataException("Unsupported release version");
        var version = tag[1..];
        if (!System.Version.TryParse(version, out var candidate)) throw new InvalidDataException("Invalid version");
        if (candidate <= System.Version.Parse(currentVersion)) return null;
        var expected = $"UsageLoom-{version}-win-x64." + (installed ? "msi" : "zip");
        var asset = root.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == expected);
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (url != $"https://github.com/cynicism66/UsageLoom/releases/download/{tag}/{expected}" || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("Release URL or SHA-256 digest is missing/invalid");
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid package size");
        return new(version, root.GetProperty("body").GetString() ?? "", url, digest[7..], size);
    }
}

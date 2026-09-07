using System.Security.Cryptography;
using System.Text;

namespace UsageLoom.Core;

/// <summary>
/// Creates a device-local, non-reversible account key. The source value is never persisted.
/// </summary>
public sealed class LocalAccountFingerprint(string keyPath)
{
    private readonly object gate = new();
    private byte[]? key;

    public string? Create(string source, string? value)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        byte[] digest;
        lock (gate)
        {
            using var hmac = new HMACSHA256(LoadOrCreateKey());
            digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(source + "\0" + normalized));
        }
        return "local-v1:" + Convert.ToHexString(digest);
    }

    private byte[] LoadOrCreateKey()
    {
        if (key is not null) return key;
        var path = Path.GetFullPath(keyPath);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("账户指纹密钥路径无效");
        Directory.CreateDirectory(directory);
        if (File.Exists(path)) return key = ReadKey(path);

        var generated = RandomNumberGenerator.GetBytes(32);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(generated);
                stream.Flush(true);
            }
            try { File.Move(temporary, path, false); }
            catch (IOException) when (File.Exists(path)) { }
            return key = File.Exists(path) ? ReadKey(path) : generated;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static byte[] ReadKey(string path)
    {
        var value = File.ReadAllBytes(path);
        if (value.Length != 32) throw new InvalidDataException("账户指纹密钥格式无效");
        return value;
    }
}

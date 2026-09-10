using System.Text.RegularExpressions;

namespace UsageLoom.Core;

public static partial class Privacy
{
    public static string Redact(string? text)
    {
        var result = text ?? "";
        result = Secret().Replace(result, "[凭据已隐藏]");
        result = Email().Replace(result, "[邮箱已隐藏]");
        result = WebUrl().Replace(result, "[网络地址已隐藏]");
        result = FilePath().Replace(result, "[路径已隐藏]");
        return result.Length <= 1200 ? result : result[..1200] + "…";
    }
    [GeneratedRegex(@"(?i)(?:Bearer\s+\S+|(?:access[_-]?token|refresh[_-]?token|api[_-]?key|cookie|password)\s*[:=]\s*[^\s,;]+|\b(?:sk|ghp|github_pat)[_-][A-Za-z0-9_-]+)")]
    private static partial Regex Secret();
    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex Email();
    [GeneratedRegex(@"(?i)https?://[^\s\]\[()""'<>]+")]
    private static partial Regex WebUrl();
    [GeneratedRegex(@"(?i)(?:(?<![a-z0-9+.-])[a-z]:[\\/][^\r\n""<>|]*|\\\\[^\r\n""<>|]+|/(?:home|Users)/[^\s""<>]+)")]
    private static partial Regex FilePath();
}

public enum RpcFailureKind
{
    Unknown,
    Network,
    Dns,
    Proxy,
    Tls,
    Timeout,
    Authentication,
    RateLimited,
    ServiceUnavailable
}

public static class RpcFailure
{
    public static RpcFailureKind Classify(string? message, int? code = null)
    {
        var text=(message??"").ToLowerInvariant();
        if(code is 401 or 403||Contains(text,"unauthorized","forbidden","authentication failed","invalid token","token expired","refresh token","status 401","status: 401","status 403","status: 403","登录凭据","认证失败"))return RpcFailureKind.Authentication;
        if(code==429||Contains(text,"too many requests","rate limited","rate limit exceeded","429 too many"))return RpcFailureKind.RateLimited;
        if(Contains(text,"dns","no such host","name resolution","resolve host","lookup address","nodename nor servname","无法解析","找不到主机"))return RpcFailureKind.Dns;
        if(Contains(text,"proxy","tunnel connection","代理"))return RpcFailureKind.Proxy;
        if(Contains(text,"tls","ssl","certificate","cert chain","secure channel","handshake","证书","安全连接"))return RpcFailureKind.Tls;
        if(Contains(text,"timed out","timeout","operation timed","超时"))return RpcFailureKind.Timeout;
        if(code is 502 or 503 or 504||Contains(text,"service unavailable","bad gateway","gateway timeout","server overloaded","temporarily unavailable","服务暂时不可用"))return RpcFailureKind.ServiceUnavailable;
        if(Contains(text,"error sending request","failed to fetch","connect error","connection refused","connection reset","connection closed","network","socket","tcp connect","网络"))return RpcFailureKind.Network;
        return RpcFailureKind.Unknown;
    }

    public static bool IsRetryable(RpcFailureKind kind)=>kind is RpcFailureKind.Network or RpcFailureKind.Dns or RpcFailureKind.Proxy or RpcFailureKind.Tls or RpcFailureKind.RateLimited or RpcFailureKind.ServiceUnavailable;

    public static string Describe(RpcFailureKind kind)=>kind switch
    {
        RpcFailureKind.Dns=>"无法解析 Codex 服务地址，请检查 DNS 或网络连接",
        RpcFailureKind.Proxy=>"无法通过代理连接 Codex 服务，请检查系统代理设置",
        RpcFailureKind.Tls=>"无法建立 Codex 服务的安全连接，请检查系统时间、证书或 HTTPS 检查软件",
        RpcFailureKind.Timeout=>"Codex 额度服务响应超时，请稍后重试",
        RpcFailureKind.Authentication=>"ChatGPT 登录缓存已失效，刷新现有凭据后仍无法读取额度",
        RpcFailureKind.RateLimited=>"Codex 额度服务暂时限制查询，请稍后重试",
        RpcFailureKind.ServiceUnavailable=>"Codex 额度服务暂时不可用，请稍后重试",
        RpcFailureKind.Network=>"无法连接 Codex 额度服务，请检查网络或代理设置",
        _=>"官方后端未能读取额度"
    };

    private static bool Contains(string text,params string[] values)=>values.Any(text.Contains);
}

public sealed class CodexConnectionClosedException() : IOException(L10n.T("sE65ED89970CA"));

public sealed class CodexRpcException : IOException
{
    public RpcFailureKind Kind { get; }
    public bool Retryable=>RpcFailure.IsRetryable(Kind);
    public int? RpcCode { get; }

    public CodexRpcException(string? message,int? code=null) : this(RpcFailure.Classify(message,code),code) { }
    private CodexRpcException(RpcFailureKind kind,int? code) : base(RpcFailure.Describe(kind))
    {
        Kind=kind;
        RpcCode=code;
    }
}

public sealed class DiagnosticLog(string directory)
{
    private readonly object gate = new();
    public string DirectoryPath { get; } = directory;
    public void Write(string level, string component, string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var file = Path.Combine(DirectoryPath, "runtime.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1_048_576)
                {
                    for (var i = 3; i >= 1; i--)
                    {
                        var old = Path.Combine(DirectoryPath, $"runtime.{i}.log");
                        if (i == 3) { if (File.Exists(old)) File.Delete(old); }
                        else if (File.Exists(old)) File.Move(old, Path.Combine(DirectoryPath, $"runtime.{i + 1}.log"), true);
                    }
                    File.Move(file, Path.Combine(DirectoryPath, "runtime.1.log"), true);
                }
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} [{level}] {component} {Privacy.Redact(message)}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}

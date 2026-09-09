using System.Net.Http;

namespace UsageLoom.Core;

public static class UpdateFeed
{
    public const string ManifestUrl="https://github.com/cynicism66/UsageLoom/releases/latest/download/update.json";
    public const string ApiUrl="https://api.github.com/repos/cynicism66/UsageLoom/releases/latest";
    public static async Task<string> ReadAsync(HttpClient client,string currentVersion,bool installed)
    {
        foreach(var url in new[]{ManifestUrl,ApiUrl})
        {
            try
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
                using var output=new MemoryStream();var buffer=new byte[8192];int count;
                while((count=await stream.ReadAsync(buffer,timeout.Token))>0)
                {
                    if(output.Length+count>1024*1024)throw new InvalidDataException("Update metadata exceeds size limit");
                    output.Write(buffer,0,count);
                }
                var json=System.Text.Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
                UpdateRelease.Parse(json,currentVersion,installed); // Same URL/hash validation on both paths.
                return json;
            }
            catch(Exception ex)when(url==ManifestUrl&&ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            { /* Older releases have no manifest. Try the public API, without credentials. */ }
        }
        throw new IOException("Update sources unavailable");
    }
}

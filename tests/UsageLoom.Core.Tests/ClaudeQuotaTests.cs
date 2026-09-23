using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UsageLoom.Core;

static class ClaudeQuotaTests
{
    private static void Check(bool condition){if(!condition)throw new Exception("Claude quota assertion");}
    internal static void Run(Action<string,Action> test,string fixtureRoot)
    {
        var now=new DateTimeOffset(2026,9,21,13,0,0,TimeSpan.Zero);
        var root=Path.Combine(fixtureRoot,"claude");Directory.CreateDirectory(root);
        var org="11111111-1111-1111-1111-111111111111";
        byte[] Body(double used=65)=>JsonSerializer.SerializeToUtf8Bytes(new{five_hour=new{utilization=used,resets_at=now.AddHours(1)},seven_day=new{utilization=20,resets_at=now.AddDays(2)}});
        test("Claude 响应、倒计时、到期不虚构满额",()=>
        {
            var snapshot=ClaudeQuotaParser.Parse(Body(),now,"a",now);
            Check(snapshot.Windows.Count==2&&snapshot.Windows[0].RemainingText(now)=="35%");
            Check(snapshot.Windows[0].RemainingText(now.AddHours(2))=="—");
            Check(snapshot.ObservedAt==now&&snapshot.Describe(now.AddHours(1))==L10n.T("claude.stale"));
            Check(ClaudeQuotaParser.Parse("{}"u8.ToArray(),now,"a",now).Windows.Count==0);
            foreach(var bad in new[]{-1d,101d})
            {try{ClaudeQuotaParser.Parse(Body(bad),now,"a",now);throw new Exception("accepted invalid percentage");}catch(InvalidDataException){}}
            try{ClaudeQuotaParser.Parse(Body(),now.AddHours(2),"a",now);throw new Exception("future time accepted");}catch(InvalidDataException){}
        });
        test("Claude 来源隔离、乱序及同时间冲突",()=>
        {
            var old=ClaudeQuotaParser.Parse(Body(10),now.AddMinutes(-1),"a",now);
            var current=ClaudeQuotaParser.Parse(Body(),now,"a",now);
            Check(ClaudeQuotaParser.Select([current,old],null).Windows[0].Used==65);
            Check(ClaudeQuotaParser.Select([old,current],null).Windows[0].Used==65);
            Check(ClaudeQuotaParser.Select([current,current with{Scope="b"}],null).Status=="conflict");
            Check(ClaudeQuotaParser.Select([current,current with{Scope="b"}],"b").Scope=="b");
            Check(ClaudeQuotaParser.Select([current],"b").Status=="scopeMissing");
            Check(ClaudeQuotaParser.Select([current,old with{ObservedAt=now}],null).Status=="conflict");
            Check(ClaudeQuotaParser.ScopeKey(root,org)!=ClaudeQuotaParser.ScopeKey(root+"other",org));
        });
        test("Claude 仅允许额度 URL、拒绝相似主机与其他正文",()=>
        {
            var url=$"https://claude.ai/api/organizations/{org}/usage";
            Check(ClaudeDesktopReader.MatchScope("1/0/"+url,root) is not null);
            foreach(var bad in new[]{url+"/other",url+"?token=private",url.Replace("claude.ai/","claude.ai.evil/"),url.Replace("https:","http:"),url.Replace("usage","chat_conversations")})
                Check(ClaudeDesktopReader.MatchScope(bad,root) is null);
        });
        test("Claude 解压：zstd、gzip、br、deflate 与尺寸边界",()=>
        {
            var payload=Body();
            using var zstd=new ZstdSharp.Compressor();
            Check(ClaudeDesktopReader.Decode(zstd.Wrap(payload).ToArray(),"zstd").SequenceEqual(payload));
            foreach(var mode in new[]{"gzip","br","deflate"})
            {
                using var output=new MemoryStream();
                using(Stream encoder=mode switch{"gzip"=>new GZipStream(output,CompressionLevel.Fastest,true),"br"=>new BrotliStream(output,CompressionLevel.Fastest,true),_=>new ZLibStream(output,CompressionLevel.Fastest,true)})encoder.Write(payload);
                Check(ClaudeDesktopReader.Decode(output.ToArray(),mode).SequenceEqual(payload));
            }
            foreach(var mode in new[]{"identity","zstd"})
            {
                var large=new byte[65537];var data=mode=="zstd"?zstd.Wrap(large).ToArray():large;
                var refused=false;try{ClaudeDesktopReader.Decode(data,mode);}catch{refused=true;}Check(refused);
            }
        });
        test("Claude 完整 Blockfile 读取及失败关闭",()=>
        {
            using var compressor=new ZstdSharp.Compressor();
            var compressed=compressor.Wrap(Body()).ToArray();
            var cache=Path.Combine(root,"Cache","Cache_Data");Directory.CreateDirectory(cache);
            var key=Encoding.UTF8.GetBytes($"1/0/https://claude.ai/api/organizations/{org}/usage");
            var header=Encoding.UTF8.GetBytes($"\0HTTP/1.1 200 OK\0date: {now:R}\0content-encoding: zstd\0");
            var index=new byte[372];Put(index,0,0xc103cac3);Put(index,4,0x30000);Put(index,8,1);Put(index,28,1);Put(index,368,0xa1010000);
            var block=new byte[8192+512];Put(block,0,0xc104cac3);Put(block,4,0x20000);Put(block,12,256);
            var ranking=new byte[8192+36];Put(ranking,0,0xc104cac3);Put(ranking,4,0x20000);Put(ranking,12,36);
            Put(block,8192+8,0x90000000);Put(ranking,8192+24,0xa1010000);File.WriteAllBytes(Path.Combine(cache,"data_0"),ranking);
            Put(block,8192+32,(uint)key.Length);Put(block,8192+40,(uint)header.Length);Put(block,8192+44,(uint)compressed.Length);
            Put(block,8192+56,0x80000002);Put(block,8192+60,0x80000001);key.CopyTo(block,8192+96);
            File.WriteAllBytes(Path.Combine(cache,"index"),index);File.WriteAllBytes(Path.Combine(cache,"data_1"),block);
            File.WriteAllBytes(Path.Combine(cache,"f_000001"),compressed);File.WriteAllBytes(Path.Combine(cache,"f_000002"),header);
            var snapshot=ClaudeDesktopReader.Read(root,null,now);
            Check(snapshot.Status=="snapshot"&&snapshot.Windows.Count==2&&snapshot.Windows[0].ResetsAt==now.AddHours(1));
            var historyPath=Path.Combine(root,"plan-usage-history.json");
            File.WriteAllText(historyPath,"{incomplete");
            Check(ClaudeDesktopReader.Read(root,null,now).Status=="snapshot");
            File.Delete(historyPath);
            // Expired snapshot remains historical, not a new full window.
            Check(ClaudeDesktopReader.Read(root,null,now.AddHours(2)).Windows[0].RemainingText(now.AddHours(2))=="—");
            Check(File.ReadAllBytes(Path.Combine(cache,"data_1")).SequenceEqual(block));
            Put(ranking,8192+28,1);File.WriteAllBytes(Path.Combine(cache,"data_0"),ranking);
            Check(ClaudeDesktopReader.Read(root,null,now).Status=="readFailed");
            Put(ranking,8192+28,0);File.WriteAllBytes(Path.Combine(cache,"data_0"),ranking);
            Put(block,8192+4,0xa1010000);File.WriteAllBytes(Path.Combine(cache,"data_1"),block);
            Check(ClaudeDesktopReader.Read(root,null,now).Status=="readFailed");
            Put(block,8192+4,0);Put(block,8192+44,1000000);File.WriteAllBytes(Path.Combine(cache,"data_1"),block);
            Check(ClaudeDesktopReader.Read(root,null,now).Status=="readFailed");
            Put(index,4,123);File.WriteAllBytes(Path.Combine(cache,"index"),index);
            Check(ClaudeDesktopReader.Read(root,null,now).Status=="unsupported");
            using var cancellation=new CancellationTokenSource();cancellation.Cancel();
            try{ClaudeDesktopReader.Read(root,null,now,cancellation.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
        });
        test("Claude 历史降级不拼接重置时间",()=>
        {
            var data=JsonSerializer.SerializeToUtf8Bytes(new{version=2,samples=new[]{new{org,t=now.ToUnixTimeMilliseconds(),u=new{fh=12,sd=25}}}});
            var snapshot=ClaudeQuotaParser.History(data,root,null,now);
            Check(snapshot.HistoryOnly&&snapshot.Windows[0].ResetsAt is null&&snapshot.Windows[1].Used==25);
            Check(ClaudeQuotaParser.History("{\"version\":1,\"samples\":[]}"u8.ToArray(),root,null,now).Status=="unsupported");
            Check(ClaudeDesktopReader.Read(Path.Combine(root,"absent"),null,now).Status=="notFound");
        });
        test("Claude 缓存并发损坏时使用已校验历史，不将旧值冒充实时额度",()=>
        {
            var fallbackRoot=Path.Combine(fixtureRoot,"claude-cache-fallback");
            var cache=Path.Combine(fallbackRoot,"Cache","Cache_Data");
            Directory.CreateDirectory(cache);
            File.WriteAllBytes(Path.Combine(cache,"index"),new byte[16]);
            Check(ClaudeDesktopReader.Read(fallbackRoot,null,now).Status=="readFailed");
            var data=JsonSerializer.SerializeToUtf8Bytes(new{version=2,samples=new[]{new{org,t=now.ToUnixTimeMilliseconds(),u=new{fh=12,sd=25}}}});
            File.WriteAllBytes(Path.Combine(fallbackRoot,"plan-usage-history.json"),data);
            var fallback=ClaudeDesktopReader.Read(fallbackRoot,null,now);
            Check(fallback.Status=="snapshot"&&fallback.HistoryOnly&&fallback.Windows.Count==2);
            Check(fallback.Windows[0].Used==12&&fallback.Windows[0].ResetsAt is null);
            File.WriteAllText(Path.Combine(fallbackRoot,"plan-usage-history.json"),"{broken");
            Check(ClaudeDesktopReader.Read(fallbackRoot,null,now).Status=="readFailed");
        });
        test("Claude 新历史不沿用旧周期重置，不忽略跨来源冲突",()=>
        {
            var cache=ClaudeQuotaParser.Select([ClaudeQuotaParser.Parse(Body(),now,"a",now)],null);
            var history=cache with{ObservedAt=now.AddMinutes(5),HistoryOnly=true,Windows=cache.Windows.Select(w=>w with{ResetsAt=null}).ToArray()};
            Check(ClaudeQuotaParser.Reconcile(cache,history,null).ObservedAt==now);
            var changed=history with{Windows=[new("five_hour",70,null),new("seven_day",20,null)]};
            Check(ClaudeQuotaParser.Reconcile(cache,changed,null).HistoryOnly);
            Check(ClaudeQuotaParser.Reconcile(cache,history with{ObservedAt=now.AddHours(2)},null).Windows.All(w=>w.ResetsAt is null));
            Check(ClaudeQuotaParser.Reconcile(cache,history with{Scope="b",Scopes=["b"]},null).Status=="conflict");
            Check(ClaudeQuotaParser.Reconcile(cache,ClaudeQuotaSnapshot.Empty("unsupported"),null).Status=="unsupported");
        });
        test("Claude 损坏和未来历史不回退到较旧有效记录",()=>
        {
            foreach(var sample in new[]{new{org,t=now.AddHours(1).ToUnixTimeMilliseconds()},new{org="",t=now.ToUnixTimeMilliseconds()}})
            {
                var data=JsonSerializer.SerializeToUtf8Bytes(new{version=2,samples=new[]{new{sample.org,sample.t,u=new{fh=12,sd=25}}}});
                try{ClaudeQuotaParser.History(data,root,null,now);throw new Exception("Unsafe sample accepted");}catch(InvalidDataException){}
            }
        });
    }
    private static void Put(byte[] bytes,int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset,4),value);
}

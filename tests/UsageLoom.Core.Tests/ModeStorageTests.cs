using System.Text.Json;
using Microsoft.Data.Sqlite;
using UsageLoom.Core;
using UsageLoom.Storage;

internal static class ModeStorageTests
{
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static readonly DateTimeOffset Start=DateTimeOffset.Parse("2026-09-12T10:00:00Z");
    private static string Row(string type,object payload,int seconds)=>JsonSerializer.Serialize(new{type,timestamp=Start.AddSeconds(seconds).ToString("O"),payload});
    private static string Meta(string id)=>Row("session_meta",new{id},0);
    private static string Settings(string id,string tier,int at)=>Row("event_msg",new{type="thread_settings_applied",thread_id=id,
        thread_settings=new{model="gpt-6-astra",service_tier=tier,reasoning_effort="ultra",instructions="PRIVATE_DO_NOT_KEEP"},text="PRIVATE_DO_NOT_KEEP"},at);
    private static string Context(int at)=>Row("turn_context",new{model="gpt-6-astra",effort="ultra",instructions="PRIVATE_DO_NOT_KEEP"},at);
    private static string Count(int total,int last,int at)=>Row("event_msg",new{type="token_count",info=new{
        total_token_usage=new{input_tokens=total,cached_input_tokens=0,output_tokens=0,reasoning_output_tokens=0,total_tokens=total},
        last_token_usage=new{input_tokens=last,cached_input_tokens=0,output_tokens=0,reasoning_output_tokens=0,total_tokens=last}}},at);
    private static (string Home,string File,HistoryStore Store) Fixture(string root,string name)
    {
        var home=Path.Combine(root,name);Directory.CreateDirectory(Path.Combine(home,"sessions"));
        return (home,Path.Combine(home,"sessions","rollout.jsonl"),new HistoryStore(Path.Combine(home,"db")));
    }
    private static void SetMetadata(string home,string key,string value)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(home,"db","usage-v2.sqlite"),Pooling=false}.ToString());db.Open();
        using var command=db.CreateCommand();command.CommandText="INSERT INTO metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key",key);command.Parameters.AddWithValue("$value",value);command.ExecuteNonQuery();
    }
    private static string? Metadata(string home,string key)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(home,"db","usage-v2.sqlite"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();
        using var command=db.CreateCommand();command.CommandText="SELECT value FROM metadata WHERE key=$key";command.Parameters.AddWithValue("$key",key);return command.ExecuteScalar() as string;
    }
    private static List<UsageEvent> Downgrade(HistoryStore store)
    {
        // Establish real persisted confirmation through the public workflow.
        // Save deliberately refuses to rewrite a previous row's attribution.
        var unassigned=store.Read().Where(e=>e.AccountScope is null).ToList();
        if(unassigned.Count>0)store.ConfirmAttribution(unassigned,"account-a");
        var events=store.Read().Select(e=>e with{Pricing=new(RequestInputTokens:e.Tokens.Input)}).ToList();
        var indexes=store.ReadIndexes().Values.Select(i=>i with{ModeMetadataVersion=0,ModeRecords=[]}).ToList();
        store.Save(new(events,indexes.Count,0,Start.AddMinutes(5)),indexes:indexes);
        return store.Read();
    }
    public static void Run(Action<string,Func<Task>> test,string root)
    {
        test("模式元数据迁移保留计数账本、归属、控制记录和历史，只重估金额",async()=>
        {
            var f=Fixture(root,"mode-storage-upgrade");
            await File.WriteAllLinesAsync(f.File,[Meta("mode-upgrade"),Settings("mode-upgrade","priority",1),Context(2),Count(100,100,3)]);
            await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);
            var old=Downgrade(f.Store);var oldRecords=f.Store.ReadIndexes()[f.File].Records.ToArray();
            SetMetadata(f.Home,"capacity_observation_floor",Start.AddDays(-1).ToString("O"));
            SetMetadata(f.Home,"legacy_quota_timeout_evidence_v1","[]");
            SetMetadata(f.Home,"restart-inference-test","retained-proof");
            var cache=new CapacityCache(4,"account-a","pro",Pricing.CatalogVersion,Start,[new("week","周",Start.AddDays(7),3,3,100,.001m,100,1,0)]);
            f.Store.SaveCapacity(cache);
            var scan=new IncrementalHistory(f.Store);var result=await scan.ScanAsync(f.Home,default);
            var current=f.Store.Read();
            Check(result.ModeMetadataMigrated&&result.BackupPath is not null&&File.Exists(result.BackupPath),"升级前必须备份");
            Check(!result.Migrated&&current.Count==old.Count,"证据升级不是计数解析器全量迁移");
            Check(current.Zip(old).All(pair=>pair.First.Id==pair.Second.Id&&pair.First.Tokens==pair.Second.Tokens&&pair.First.Timestamp==pair.Second.Timestamp&&pair.First.AccountScope==pair.Second.AccountScope&&pair.First.AccountAttribution==pair.Second.AccountAttribution),"计数、身份和归属必须保持");
            Check(Pricing.Calculate(current.Single()).Cost==.002m&&Pricing.ResolveMode(current.Single().Pricing)==("fast","request-setting"),"旧Fast应按设置重估且不是实际响应证据");
            Check(f.Store.ReadIndexes()[f.File].Records.SequenceEqual(oldRecords),"不能改变重启续接验证的计数账本前缀");
            Check(Metadata(f.Home,"capacity_observation_floor")==Start.AddDays(-1).ToString("O")&&Metadata(f.Home,"restart-inference-test")=="retained-proof"&&Metadata(f.Home,"legacy_quota_timeout_evidence_v1")=="[]","不得抹去历史安全边界/证据");
            Check(f.Store.ReadCapacity()!.Windows.Single()==cache.Windows.Single(),"重算前保留旧参考，不能清空采样");
            var unchanged=await scan.ScanAsync(f.Home,default);
            Check(unchanged.Report.UsedCache&&!unchanged.ModeMetadataMigrated&&unchanged.BackupPath is null,"升级幂等");
        });
        test("模式索引白名单不保存正文、提示或认证字段",async()=>
        {
            var f=Fixture(root,"mode-storage-privacy");
            await File.WriteAllLinesAsync(f.File,[Meta("privacy"),Settings("privacy","priority",1),Context(2),Count(100,100,3)]);
            await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);
            var index=f.Store.ReadIndexes()[f.File];
            Check(index.ModeMetadataVersion==IncrementalHistory.ModeMetadataVersion&&index.ModeRecords.Count==2,"应保存两条结构化模式证据");
            Check(!JsonSerializer.Serialize(index).Contains("PRIVATE_DO_NOT_KEEP"),"不得保留正文");
            foreach(var text in index.ModeRecords)
            {
                using var doc=JsonDocument.Parse(text);var p=doc.RootElement.GetProperty("payload");
                Check(!p.TryGetProperty("instructions",out _)&&!p.TryGetProperty("text",out _),"白名单之外必须丢弃");
            }
        });
        test("模式升级保留缺失源文件的已索引事件与归属",async()=>
        {
            var f=Fixture(root,"mode-storage-missing");var second=Path.Combine(f.Home,"sessions","removed.jsonl");
            await File.WriteAllLinesAsync(f.File,[Meta("kept"),Settings("kept","priority",1),Context(2),Count(100,100,3)]);
            await File.WriteAllLinesAsync(second,[Meta("removed"),Context(2),Count(200,200,3)]);
            await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);var before=Downgrade(f.Store);
            File.Delete(second); // An explicitly created, scoped synthetic fixture.
            await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);
            var after=f.Store.Read();
            Check(after.Count==before.Count&&after.Sum(e=>e.Tokens.Total)==300,"模式补齐不能删除已索引的历史");
            Check(after.Single(e=>e.Session=="removed").Pricing!.RequestedServiceTier is null,"缺失证据保持未知");
            Check(after.All(e=>e.AccountScope=="account-a"),"既有归属保留");
        });
        test("显式清空档位在原始日志与索引回放中语义一致",async()=>
        {
            foreach(var tier in new object?[]{null,"",17})
            {
                var f=Fixture(root,"mode-storage-clear-"+(tier is null?"null":tier is string?"empty":"invalid"));
                await File.WriteAllLinesAsync(f.File,[Meta("clear"),Settings("clear","priority",1),
                    Row("turn_context",new{model="gpt-6-astra",service_tier=tier,effort="ultra"},2),Count(100,100,3)]);
                var raw=await new HistoryScanner().ScanAsync(f.Home,default);
                await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);
                var indexed=f.Store.Read().Single();
                Check(Pricing.ResolveMode(raw.Events.Single().Pricing)==Pricing.ResolveMode(indexed.Pricing),"白名单不得把清空档位变成继承Fast");
                Check(Pricing.ResolveMode(indexed.Pricing).Tier=="unknown","显式未知不能沿用旧Fast");
            }
        });
        test("明确关闭或清空Fast后缺字段不回流，原始日志与旧索引迁移回放一致",async()=>
        {
            foreach(var tier in new string?[]{"default",null})
            {
                var f=Fixture(root,"mode-storage-stale-fallback-"+(tier??"null"));
                await File.WriteAllLinesAsync(f.File,[Meta("stale-fallback"),Settings("stale-fallback","priority",1),Context(2),Count(100,100,3),
                    Row("turn_context",new{model="gpt-6-astra",service_tier=tier,effort="medium"},4),Count(200,100,5),
                    Context(6),Count(300,100,7)]);
                var raw=(await new HistoryScanner().ScanAsync(f.Home,default)).Events.OrderBy(e=>e.Timestamp).ToArray();
                string[] expected=["fast",tier is null?"unknown":"standard","unknown"];
                Check(raw.Select(e=>Pricing.ResolveMode(e.Pricing).Tier).SequenceEqual(expected),"原始日志的后续缺字段context不能重新继承旧Fast");
                await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);
                var initial=f.Store.Read().OrderBy(e=>e.Timestamp).ToArray();
                Check(initial.Select(e=>Pricing.ResolveMode(e.Pricing)).SequenceEqual(raw.Select(e=>Pricing.ResolveMode(e.Pricing)))&&
                    initial.Select(e=>e with{Pricing=null}).SequenceEqual(raw.Select(e=>e with{Pricing=null})),"首次索引应与原始日志的模式、计数和归属一致");
                var ledger=f.Store.ReadIndexes()[f.File].Records.ToArray();
                var old=Downgrade(f.Store).OrderBy(e=>e.Timestamp).ToArray();
                var scan=new IncrementalHistory(f.Store);var migration=await scan.ScanAsync(f.Home,default);
                var migrated=f.Store.Read().OrderBy(e=>e.Timestamp).ToArray();
                Check(migration.ModeMetadataMigrated&&migration.BackupPath is not null&&File.Exists(migration.BackupPath),"旧模式索引补齐前必须备份");
                Check(migrated.Select(e=>Pricing.ResolveMode(e.Pricing).Tier).SequenceEqual(expected),"模式迁移不能让已关闭或清空的Fast回流");
                Check(migrated.Select(e=>e with{Pricing=null}).SequenceEqual(old.Select(e=>e with{Pricing=null}))&&
                    migrated.Length==3&&migrated.Sum(e=>e.Tokens.Total)==300&&
                    migrated.All(e=>e.AccountScope=="account-a"&&e.AccountAttribution=="user-confirmed"),"升级只能补模式，不能改事件、Token或撤销确认归属");
                Check(f.Store.ReadIndexes()[f.File].Records.SequenceEqual(ledger),"模式升级不得重写旧计数账本");
                var records=f.Store.ReadIndexes().ToDictionary(pair=>pair.Key,
                    pair=>(IReadOnlyList<string>)pair.Value.Records.Concat(pair.Value.ModeRecords).ToArray(),StringComparer.OrdinalIgnoreCase);
                var replay=(await new HistoryScanner().ScanAsync(f.Home,default,indexedRecords:records)).Events.OrderBy(e=>e.Timestamp).ToArray();
                Check(replay.Select(e=>Pricing.ResolveMode(e.Pricing)).SequenceEqual(raw.Select(e=>Pricing.ResolveMode(e.Pricing)))&&
                    replay.Select(e=>(e.Id,e.Tokens,e.Model,e.Timestamp)).SequenceEqual(raw.Select(e=>(e.Id,e.Tokens,e.Model,e.Timestamp))),"迁移后sidecar回放必须与原始日志模式及计数身份一致");
                var unchanged=await scan.ScanAsync(f.Home,default);
                Check(unchanged.Report.UsedCache&&!unchanged.ModeMetadataMigrated&&
                    f.Store.Read().OrderBy(e=>e.Timestamp).SequenceEqual(migrated),"重复扫描不得重新迁移或改变已修复模式与归属");
            }
        });
        test("他人context的紧凑副本不能复活Fast，模式升级与raw回放一致",async()=>
        {
            var contexts=new object[]{new{model="gpt-6-astra",thread_id="other"},
                new{model="gpt-6-astra",thread_id="other",service_tier="priority"}};
            for(var i=0;i<contexts.Length;i++)
            {
                var f=Fixture(root,"mode-storage-foreign-"+i);
                await File.WriteAllLinesAsync(f.File,[Meta("owner"),Settings("owner","priority",1),
                    Row("turn_context",new{model="gpt-6-astra",thread_id="owner",service_tier="default"},2),Count(100,100,3),
                    Row("turn_context",contexts[i],4),Count(200,100,5)]);
                var raw=await new HistoryScanner().ScanAsync(f.Home,default);
                Check(raw.Events.All(e=>Pricing.ResolveMode(e.Pricing).Tier=="standard"),"raw应忽略他人context");
                var scan=new IncrementalHistory(f.Store);await scan.ScanAsync(f.Home,default);
                var ledger=f.Store.ReadIndexes()[f.File].Records.ToArray();
                foreach(var migration in new[]{false,true})
                {
                    if(migration){Downgrade(f.Store);await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);}
                    var indexed=f.Store.Read().OrderBy(e=>e.Timestamp).ToArray();
                    Check(indexed.All(e=>Pricing.ResolveMode(e.Pricing).Tier=="standard"),"紧凑副本不能把standard变成Fast");
                    Check(indexed.Select(e=>(e.Id,e.Tokens,e.Timestamp,e.Model)).SequenceEqual(raw.Events.Select(e=>(e.Id,e.Tokens,e.Timestamp,e.Model))),"模式约束不能改变计数身份");
                    Check(f.Store.ReadIndexes()[f.File].Records.SequenceEqual(ledger),"修复不能重写计数账本前缀");
                }
            }
        });
        test("model-only与缺失模型context边界在raw和sidecar中一致",async()=>
        {
            var contexts=new object[]{new{model="gpt-6-astra",thread_id="owner"},new{thread_id="owner"},new{model=(string?)null,thread_id="owner"}};
            for(var i=0;i<contexts.Length;i++)
            {
                var f=Fixture(root,"mode-storage-boundary-"+i);
                await File.WriteAllLinesAsync(f.File,[Meta("owner"),Settings("owner","priority",1),
                    Row("turn_context",new{model="gpt-6-astra",thread_id="owner",service_tier="default"},2),Count(100,100,3),
                    Row("turn_context",contexts[i],4),Count(200,100,5)]);
                var raw=await new HistoryScanner().ScanAsync(f.Home,default);
                await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);
                var indexed=f.Store.Read().OrderBy(e=>e.Timestamp).ToArray();
                Check(indexed.Select(e=>Pricing.ResolveMode(e.Pricing)).SequenceEqual(raw.Events.Select(e=>Pricing.ResolveMode(e.Pricing))),"合法model-only覆盖和损坏模型边界都必须保留");
                Check(Pricing.ResolveMode(indexed[1].Pricing).Tier=="unknown","明确普通context后的缺字段或损坏模型边界不能复活旧Fast");
                Check(indexed.Sum(e=>e.Tokens.Total)==200,"模式缺失不能丢Token");
            }
        });
        test("模式设置缺少生效时间不沿用旧Fast，raw与sidecar均保持未知",async()=>
        {
            for(var kind=0;kind<4;kind++)
            {
                var f=Fixture(root,"mode-storage-untimed-"+kind);
                object payload=kind%2==0?new{type="thread_settings_applied",thread_id="owner",thread_settings=new{model="gpt-6-astra",service_tier="default"}}:
                    new{model="gpt-6-astra",thread_id="owner",service_tier="default"};
                var boundary=JsonSerializer.Serialize(new{type=kind%2==0?"event_msg":"turn_context",timestamp=kind<2?null:"invalid-time",payload});
                await File.WriteAllLinesAsync(f.File,[Meta("owner"),Settings("owner","priority",1),Context(2),Count(100,100,3),boundary,Count(200,100,5)]);
                var raw=await new HistoryScanner().ScanAsync(f.Home,default);
                await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);
                var indexed=f.Store.Read().OrderBy(e=>e.Timestamp).ToArray();
                Check(indexed.All(e=>Pricing.ResolveMode(e.Pricing).Tier=="unknown")&&raw.Events.All(e=>Pricing.ResolveMode(e.Pricing).Tier=="unknown"),"无法确定设置生效时间时不可推断Fast连续");
                Check(indexed.Select(e=>(e.Id,e.Tokens,e.Timestamp,e.Model)).SequenceEqual(raw.Events.Select(e=>(e.Id,e.Tokens,e.Timestamp,e.Model))),"未知时间不影响有效Token与身份");
            }
        });
        test("未定时的他人context不污染本线程，sidecar仍抑制无约束副本",async()=>
        {
            var f=Fixture(root,"mode-storage-foreign-untimed");
            var foreign=JsonSerializer.Serialize(new{type="turn_context",timestamp=(string?)null,payload=new{model="gpt-6-astra",thread_id="other",service_tier="priority"}});
            await File.WriteAllLinesAsync(f.File,[Meta("owner"),Settings("owner","default",1),Context(2),Count(100,100,3),foreign,Count(200,100,5)]);
            var raw=await new HistoryScanner().ScanAsync(f.Home,default);
            await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);
            Check(f.Store.Read().All(e=>Pricing.ResolveMode(e.Pricing).Tier=="standard")&&raw.Events.All(e=>Pricing.ResolveMode(e.Pricing).Tier=="standard"),"明确外线程的未定时context应被隔离");
        });
        test("模式迁移保留半行，后续普通档位新增Token不重复",async()=>
        {
            var f=Fixture(root,"mode-storage-tail");
            await File.WriteAllLinesAsync(f.File,[Meta("tail"),Settings("tail","priority",1),Context(2),Count(100,100,3)]);
            var scan=new IncrementalHistory(f.Store);await scan.ScanAsync(f.Home,default);Downgrade(f.Store);
            var next=Settings("tail","default",4)+"\n"+Context(5)+"\n"+Count(200,100,6)+"\n";
            var cut=next.LastIndexOf("token_count",StringComparison.Ordinal)+5;
            await File.AppendAllTextAsync(f.File,next[..cut]);
            await scan.ScanAsync(f.Home,default);
            Check(f.Store.Read().Sum(e=>e.Tokens.Total)==100,"半行不应成为用量");
            await File.AppendAllTextAsync(f.File,next[cut..]);await scan.ScanAsync(f.Home,default);await scan.ScanAsync(f.Home,default);
            var rows=f.Store.Read().OrderBy(e=>e.Timestamp).ToArray();
            Check(rows.Length==2&&rows.Sum(e=>e.Tokens.Total)==200,"重复扫描不重计");
            Check(Pricing.ResolveMode(rows[0].Pricing).Tier=="fast"&&Pricing.ResolveMode(rows[1].Pricing).Tier=="standard","按对应回合档位计价");
        });
        test("模式升级取消不改写旧事件与模式版本",async()=>
        {
            var f=Fixture(root,"mode-storage-cancel");
            await File.WriteAllLinesAsync(f.File,[Meta("cancel"),Settings("cancel","priority",1),Context(2),Count(100,100,3)]);
            await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);var old=Downgrade(f.Store);
            using var ct=new CancellationTokenSource();ct.Cancel();
            try{await new IncrementalHistory(f.Store).ScanAsync(f.Home,ct.Token);throw new InvalidOperationException("应当取消");}catch(OperationCanceledException){}
            Check(f.Store.Read().SequenceEqual(old)&&f.Store.ReadIndexes()[f.File].ModeMetadataVersion==0,"取消必须保留旧数据");
        });
        test("模式升级与正常缓存子分类更正同时发生不冻结扫描",async()=>
        {
            var f=Fixture(root,"mode-storage-correction");
            await File.WriteAllLinesAsync(f.File,[Meta("correction"),Settings("correction","priority",1),Context(2),Count(100,100,3)]);
            await new IncrementalHistory(f.Store).ScanAsync(f.Home,default);Downgrade(f.Store);
            await File.AppendAllLinesAsync(f.File,[Row("event_msg",new{type="token_count",info=new{
                total_token_usage=new{input_tokens=100,cached_input_tokens=40,output_tokens=0,total_tokens=100}}},4)]);
            var scanner=new IncrementalHistory(f.Store);var result=await scanner.ScanAsync(f.Home,default);
            var item=f.Store.Read().Single();
            Check(result.ModeMetadataMigrated&&item.Tokens.Total==100&&item.Tokens.Cached==40,"新日志正常子分类更正必须仍可应用");
            Check(item.AccountScope=="account-a"&&item.AccountAttribution=="user-confirmed","更正不能撤销确认");
            Check((await scanner.ScanAsync(f.Home,default)).Report.UsedCache,"不能反复迁移或冻结增量");
        });
        test("显式重建也保留用户清除采样的floor与已核验审计证据",async()=>
        {
            var f=Fixture(root,"mode-storage-rebuild-floor");
            await File.WriteAllLinesAsync(f.File,[Meta("rebuild-floor"),Context(2),Count(100,100,3)]);
            var scan=new IncrementalHistory(f.Store);await scan.ScanAsync(f.Home,default);
            SetMetadata(f.Home,"capacity_observation_floor",Start.ToString("O"));SetMetadata(f.Home,"restart-inference-test","retained-proof");
            await scan.RebuildAsync(f.Home,default);
            Check(Metadata(f.Home,"capacity_observation_floor")==Start.ToString("O")&&Metadata(f.Home,"restart-inference-test")=="retained-proof","重建不能使用户清除的旧样本复活，也不能删除归属证据");
        });
    }
}

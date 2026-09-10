using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UsageLoom.Core;
using UsageLoom.Storage;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace UsageLoom.App;

public sealed partial class LoomApp : Application
{
    private readonly string[] args;
    private TrayIcon? tray;
    private Dashboard? flyout, dashboard;
    private DispatcherQueueTimer? smokeTimer;
    private DispatcherQueueTimer? timer;
    private DispatcherQueue queue = null!;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource quotaCancellation = new();
    private readonly CodexClient client = new(Program.Log,new LocalAccountFingerprint(Path.Combine(Program.DataPath,"account-fingerprint.key")));
    private readonly HistoryStore store = new(Program.DataPath);
    private readonly HistoryScanner scanner = new();
    private IncrementalHistory? incremental;
    private readonly DateTimeOffset openedAt=DateTimeOffset.UtcNow;
    private RestartCheckpoint? restartCheckpoint;
    private readonly NotificationPolicy notifications = new();
    private readonly WeeklyCapacityEstimator capacityEstimator = new();
    private List<UsageEvent>? capacityPriceEvents;
    private string? capacityPriceAccount;
    private Estimate? capacityPrice;
    private long capacityTokenTotal;
    private bool capacityHistoryReady;
    private int preservedHistoryFiles;
    private IReadOnlyList<UsageUncertainty> capacityUncertainRanges=[];
    private string PreservedHistoryMessage=>L10n.F(capacityHistoryReady?"scan.preservedScoped":"scan.preserved",preservedHistoryFiles);
    private DateTimeOffset capacityIndexedThrough;
    private readonly CapacityBatchSchedule capacityBatch=new(DateTimeOffset.UtcNow);
    private List<UsageEvent>? batchEvents;
    private string? capacityDisplayIdentity;
    private List<QuotaWindow> capacityDisplayWindows=[];
    private DateTimeOffset nextCapacityCleanupCheck=DateTimeOffset.UtcNow.AddMinutes(1);
    private readonly SemaphoreSlim capacityWorkGate=new(1,1);
    private Task? capacityTask, cleanupTask;
    private int capacityEpoch;
    internal bool CapacityBusy=>capacityTask is {IsCompleted:false};
    private DateTimeOffset? capacityLastCalculated;
    private string capacityFeedback="";
    private int capacityInterruptionCount;
    private CapacitySource appliedCapacitySource;
    private CapacitySource CurrentCapacitySource()=>new(Config.CliPath,Config.CodexHome,Config.AuthorizedAccount,Config.ReuseBackend);
    internal string CapacityCalculationStatus=>capacityFeedback+"\n"+L10n.F("capacity.schedule",capacityLastCalculated?.ToLocalTime().ToString("MM-dd HH:mm:ss")??"—",capacityBatch.NextAt.ToLocalTime().ToString("HH:mm:ss"));
    private void RecordCapacityObservation(QuotaState quota,bool barrier=false,bool queryFailure=false)
    {
        if(IsDemo)return;
        try
        {
            var valid=!barrier&&Config.CapacityEnabled&&quota.Fresh&&quota.AccountKey is not null;
            var retainIdentity=queryFailure&&Config.CapacityEnabled&&quota.AccountKey is not null;
            if(!valid&&!queryFailure)capacityInterruptionCount=0;
            store.SaveQuotaObservation(new(valid?quota.FetchedAt??DateTimeOffset.UtcNow:DateTimeOffset.UtcNow,valid||retainIdentity?quota.AccountKey:null,valid||retainIdentity?quota.Plan?.Trim().ToLowerInvariant():null,
                Pricing.CatalogVersion,valid?quota.PrimaryWindows.Where(w=>w.Minutes==10080).ToList():[],!valid)
                {BarrierReason=valid?null:queryFailure?"query-failure":"explicit-boundary"});
            capacityBatch.MarkDirty();
            if(valid)Program.Log.Write("INFO","CapacityPrecision",quota.PrimaryWindows.Any(w=>w.Used!=Math.Truncate(w.Used))?"Fractional percentage observed":"This response contains integer percentages");
        }
        catch(Exception ex){Program.Log.Write("WARN","CapacityLedger",ex.Message);}
    }
    private IReadOnlyList<WeeklyCapacityEstimate> ObserveCapacity(QuotaState quota,bool force=false)
    {
        if(!Config.CapacityEnabled)return [];
        if(!IsDemo)
        {
            if(!ReferenceEquals(batchEvents,Events)){if(batchEvents is null||!batchEvents.SequenceEqual(Events))capacityBatch.MarkDirty();batchEvents=Events;}
            if(quota.Fresh)
            {
                var identity=quota.AccountKey+"|"+quota.Plan+"|"+string.Join(",",quota.PrimaryWindows.Select(w=>w.Key));
                var windows=quota.PrimaryWindows.ToList();
                var reset=windows.Any(w=>capacityDisplayWindows.FirstOrDefault(previous=>previous.Key==w.Key) is {} old&&
                    (w.Used<old.Used||old.ResetsAt is {} a&&w.ResetsAt is {} b&&Math.Abs((a-b).TotalMinutes)>2));
                if(capacityDisplayIdentity!=identity||reset)
                {
                    capacityInterruptionCount=0;
                    capacityDisplayIdentity=identity;capacityEstimator.InitializeTemporal(quota);
                    WeeklyCapacity=capacityEstimator.DisplayCurrent;
                }
                capacityDisplayWindows=windows;
            }
            if(CapacityBusy)return WeeklyCapacity;
            if(!quota.Fresh||!capacityHistoryReady)
            {
                capacityFeedback=preservedHistoryFiles>0?PreservedHistoryMessage:L10n.T(!capacityHistoryReady?"capacity.waitIndex":"capacity.waitQuota");
                return WeeklyCapacity;
            }
            if(!capacityBatch.TryBegin(DateTimeOffset.UtcNow,force,capacityLastCalculated is null))return WeeklyCapacity;
            capacityTask=CalculateCapacityBackgroundAsync(quota);
            queue.TryEnqueue(()=>{if(!quitting)Changed?.Invoke();});
            return WeeklyCapacity;
        }
        if(!capacityHistoryReady)
        {
            if(quota.Fresh)capacityEstimator.ApplyTemporal(quota,null,0,null);
            return capacityEstimator.DisplayCurrent;
        }
        if(!ReferenceEquals(capacityPriceEvents,Events)||capacityPriceAccount!=quota.AccountKey)
        {
            var general=CapacityUsage.ForAccount(Events,quota.AccountKey);
            capacityPrice=Pricing.Summarize(general);capacityTokenTotal=general.Sum(item=>item.Tokens.Total);capacityPriceEvents=Events;
            capacityPriceAccount=quota.AccountKey;
        }
        if(!quota.Fresh)return capacityEstimator.DisplayCurrent;
        // Only synthetic preview data reaches this path; real calculations run in the worker.
        capacityEstimator.Observe(quota,capacityTokenTotal,capacityPrice);
        return capacityEstimator.DisplayCurrent;
    }
    private async Task CalculateCapacityBackgroundAsync(QuotaState quota)
    {
        var events=Events;var epoch=capacityEpoch;var generation=configurationGeneration;var indexedThrough=capacityIndexedThrough;
        var uncertainRanges=capacityUncertainRanges;
        var stamp=new CapacityInputStamp(events,epoch,generation,quota.AccountKey,quota.Plan,quota.FetchedAt);
        capacityFeedback=L10n.T("capacity.running");
        var watch=Stopwatch.StartNew();
        bool Current()=>stamp.Matches(Events,capacityEpoch,configurationGeneration,Quota,Config.CapacityEnabled,quitting);
        try
        {
            await capacityWorkGate.WaitAsync(lifetime.Token);
            try
            {
            var output=await Task.Run(()=>
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    var result=TemporalCapacity.CalculateStable(store.ReadQuotaObservations(),events,indexedThrough,lifetime.Token,uncertainRanges);
                    var general=CapacityUsage.ForAccount(events,quota.AccountKey).ToList();
                    var price=Pricing.Summarize(general);
                    var pending=result.PendingFrom is {} from?general.Where(e=>e.Timestamp>from).Sum(e=>e.Tokens.Total):0;
                    lifetime.Token.ThrowIfCancellationRequested();
                    return (result,price,total:general.Sum(e=>e.Tokens.Total)-pending);
            },lifetime.Token);
            if(!Current()){capacityBatch.MarkDirty();capacityFeedback=L10n.T("capacity.changed");return;}
            // Validate the immutable snapshot on the UI thread before persisting.
            var history=await Task.Run(()=>
            {
                    store.SaveTemporalIntervals(output.result.Intervals,output.result.History,4);
                    return store.ReadValidCapacityHistory();
            },lifetime.Token);
            if(!Current()){capacityBatch.MarkDirty();capacityFeedback=L10n.T("capacity.changed");return;}
            capacityEstimator.Restore(null,history);
            capacityEstimator.ApplyTemporal(quota,output.result.Cache,output.total,output.price,output.result.PendingBaselineUsed);
            capacityInterruptionCount=output.result.Interruptions.Count;
            WeeklyCapacity=capacityEstimator.DisplayCurrent;
            capacityLastCalculated=DateTimeOffset.Now;
            capacityFeedback=L10n.T("capacity.done")+" · "+WeeklyCapacityProgress;
            var cache=capacityEstimator.Export();
            await Task.Run(()=>store.SaveCapacity(cache),lifetime.Token);
            Program.Log.Write("INFO","CapacityBatch",$"Completed in {watch.ElapsedMilliseconds} ms; intervals={output.result.Intervals.Count}; interruptions={capacityInterruptionCount}");
            foreach(var gap in output.result.Interruptions)
                Program.Log.Write("INFO","CapacityContinuity",$"from={gap.From:O}; to={gap.To:O}; reason={gap.Reason}");
            }
            finally{capacityWorkGate.Release();}
        }
        catch(OperationCanceledException){}
        catch(Exception ex){capacityBatch.MarkDirty();capacityFeedback=L10n.T("capacity.failed");Program.Log.Write("WARN","CapacityBatch",ex.Message);}
        finally{if(!quitting)Changed?.Invoke();}
    }
    internal async Task CalculateCapacityNowAsync()
    {
        if(!Config.CapacityEnabled)capacityFeedback=L10n.T("capacity.disabled");
        else WeeklyCapacity=ObserveCapacity(Quota,true);
        Changed?.Invoke();
        if(capacityTask is {} work)await work;
    }
    internal async Task ResetCapacityAsync()
    {
        if(IsDemo){Message=L10n.T("s80323CB58044");return;}
        capacityEpoch++;if(capacityTask is {} work)await work;
        RecordCapacityObservation(Quota,true);await Task.Run(()=>store.SaveCapacity(null));capacityEstimator.Reset();capacityDisplayIdentity=null;WeeklyCapacity=[];
        if(historyLoaded&&Quota.Fresh)WeeklyCapacity=ObserveCapacity(Quota);
        Message=L10n.T("sE8381D29D4E7");
        Program.Log.Write("INFO","CapacityCache",Message);Changed?.Invoke();
    }
    internal Task SetCapacityEnabledAsync(bool enabled)
    {
        capacityEpoch++;Config.CapacityEnabled=enabled;
        RecordCapacityObservation(Quota,true);
        if(!IsDemo)Config.Save();
        capacityEstimator.Reset();WeeklyCapacity=[];
        if(enabled&&!IsDemo)RestoreCapacity();
        if(enabled&&historyLoaded&&Quota.Fresh)WeeklyCapacity=ObserveCapacity(Quota);
        Changed?.Invoke();return Task.CompletedTask;
    }
    internal Task<List<CapacityCache>> ReadCapacityHistoryAsync(int page)=>IsDemo?Task.FromResult(new List<CapacityCache>()):Task.Run(()=>store.ReadCapacityHistory(page));
    private Task? attributionTask;
    internal Task ConfirmAttributionAsync(IReadOnlyList<UsageEvent> preview,string account)
    {
        if(attributionBusy)throw new InvalidOperationException(L10n.T("attribution.retry"));
        attributionTask=ConfirmAttributionCoreAsync(preview,account);return attributionTask;
    }
    private async Task ConfirmAttributionCoreAsync(IReadOnlyList<UsageEvent> preview,string account)
    {
        if(IsDemo||!Quota.Fresh||Quota.AccountKey!=account||scanning||refreshing||attributionBusy)
            throw new InvalidOperationException(L10n.T("attribution.retry"));
        attributionBusy=true;capacityEpoch++;
        try
        {
            if(capacityTask is {} work)await work;
            if(!Quota.Fresh||Quota.AccountKey!=account)throw new InvalidOperationException(L10n.T("attribution.retry"));
            await Task.Run(()=>store.ConfirmAttribution(preview,account),lifetime.Token);
            Events=await Task.Run(()=>store.Read(lifetime.Token),lifetime.Token);
            capacityBatch.MarkDirty();Message=L10n.T("attribution.done");
        }
        finally{attributionBusy=false;if(!quitting)Changed?.Invoke();}
    }
    private bool attributionBusy;
    private void RestoreCapacity(){capacityDisplayIdentity=null;capacityBatch.MarkDirty();capacityEstimator.Restore(store.ReadCapacity(),store.ReadValidCapacityHistory());}
    internal string CapacityCacheStatus=>capacityEstimator.RestoredAt is {} at?L10n.F("s477716960258", at.ToLocalTime()):L10n.T("sFE6B9AB0A2C3");
    private bool refreshing, scanning, quitting;
    private int configurationGeneration, failures;
    private int historyRevision;
    private DateTimeOffset lastAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset lastEventProbe = DateTimeOffset.MinValue;
    private DateTimeOffset lastHistoryCheck = DateTimeOffset.MinValue;
    private DateTimeOffset lastUsageActivity = DateTimeOffset.MinValue;
    private DateTimeOffset historyChangedAt = DateTimeOffset.MinValue;
    private readonly List<FileSystemWatcher> historyWatchers = [];
    private bool historyDirty = true;
    private bool historyLoaded;
    private Task? quotaTask, scanTask;
    private Task? authorizationTask;
    private CancellationTokenSource? authorizationCancellation;
    internal bool Authorizing { get; private set; }
    private string AuthorizedHome=>Path.Combine(Program.DataPath,"authorized-backend");
    internal Settings Config { get; private set; } = Settings.Load();
    internal bool IsDemo => args.Contains("--demo") || args.Contains("--smoke-test");
    internal string PreviewPage => IsDemo ? args.FirstOrDefault(a=>a.StartsWith("--preview-page="))?.Split('=')[1]??"overview" : "overview";
    internal bool PreviewNarrow=>IsDemo&&args.Contains("--preview-narrow");
    internal bool PreviewWide=>IsDemo&&args.Contains("--preview-wide");
    internal bool PreviewHourly=>IsDemo&&args.Contains("--preview-single-day");
    internal bool NavigationCheck=>IsDemo&&args.Contains("--navigation-check");
    internal bool PersonalizationCheck=>IsDemo&&args.Contains("--personalization-check");
    internal QuotaState Quota { get; private set; } = new([], null, null, L10n.T("s68C78EBE89A8"), false);
    internal List<UsageEvent> Events { get; private set; } = [];
    internal bool HasLoadedHistory => IsDemo||historyLoaded;
    internal IReadOnlyDictionary<string,string> SessionNames { get; private set; }=new Dictionary<string,string>();
    internal IReadOnlyList<WeeklyCapacityEstimate> WeeklyCapacity { get; private set; }=[];
    internal string WeeklyCapacityProgress => !Config.CapacityEnabled?L10n.T("s40E9A224A0E3"):preservedHistoryFiles>0?PreservedHistoryMessage:
        capacityEstimator.DescribeProgress(Quota,capacityTokenTotal,capacityHistoryReady)+
        (Quota.Fresh&&capacityInterruptionCount>0?"\n"+L10n.F("capacity.interrupted",capacityInterruptionCount):"");
    internal string HistoryStatus { get; private set; } = L10n.T("sA3A08B0EC497");
    internal string Message { get; private set; } = L10n.T("s5A253CCAEBA1");
    internal event Action? Changed;
    internal string DiagnosticSummary => Privacy.Redact(L10n.F("s85D27251E958", Program.Version, (Config.AuthorizedAccount?L10n.T("sC830712A7378"):Config.ReuseBackend?L10n.T("sF95B8B03421E"):L10n.T("s7AE987981F59")), client.LastStage, client.AccountObservation, Quota.Status, Events.Count, Config.AutoRefresh, client.IsConnected, Message));
    public LoomApp(string[] args)
    {
        this.args = args;
        appliedCapacitySource=CurrentCapacitySource();
        InitializeComponent();
        UnhandledException += (_, e) => Program.Log.Write("ERROR", "UI", e.Exception.ToString());
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if(this.args.Contains("--preview-english"))Config.Language="en-US";
        L10n.Language=Config.Language;
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        queue = DispatcherQueue.GetForCurrentThread();
        if(!IsDemo)
        {
            try{restartCheckpoint=store.TakeRestartCheckpoint();}
            catch(Exception ex){Program.Log.Write("WARN","Attribution",ex.Message);}
            try{RestoreCapacity();Program.Log.Write("INFO","CapacityCache","缓存已读取，等待账号、套餐和周窗口核验");}
            catch(Exception ex){Program.Log.Write("WARN","CapacityCache",ex.Message);}
        }
        notifications.Restore(Config.NotificationWindows, DateTimeOffset.Now);
        tray = new TrayIcon(ToggleFlyout, ShowDetails, ShowSettings, () => _ = RefreshQuotaAsync(true), () => _ = QuitAsync());
        client.AccountInvalidated += () => queue.TryEnqueue(() =>
        {
            if (quitting) return;
            restartCheckpoint=null;
            RecordCapacityObservation(Quota,true);
            capacityEstimator.Reset();WeeklyCapacity=[];
            try{RestoreCapacity();}
            catch(Exception ex){Program.Log.Write("WARN","CapacityCache",ex.Message);}
            Quota = new([], null, null, L10n.T("s62896AA1C5E5"), false);
            Changed?.Invoke();
        });
        // 事件目前只用于触发经完整身份校验的读取，防止缺失身份/字段的事件直接污染当前快照。
        client.QuotaUpdated += payload => queue.TryEnqueue(() =>
        {
            if (quitting || !Config.AutoRefresh || refreshing || DateTimeOffset.Now - lastEventProbe < TimeSpan.FromSeconds(5)) return;
            lastEventProbe = DateTimeOffset.Now;
            _ = RefreshQuotaAsync(false);
        });
        Program.Log.Write("INFO", "App", "UsageLoom 原生应用已启动");
        var smoke = this.args.Contains("--smoke-test");
        if (smoke || this.args.Contains("--demo"))
        {
            if(this.args.Contains("--preview-light"))Config.Theme="Light";
            if(this.args.Contains("--preview-dark"))Config.Theme="Dark";
            Config.AutoRefresh = false;
            Config.LowNotify = false;
            Config.ResetNotify = false;
            Quota = new([new("codex:primary", L10n.T("sFDC11436C31D"), 42, 300, DateTimeOffset.Now.AddHours(2)), new("codex:weekly", L10n.T("sCE5334E5C266"), 18, 10080, DateTimeOffset.Now.AddDays(3))], 2, DateTimeOffset.Now, L10n.T("s060C4C3C9C70"), true);
            Events = Enumerable.Range(0, 7).Select(day => {
                var at=DateTimeOffset.Now.AddDays(-day); var input=new long[]{48200,36100,62800,24500,56300,41000,18700}[day];
                return new UsageEvent("demo-"+day,L10n.T("s718E2B63A78A")+(day+1),day%2==0?L10n.T("s9722F7C17917"):L10n.T("sAD316B8625B7"),day%2==0?"gpt-5.4-mini":"gpt-5.4",L10n.T("s95CB8B370EBC"),at,at.ToString("yyyy-MM-dd"),new(input,input/3,0,input/5,input/20));
            }).ToList();
            if(this.args.Contains("--preview-session-stress"))
            {
                Events=Enumerable.Range(0,36).Select(index=>
                {
                    var group=index<27?0:index<34?1:2;var at=DateTimeOffset.Now.AddMinutes(-index*9);
                    var input=new long[]{18400,9200,3100}[group]+index*37;
                    return new UsageEvent("stress-"+index,L10n.T("s239B9A6BB3B3")+(group+1),L10n.T("s0A482A3494C9")+(group==0?L10n.T("sD034CC438F1C"):group==1?L10n.T("s19724E06CB20"):L10n.T("s996593E720D7")),group==2?"gpt-5.4-mini":"gpt-5.4",L10n.T("s95CB8B370EBC"),at,at.ToString("yyyy-MM-dd"),new(input,input/3,0,input/5,input/20));
                }).ToList();
            }
            if(this.args.Contains("--preview-single-day"))Events=Events.Select(item=>item with{LocalDate=DateTime.Today.ToString("yyyy-MM-dd"),Timestamp=DateTimeOffset.Now}).ToList();
            if(this.args.Contains("--preview-empty")){Events=[];Quota=QuotaState.LocalAccount;}
            if(this.args.Contains("--preview-weekly"))Quota=new([new("codex:weekly",L10n.T("s475811D50FA9"),18,10080,DateTimeOffset.Now.AddDays(3))],2,DateTimeOffset.Now,L10n.T("sB82002E0639F"),true,"demo","pro");
            if(this.args.Contains("--preview-weekly"))WeeklyCapacity=[new("codex:weekly",L10n.T("s475811D50FA9"),12800000,1280000,10,3,0,L10n.T("sDFBAD24E7F4A"),DateTimeOffset.Now.AddDays(3))];
            Message = L10n.T("sEFF3B8AB8B40");
        }
        else
        {
            _ = LoadHistoryAsync();
            WatchHistory();
            if (Config.AutoRefresh) _ = RefreshQuotaAsync(false);
        }
        timer = queue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(5);
        timer.Tick += (_, _) => { Tick(); _ = CheckAppUpdateAsync(false); }; timer.Start();
        if (!this.args.Contains("--background") || this.args.Contains("--show") || smoke || this.args.Contains("--demo")) ShowDetails();
        if (smoke)
        {
            ToggleFlyout();
            Program.Log.Write("INFO", "Smoke", "Dashboard 与额度弹窗已创建，使用独立模拟数据目录");
        }
        if (this.args.Contains("--smoke-test"))
        {
            smokeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            smokeTimer.Interval = TimeSpan.FromSeconds(8); smokeTimer.IsRepeating = false;
            smokeTimer.Tick += async (_, _) => { Program.Log.Write("INFO", "Smoke", "原生窗口和托盘启动检查完成"); await QuitAsync(); }; smokeTimer.Start();
        }
    }
    private void Tick()
    {
        if(!IsDemo&&DateTimeOffset.UtcNow>=nextCapacityCleanupCheck)
        {
            nextCapacityCleanupCheck=DateTimeOffset.UtcNow.AddHours(1);
            if(cleanupTask is null||cleanupTask.IsCompleted)cleanupTask=CleanupCapacityAsync();
        }
        if (quitting || IsDemo) return;
        var visible = flyout?.IsPanelVisible == true || dashboard?.IsPanelVisible == true;
        var period = SamplingSchedule.QuotaPeriod(visible,DateTimeOffset.Now,lastUsageActivity,Config.ForegroundSeconds,Config.BackgroundSeconds);
        var backoff = Math.Min(1800, period * Math.Pow(2, Math.Min(failures, 5)));
        if (Quota.Fresh && Quota.FetchedAt is {} fetched && DateTimeOffset.Now - fetched > TimeSpan.FromSeconds(Math.Max(60, period * 2)))
        {
            Quota = Quota.SnapshotOnly?Quota.ClearUnverifiedSnapshot(L10n.T("s1F868CB03F9A")):Quota with { Fresh = false, Status = L10n.T("s90968417D7AC") };
            Changed?.Invoke();
        }
        if (Config.AutoRefresh && !Quota.IsLocalAccount && !refreshing && DateTimeOffset.Now - lastAttempt >= TimeSpan.FromSeconds(backoff)) _ = RefreshQuotaAsync(false);
        if (!IsDemo && !scanning && (historyDirty && (DateTimeOffset.Now - historyChangedAt > TimeSpan.FromSeconds(2) || DateTimeOffset.Now - lastHistoryCheck > TimeSpan.FromSeconds(15)) || DateTimeOffset.Now - lastHistoryCheck > TimeSpan.FromMinutes(2)))
        {
            lastHistoryCheck = DateTimeOffset.Now;
            historyDirty = false;
            _ = ScanAsync();
        }
        if(!IsDemo&&!scanning&&!refreshing&&Config.CapacityEnabled&&capacityBatch.Dirty&&DateTimeOffset.UtcNow>=capacityBatch.NextAt)
        {
            var before=WeeklyCapacity;WeeklyCapacity=ObserveCapacity(Quota);
            if(!ReferenceEquals(before,WeeklyCapacity))Changed?.Invoke();
        }
        EvaluateNotifications();
    }
    private async Task CleanupCapacityAsync()
    {
        try
        {
            await capacityWorkGate.WaitAsync(lifetime.Token);
            try
            {
                var cleaned=await Task.Run(()=>store.CleanupCapacity(DateTimeOffset.UtcNow),lifetime.Token);
                if(cleaned.Ran)Program.Log.Write("INFO","CapacityCleanup",$"Retention 30 days; removed snapshots={cleaned.Observations}, intervals={cleaned.Intervals}; estimates retained");
            }
            finally{capacityWorkGate.Release();}
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Program.Log.Write("WARN","CapacityCleanup",ex.Message);}
    }
    private void WatchHistory()
    {
        foreach(var watcher in historyWatchers)watcher.Dispose();historyWatchers.Clear();
        if(IsDemo)return;
        foreach(var leaf in new[]{"sessions","archived_sessions"})
        {
            var directory=Path.Combine(Config.CodexHome,leaf);
            if(!Directory.Exists(directory))continue;
            try
            {
                var watcher=new FileSystemWatcher(directory,"*.jsonl"){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size};
                void Dirty()=>queue.TryEnqueue(()=>{if(!quitting){historyDirty=true;historyChangedAt=DateTimeOffset.Now;}});
                watcher.Changed+=(_,_)=>Dirty();watcher.Created+=(_,_)=>Dirty();watcher.Deleted+=(_,_)=>Dirty();watcher.Renamed+=(_,_)=>Dirty();
                watcher.Error+=(_,_)=>Dirty();watcher.EnableRaisingEvents=true;historyWatchers.Add(watcher);
            }
            catch(Exception ex)when(ex is IOException or ArgumentException or UnauthorizedAccessException){Program.Log.Write("WARN","HistoryWatch",ex.Message);}
        }
    }
    private void ToggleFlyout()
    {
        flyout ??= new Dashboard(this, true);
        if (flyout.IsPanelVisible) flyout.Hide(); else flyout.ShowPanel();
    }
    internal void ShowDetails() { dashboard ??= new Dashboard(this, false); dashboard.ShowPage(IsDemo?PreviewPage:"overview"); }
    internal void ShowSettings() { dashboard ??= new Dashboard(this, false); dashboard.ShowPage("settings"); }
    internal Task RefreshQuotaAsync(bool manual)
    {
        if(IsDemo){Message=L10n.T("s987E3F3AAADE");Changed?.Invoke();return Task.CompletedTask;}
        if (quitting || attributionBusy || Authorizing || refreshing || (!manual && !Config.AutoRefresh)) return quotaTask ?? Task.CompletedTask;
        refreshing = true; lastAttempt = DateTimeOffset.Now;
        Quota=Quota.ClearUnverifiedSnapshot(L10n.T("s5FFA5AB58038"));
        var epoch = configurationGeneration;
        var ct = quotaCancellation.Token;
        quotaTask = RefreshCoreAsync(epoch, ct);
        return quotaTask;
    }
    private async Task RefreshCoreAsync(int epoch, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        Message = L10n.T("s1EA00D373811"); Changed?.Invoke();
        try
        {
            var result = await client.ReadAsync(Config.CliPath, Config.AuthorizedAccount?AuthorizedHome:Config.CodexHome, ct,Config.ReuseBackend&&!Config.AuthorizedAccount,Config.AuthorizedAccount);
            if(!result.Fresh||result.AccountKey is null)restartCheckpoint=null;
            else if(restartCheckpoint is not null)historyDirty=true;
            if (quitting || epoch != configurationGeneration || ct.IsCancellationRequested) return;
            Quota = result; failures = result.Fresh ? 0 : failures + 1;
            RecordCapacityObservation(result);
            SessionNames=new Dictionary<string,string>(client.ThreadNames,StringComparer.Ordinal);
            WeeklyCapacity=ObserveCapacity(result);
            Message = result.IsLocalAccount ? L10n.T("s175D58D46FAC") : L10n.F("s0AB524CB61AE", watch.ElapsedMilliseconds);
            Program.Log.Write(result.Fresh || result.IsLocalAccount ? "INFO" : "WARN", "Quota", $"{result.Status} elapsedMs={watch.ElapsedMilliseconds}");
            if(result.IsLocalAccount)await client.StopAsync();
            EvaluateNotifications();
        }
        catch (OperationCanceledException) { if (epoch == configurationGeneration) Message = L10n.T("sFC3D9710C01E"); }
        catch (Exception ex)
        {
            if (epoch != configurationGeneration || quitting) return;
            restartCheckpoint=null;
            failures++;
            var status=Config.ReuseBackend?L10n.T("s236AB20594D6"):
                ex is CodexRpcException rpc&&rpc.Kind==RpcFailureKind.Authentication?L10n.T("s0F3153A9971E"):
                ex is CodexRpcException?L10n.T("s4D8B77CAC03C"):
                L10n.T("sBF248948A756");
            RecordCapacityObservation(Quota,true,ex is CodexRpcException failure&&(failure.Retryable||failure.Kind==RpcFailureKind.Timeout)||ex is TimeoutException or CodexConnectionClosedException);
            Quota = Quota with{Windows=[],ResetCount=null,FetchedAt=null,Status=status,Fresh=false};
            Message = Privacy.Redact(ex.Message);
            Program.Log.Write("WARN", "Quota", ex.Message);
            await client.StopAsync();
        }
        finally
        {
            SessionNames=new Dictionary<string,string>(client.ThreadNames,StringComparer.Ordinal);
            refreshing = false;
            if (!Config.AutoRefresh) await client.StopAsync();
            if (!quitting) Changed?.Invoke();
        }
    }
    private async Task LoadHistoryAsync()
    {
        var revision=historyRevision;
        try { var events = await Task.Run(() => store.Read(lifetime.Token), lifetime.Token); if (!quitting&&revision==historyRevision) { Events = events;historyLoaded=true;WeeklyCapacity=ObserveCapacity(Quota); Changed?.Invoke(); } }
        catch (Exception ex) { Program.Log.Write("WARN", "Storage", ex.Message); }
    }
    internal Task ScanAsync(bool verifyIntegrity=false,bool rebuild=false)
    {
        if(IsDemo){Message=L10n.T("s04AC6333CEB4");Changed?.Invoke();return Task.CompletedTask;}
        if(rebuild&&scanning){Message=L10n.T("s3706DC826F7D");Changed?.Invoke();return Task.CompletedTask;}
        if (quitting || scanning || attributionBusy) return scanTask ?? Task.CompletedTask;
        scanning = true;
        historyRevision++;
        var home = Config.CodexHome;
        var accountScope=Quota.Fresh&&!Quota.IsLocalAccount?Quota.AccountKey:null;
        scanTask = ScanCoreAsync(home,verifyIntegrity,rebuild,accountScope);
        return scanTask;
    }
    private async Task ScanCoreAsync(string home,bool verifyIntegrity,bool rebuild,string? accountScope)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            Message = L10n.T("s31C4D1614F44"); Changed?.Invoke();
            if(!Directory.Exists(home)&&!rebuild&&!verifyIntegrity)
            {
                Message=L10n.T("sE5936C9E629D");
                Program.Log.Write("INFO","Scan",Message);return;
            }
            var progress = new Progress<string>(text => { if (!quitting) { Message = text; Changed?.Invoke(); } });
            incremental ??= new IncrementalHistory(store);
            var indexed = await Task.Run(() => rebuild?incremental.RebuildAsync(home,lifetime.Token,progress):incremental.ScanAsync(home, lifetime.Token, progress,verifyIntegrity,accountScope), lifetime.Token);
            var report = indexed.Report;
            if(rebuild)restartCheckpoint=null;
            if(restartCheckpoint is {} pending&&Quota.Fresh&&Quota.AccountKey is {} currentAccount)
            {
                var inferred=await Task.Run(()=>store.AttributeRestartGap(pending,currentAccount,home,openedAt,lifetime.Token),lifetime.Token);
                restartCheckpoint=null;
                Program.Log.Write("INFO","Attribution",$"Restart inference completed: {inferred} records; excluded from capacity sampling");
            }
            var previousTokens=Events.Sum(item=>item.Tokens.Total);
            Events = await Task.Run(() => store.Read(lifetime.Token), lifetime.Token);
            if(Events.Sum(item=>item.Tokens.Total)>previousTokens)
            {
                lastUsageActivity=DateTimeOffset.Now;
                Program.Log.Write("INFO","CapacitySampling","检测到新增本地用量，后台采用活跃查询周期；无需打开面板");
            }
            historyLoaded=true;
            preservedHistoryFiles=indexed.PreservedFiles;
            if(!capacityUncertainRanges.SequenceEqual(indexed.UncertainRanges)){capacityEpoch++;capacityBatch.MarkDirty();}
            capacityUncertainRanges=indexed.UncertainRanges;
            capacityHistoryReady=indexed.PreservedFiles==0||indexed.UncertainRanges.Count>0&&indexed.UncertainRanges.All(r=>!r.IsUnknown);
            if(capacityHistoryReady)capacityIndexedThrough=report.ScannedAt;
            WeeklyCapacity=ObserveCapacity(Quota);
            Message = L10n.F("sBF8AACAC3A69", (report.UsedCache ? L10n.T("s38BE587EDD10") : L10n.T("sB164E0EDAEC8")), report.Files, indexed.BytesParsed, report.Warnings, watch.ElapsedMilliseconds);
            if(indexed.PreservedFiles>0)Message+=" · "+PreservedHistoryMessage;
            if(verifyIntegrity)Message=L10n.T("s1AD7D8B010C8")+Message;
            if(indexed.BackupPath is not null)Message=indexed.Migrated
                ?L10n.F("sB389D366B4CF", (indexed.PreviousParserVersions.Contains(0)?L10n.T("sCA6ACDE43454"):L10n.T("s7B2F11B1DAF2")+string.Join(',',indexed.PreviousParserVersions)), Path.GetFileName(indexed.BackupPath), report.Warnings, indexed.DeferredFiles)
                :L10n.F("sBAC47EE47B83", Path.GetFileName(indexed.BackupPath), report.Warnings, indexed.DeferredFiles);
            Program.Log.Write("INFO", "Scan", Message);
        }
        catch (OperationCanceledException) { Message = L10n.T("s0E17A03E2816"); }
        catch (Exception ex) { scanner.InvalidateCache(); Message = Privacy.Redact(ex.Message); Program.Log.Write("ERROR", "Scan", ex.Message); }
        finally { HistoryStatus=Message;scanning = false; if (!quitting) Changed?.Invoke(); }
    }
    internal void SetTheme(string theme)
    {
        Config.Theme=theme;Config.AppearanceConfigured=true;
        if(!IsDemo)Config.Save();
        dashboard?.ApplyTheme();flyout?.ApplyTheme();
    }
    internal void SetLanguage(string language)
    {
        Config.Language=language;
        if(!IsDemo)Config.Save();
        Message=L10n.T("s1D102BCFB482");
        Changed?.Invoke();
    }
    internal async Task SaveSettingsAsync()
    {
        if(Authorizing)throw new InvalidOperationException(L10n.T("s3B5CB5F80AA4"));
        var source=CurrentCapacitySource();
        var sourceChanged=!appliedCapacitySource.Matches(source);
        Config.Save();
        if(!sourceChanged)
        {
            // Ordinary settings must not clear account context or cancel a valid
            // interval. The timer reads the updated refresh/notification settings.
            dashboard?.ApplyTheme();flyout?.ApplyTheme();
            Program.Log.Write("INFO","CapacitySettings","Ordinary settings saved; sampling continuity retained");
            Message=L10n.T("sBD03C0AAD701");Changed?.Invoke();
            if(Config.AutoRefresh)await RefreshQuotaAsync(false);
            return;
        }
        restartCheckpoint=null;
        RecordCapacityObservation(Quota,true);
        Program.Log.Write("INFO","CapacitySettings","Source settings changed; sampling boundary recorded");
        appliedCapacitySource=source;
        capacityEstimator.Reset();WeeklyCapacity=[];SessionNames=new Dictionary<string,string>();
        configurationGeneration++;capacityEpoch++;capacityDisplayIdentity=null;capacityLastCalculated=null;
        quotaCancellation.Cancel();
        if (quotaTask is not null) await quotaTask;
        quotaCancellation.Dispose(); quotaCancellation = new();
        await client.StopAsync();
        WatchHistory();historyDirty=true;lastHistoryCheck=DateTimeOffset.MinValue;
        if(Config.CapacityEnabled&&!IsDemo)RestoreCapacity();
        Quota = new([], null, null, L10n.T("s2A585008799C"), false);
        failures = 0; lastAttempt = DateTimeOffset.MinValue;
        dashboard?.ApplyTheme(); flyout?.ApplyTheme();
        Message = L10n.T("sBD03C0AAD701"); Changed?.Invoke();
        if (Config.AutoRefresh) await RefreshQuotaAsync(false);
    }
    private void EvaluateNotifications()
    {
        if (IsDemo || quitting || Config.ReuseBackend || Config.AuthorizedAccount) return;
        var pending = notifications.Evaluate(Quota, new(Config.LowNotify, Config.ResetNotify, Config.LowPercent, Config.ResetMinutes), DateTimeOffset.Now, TimeSpan.FromMinutes(10));
        if (pending.Count == 0) return;
        Config.NotificationWindows = notifications.Export().ToList();
        try
        {
            Config.Save(); // 去重标记持久化成功后才发送，失败不反复重发。
            foreach (var notification in pending) tray?.Notify(notification.Title, notification.Body);
        }
        catch (Exception ex) { Program.Log.Write("WARN", "Notify", ex.Message); }
    }
    internal async Task QuitAsync()
    {
        if (quitting) return; quitting = true;
        smokeTimer?.Stop(); timer?.Stop();
        foreach(var watcher in historyWatchers)watcher.Dispose();historyWatchers.Clear();
        lifetime.Cancel(); quotaCancellation.Cancel();
        authorizationCancellation?.Cancel();
        flyout?.Release(); dashboard?.Release();
        if (quotaTask is not null) await quotaTask;
        if (authorizationTask is not null) await authorizationTask;
        if (scanTask is not null) await scanTask;
        if (capacityTask is not null) await capacityTask;
        if (cleanupTask is not null) await cleanupTask;
        if (attributionTask is not null){try{await attributionTask;}catch(Exception ex){Program.Log.Write("WARN","Attribution",ex.Message);}}
        if(!IsDemo&&Quota.Fresh)
        {
            try{incremental?.SaveExitCheckpoint(Quota.AccountKey,Config.CodexHome,DateTimeOffset.UtcNow);}
            catch(Exception ex){Program.Log.Write("WARN","Attribution",ex.Message);}
        }
        await client.DisposeAsync();
        tray?.Dispose(); tray = null;
        Program.Log.Write("INFO", "App", "正常退出，托盘已清理");
        Exit();
    }
    internal void CancelAuthorization()=>authorizationCancellation?.Cancel();
    internal async Task UseCachedLoginAsync(string executable,string home)
    {
        if(IsDemo){Message=L10n.T("s70344027B536");Changed?.Invoke();return;}
        if(Authorizing)throw new InvalidOperationException(L10n.T("s77DDD463240B"));
        if(!Path.IsPathFullyQualified(home))throw new ArgumentException(L10n.T("sA279FE7C2774"));
        Config.CliPath=executable;Config.CodexHome=home;
        Config.AuthorizedAccount=false;Config.ReuseBackend=false;
        await SaveSettingsAsync();
        if(!Config.AutoRefresh)await RefreshQuotaAsync(true);
    }
    internal Task AuthorizeAsync(bool logout=false)
    {
        if(IsDemo){Message=L10n.T("s8253A60AB040");Changed?.Invoke();return Task.CompletedTask;}
        if(Authorizing||quitting)return authorizationTask??Task.CompletedTask;
        Authorizing=true;
        authorizationCancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        authorizationTask=AuthorizeCoreAsync(logout,authorizationCancellation.Token);
        return authorizationTask;
    }
    private async Task AuthorizeCoreAsync(bool logout,CancellationToken ct)
    {
        var succeeded=false;
        try
        {
            quotaCancellation.Cancel();if(quotaTask is not null)await quotaTask;
            quotaCancellation.Dispose();quotaCancellation=new();
            await client.StopAsync();
            Config.AuthorizedAccount=true;Config.ReuseBackend=false;Config.Save();
            appliedCapacitySource=CurrentCapacitySource();
            capacityEstimator.Reset();WeeklyCapacity=[];SessionNames=new Dictionary<string,string>();
            Quota=new([],null,null,logout?L10n.T("sFD38695BC486"):L10n.T("sC125127F9996"),false);
            Message=Quota.Status;Changed?.Invoke();
            if(logout)await client.LogoutAuthorizedAsync(Config.CliPath,AuthorizedHome,ct);
            else await client.LoginAsync(Config.CliPath,AuthorizedHome,uri=>Windows.System.Launcher.LaunchUriAsync(uri).AsTask(),ct);
            succeeded=true;
            Quota=QuotaState.LocalAccount;
            Message=logout?L10n.T("sC1DF91681D20"):L10n.T("sC0197453392D");
        }
        catch(OperationCanceledException){Message=L10n.T("s7A52D6684EBC");}
        catch(Exception){Message=L10n.T("s4A7579E47EC3");Program.Log.Write("WARN","Login",client.LastStage);}
        finally
        {
            Authorizing=false;authorizationCancellation?.Dispose();authorizationCancellation=null;
            if(!succeeded)Quota=QuotaState.LocalAccount with{Status=Message};
            if(!quitting)Changed?.Invoke();
        }
        if(succeeded&&!logout&&!quitting)await RefreshQuotaAsync(true);
    }
}

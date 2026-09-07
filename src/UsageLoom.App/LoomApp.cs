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
    private readonly NotificationPolicy notifications = new();
    private readonly WeeklyCapacityEstimator capacityEstimator = new();
    private List<UsageEvent>? capacityPriceEvents;
    private string? capacityPriceAccount;
    private Estimate? capacityPrice;
    private long capacityTokenTotal;
    private bool capacityHistoryReady;
    private IReadOnlyList<WeeklyCapacityEstimate> ObserveCapacity(QuotaState quota)
    {
        if(!capacityHistoryReady)return capacityEstimator.Current;
        if(!ReferenceEquals(capacityPriceEvents,Events)||capacityPriceAccount!=quota.AccountKey)
        {
            var general=CapacityUsage.ForAccount(Events,quota.AccountKey);
            capacityPrice=Pricing.Summarize(general);capacityTokenTotal=general.Sum(item=>item.Tokens.Total);capacityPriceEvents=Events;
            capacityPriceAccount=quota.AccountKey;
        }
        var result=capacityEstimator.Observe(quota,capacityTokenTotal,capacityPrice);
        if(!IsDemo&&quota.Fresh)
        {
            try{store.SaveCapacity(capacityEstimator.Export());}
            catch(Exception ex){Program.Log.Write("WARN","CapacityCache",ex.Message);}
        }
        return result;
    }
    internal Task ResetCapacityAsync()
    {
        if(IsDemo){Message="模拟模式不修改缓存";return Task.CompletedTask;}
        store.SaveCapacity(null);capacityEstimator.Reset();WeeklyCapacity=[];
        if(historyLoaded&&Quota.Fresh)WeeklyCapacity=ObserveCapacity(Quota);
        Message="估算缓存已重置，重新采样；本地 Token 历史未删除";
        Program.Log.Write("INFO","CapacityCache",Message);Changed?.Invoke();return Task.CompletedTask;
    }
    internal Task<List<CapacityCache>> ReadCapacityHistoryAsync(int page)=>IsDemo?Task.FromResult(new List<CapacityCache>()):Task.Run(()=>store.ReadCapacityHistory(page));
    internal string CapacityCacheStatus=>capacityEstimator.RestoredAt is {} at?$"已恢复 {at.ToLocalTime():MM-dd HH:mm} 的样本 · 历史独立保留":"有效样本独立归档；重置当前采样不删除估算历史";
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
    internal QuotaState Quota { get; private set; } = new([], null, null, "本地账户 · 本机 Token 统计无需登录；在线额度尚未查询", false);
    internal List<UsageEvent> Events { get; private set; } = [];
    internal bool HasLoadedHistory => IsDemo||historyLoaded;
    internal IReadOnlyDictionary<string,string> SessionNames { get; private set; }=new Dictionary<string,string>();
    internal IReadOnlyList<WeeklyCapacityEstimate> WeeklyCapacity { get; private set; }=[];
    internal string WeeklyCapacityProgress => capacityEstimator.DescribeProgress(Quota,capacityTokenTotal,capacityHistoryReady);
    internal string HistoryStatus { get; private set; } = "本地 Token 统计 · 无需登录";
    internal string Message { get; private set; } = "正式版 · 不读取凭据、不上传本地数据";
    internal event Action? Changed;
    internal string DiagnosticSummary => Privacy.Redact($"UsageLoom {Program.Version}\n模式：{(Config.AuthorizedAccount?"UsageLoom 独立授权":Config.ReuseBackend?"复用现有后端（实验）":"本机已有缓存（独立进程）")}\n阶段：{client.LastStage}\n登录检测：{client.AccountObservation}\n额度：{Quota.Status}\n事件记录：{Events.Count}\n自动刷新：{Config.AutoRefresh}\nRPC 连接：{client.IsConnected}\n{Message}");
    public LoomApp(string[] args)
    {
        this.args = args;
        InitializeComponent();
        UnhandledException += (_, e) => Program.Log.Write("ERROR", "UI", e.Exception.ToString());
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        queue = DispatcherQueue.GetForCurrentThread();
        if(!IsDemo)
        {
            try{capacityEstimator.Restore(store.ReadCapacity());Program.Log.Write("INFO","CapacityCache","缓存已读取，等待账号、套餐和周窗口核验");}
            catch(Exception ex){Program.Log.Write("WARN","CapacityCache",ex.Message);}
        }
        notifications.Restore(Config.NotificationWindows, DateTimeOffset.Now);
        tray = new TrayIcon(ToggleFlyout, ShowDetails, () => _ = RefreshQuotaAsync(true), () => _ = QuitAsync());
        client.AccountInvalidated += () => queue.TryEnqueue(() =>
        {
            if (quitting) return;
            capacityEstimator.Reset();WeeklyCapacity=[];
            try{capacityEstimator.Restore(store.ReadCapacity());}
            catch(Exception ex){Program.Log.Write("WARN","CapacityCache",ex.Message);}
            Quota = new([], null, null, "账号状态待确认，已撤下旧数据", false);
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
            Quota = new([new("codex:primary", "模拟 · 5 小时额度", 42, 300, DateTimeOffset.Now.AddHours(2)), new("codex:weekly", "模拟 · 每周额度", 18, 10080, DateTimeOffset.Now.AddDays(3))], 2, DateTimeOffset.Now, "模拟数据，仅用于界面验证", true);
            Events = Enumerable.Range(0, 7).Select(day => {
                var at=DateTimeOffset.Now.AddDays(-day); var input=new long[]{48200,36100,62800,24500,56300,41000,18700}[day];
                return new UsageEvent("demo-"+day,"模拟会话 "+(day+1),day%2==0?"示例项目 · Studio":"示例项目 · Notes",day%2==0?"gpt-5.4-mini":"gpt-5.4","主任务",at,at.ToString("yyyy-MM-dd"),new(input,input/3,0,input/5,input/20));
            }).ToList();
            if(this.args.Contains("--preview-session-stress"))
            {
                Events=Enumerable.Range(0,36).Select(index=>
                {
                    var group=index<27?0:index<34?1:2;var at=DateTimeOffset.Now.AddMinutes(-index*9);
                    var input=new long[]{18400,9200,3100}[group]+index*37;
                    return new UsageEvent("stress-"+index,"布局压力 Session "+(group+1),"示例项目 · "+(group==0?"长记录":group==1?"中记录":"短记录"),group==2?"gpt-5.4-mini":"gpt-5.4","主任务",at,at.ToString("yyyy-MM-dd"),new(input,input/3,0,input/5,input/20));
                }).ToList();
            }
            if(this.args.Contains("--preview-single-day"))Events=Events.Select(item=>item with{LocalDate=DateTime.Today.ToString("yyyy-MM-dd"),Timestamp=DateTimeOffset.Now}).ToList();
            if(this.args.Contains("--preview-empty")){Events=[];Quota=QuotaState.LocalAccount;}
            if(this.args.Contains("--preview-weekly"))Quota=new([new("codex:weekly","每周额度",18,10080,DateTimeOffset.Now.AddDays(3))],2,DateTimeOffset.Now,"模拟：仅周窗口",true,"demo","pro");
            if(this.args.Contains("--preview-weekly"))WeeklyCapacity=[new("codex:weekly","每周额度",12800000,1280000,10,3,0,"较高",DateTimeOffset.Now.AddDays(3))];
            Message = "设计预览 · 所有数字均为模拟数据，不连接账号";
        }
        else
        {
            _ = LoadHistoryAsync();
            WatchHistory();
            if (Config.AutoRefresh) _ = RefreshQuotaAsync(false);
        }
        timer = queue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(5);
        timer.Tick += (_, _) => Tick(); timer.Start();
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
        if (quitting || IsDemo) return;
        var visible = flyout?.IsPanelVisible == true || dashboard?.IsPanelVisible == true;
        var period = SamplingSchedule.QuotaPeriod(visible,DateTimeOffset.Now,lastUsageActivity,Config.ForegroundSeconds,Config.BackgroundSeconds);
        var backoff = Math.Min(1800, period * Math.Pow(2, Math.Min(failures, 5)));
        if (Quota.Fresh && Quota.FetchedAt is {} fetched && DateTimeOffset.Now - fetched > TimeSpan.FromSeconds(Math.Max(60, period * 2)))
        {
            Quota = Quota.SnapshotOnly?Quota.ClearUnverifiedSnapshot("本次额度已过期，请刷新；未沿用旧数值"):Quota with { Fresh = false, Status = "额度快照已过期" };
            Changed?.Invoke();
        }
        if (Config.AutoRefresh && !Quota.IsLocalAccount && !refreshing && DateTimeOffset.Now - lastAttempt >= TimeSpan.FromSeconds(backoff)) _ = RefreshQuotaAsync(false);
        if (!IsDemo && !scanning && (historyDirty && (DateTimeOffset.Now - historyChangedAt > TimeSpan.FromSeconds(2) || DateTimeOffset.Now - lastHistoryCheck > TimeSpan.FromSeconds(15)) || DateTimeOffset.Now - lastHistoryCheck > TimeSpan.FromMinutes(2)))
        {
            lastHistoryCheck = DateTimeOffset.Now;
            historyDirty = false;
            _ = ScanAsync();
        }
        EvaluateNotifications();
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
    internal void ShowDetails() { dashboard ??= new Dashboard(this, false); dashboard.ShowPanel(); }
    internal Task RefreshQuotaAsync(bool manual)
    {
        if(IsDemo){Message="设计预览不执行真实查询，展示的是模拟数据。";Changed?.Invoke();return Task.CompletedTask;}
        if (quitting || Authorizing || refreshing || (!manual && !Config.AutoRefresh)) return quotaTask ?? Task.CompletedTask;
        refreshing = true; lastAttempt = DateTimeOffset.Now;
        Quota=Quota.ClearUnverifiedSnapshot("正在重新核对本机账号和额度；已撤下上次快照");
        var epoch = configurationGeneration;
        var ct = quotaCancellation.Token;
        quotaTask = RefreshCoreAsync(epoch, ct);
        return quotaTask;
    }
    private async Task RefreshCoreAsync(int epoch, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        Message = "正在只读查询当前 CLI 额度…"; Changed?.Invoke();
        try
        {
            var result = await client.ReadAsync(Config.CliPath, Config.AuthorizedAccount?AuthorizedHome:Config.CodexHome, ct,Config.ReuseBackend&&!Config.AuthorizedAccount,Config.AuthorizedAccount);
            if (quitting || epoch != configurationGeneration || ct.IsCancellationRequested) return;
            Quota = result; failures = result.Fresh ? 0 : failures + 1;
            SessionNames=new Dictionary<string,string>(client.ThreadNames,StringComparer.Ordinal);
            if(historyLoaded)WeeklyCapacity=ObserveCapacity(result);
            Message = result.IsLocalAccount ? "本地账户 · Token 统计正常可用；登录后可手动检查额度" : $"额度检查完成 · {watch.ElapsedMilliseconds} ms";
            Program.Log.Write(result.Fresh || result.IsLocalAccount ? "INFO" : "WARN", "Quota", $"{result.Status} elapsedMs={watch.ElapsedMilliseconds}");
            if(result.IsLocalAccount)await client.StopAsync();
            EvaluateNotifications();
        }
        catch (OperationCanceledException) { if (epoch == configurationGeneration) Message = "额度查询已取消"; }
        catch (Exception ex)
        {
            if (epoch != configurationGeneration || quitting) return;
            failures++;
            var status=Config.ReuseBackend?"现有后端连接未成功；未回退到独立 CLI，本地统计仍可用":
                ex is CodexRpcException rpc&&rpc.Kind==RpcFailureKind.Authentication?"已识别 ChatGPT 登录，但现有登录缓存已失效；本地 Token 统计仍可用":
                ex is CodexRpcException?"额度查询未完成，登录状态待确认；本地 Token 统计仍可用":
                "查询失败，账号状态待确认";
            Quota = new([], null, null, status, false);
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
        if(IsDemo){Message="设计预览不扫描本机日志，展示的是模拟数据。";Changed?.Invoke();return Task.CompletedTask;}
        if(rebuild&&scanning){Message="已有扫描正在运行，请完成后再重建。";Changed?.Invoke();return Task.CompletedTask;}
        if (quitting || scanning) return scanTask ?? Task.CompletedTask;
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
            Message = "扫描本机日志中；不会读取标题索引或凭据。"; Changed?.Invoke();
            if(!Directory.Exists(home)&&!rebuild&&!verifyIntegrity)
            {
                Message="尚未发现本地日志目录；可在设置中选择日志位置。已有统计会保留，无需登录。";
                Program.Log.Write("INFO","Scan",Message);return;
            }
            var progress = new Progress<string>(text => { if (!quitting) { Message = text; Changed?.Invoke(); } });
            incremental ??= new IncrementalHistory(store);
            var indexed = await Task.Run(() => rebuild?incremental.RebuildAsync(home,lifetime.Token,progress):incremental.ScanAsync(home, lifetime.Token, progress,verifyIntegrity,accountScope), lifetime.Token);
            var report = indexed.Report;
            var previousTokens=Events.Sum(item=>item.Tokens.Total);
            Events = await Task.Run(() => store.Read(lifetime.Token), lifetime.Token);
            if(Events.Sum(item=>item.Tokens.Total)>previousTokens)
            {
                lastUsageActivity=DateTimeOffset.Now;
                Program.Log.Write("INFO","CapacitySampling","检测到新增本地用量，后台采用活跃查询周期；无需打开面板");
            }
            historyLoaded=true;
            capacityHistoryReady=true;
            WeeklyCapacity=ObserveCapacity(Quota);
            Message = $"{(report.UsedCache ? "文件无变化，使用持久化索引" : "增量索引完成")}：{report.Files} 文件，本次解析 {indexed.BytesParsed:N0} 字节，{report.Warnings} 项警告 · {watch.ElapsedMilliseconds} ms";
            if(verifyIntegrity)Message="历史前缀完整校验通过 · "+Message;
            if(indexed.BackupPath is not null)Message=indexed.Migrated
                ?$"{(indexed.PreviousParserVersions.Contains(0)?"未版本化历史":"旧索引 v"+string.Join(',',indexed.PreviousParserVersions))}已安全迁移；已从原始日志重建，旧统计备份为 backups / {Path.GetFileName(indexed.BackupPath)}。{report.Warnings} 项数据质量提示未阻断可恢复记录；{indexed.DeferredFiles} 个活动文件的尾部将在下次增量补齐。"
                :$"重建完成；旧统计已备份到应用数据目录 backups / {Path.GetFileName(indexed.BackupPath)}。{report.Warnings} 项数据质量提示未阻断可恢复记录；{indexed.DeferredFiles} 个活动文件的尾部将在下次增量补齐。";
            Program.Log.Write("INFO", "Scan", Message);
        }
        catch (OperationCanceledException) { Message = "扫描已取消"; }
        catch (Exception ex) { scanner.InvalidateCache(); Message = Privacy.Redact(ex.Message); Program.Log.Write("ERROR", "Scan", ex.Message); }
        finally { HistoryStatus=Message;scanning = false; if (!quitting) Changed?.Invoke(); }
    }
    internal async Task SaveSettingsAsync()
    {
        if(Authorizing)throw new InvalidOperationException("请先完成或取消浏览器授权");
        capacityEstimator.Reset();WeeklyCapacity=[];SessionNames=new Dictionary<string,string>();
        Config.Save(); configurationGeneration++;
        quotaCancellation.Cancel();
        if (quotaTask is not null) await quotaTask;
        quotaCancellation.Dispose(); quotaCancellation = new();
        await client.StopAsync();
        WatchHistory();historyDirty=true;lastHistoryCheck=DateTimeOffset.MinValue;
        Quota = new([], null, null, "设置已保存；等待当前账号查询", false);
        failures = 0; lastAttempt = DateTimeOffset.MinValue;
        dashboard?.ApplyTheme(); flyout?.ApplyTheme();
        Message = "设置已保存"; Changed?.Invoke();
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
    private async Task QuitAsync()
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
        await client.DisposeAsync();
        tray?.Dispose(); tray = null;
        Program.Log.Write("INFO", "App", "正常退出，托盘已清理");
        Exit();
    }
    internal void CancelAuthorization()=>authorizationCancellation?.Cancel();
    internal async Task UseCachedLoginAsync(string executable,string home)
    {
        if(IsDemo){Message="模拟模式不检查真实登录缓存";Changed?.Invoke();return;}
        if(Authorizing)throw new InvalidOperationException("请先取消或完成当前授权");
        if(!Path.IsPathFullyQualified(home))throw new ArgumentException("请填写本机 Codex Home 完整目录");
        Config.CliPath=executable;Config.CodexHome=home;
        Config.AuthorizedAccount=false;Config.ReuseBackend=false;
        capacityEstimator.Reset();WeeklyCapacity=[];SessionNames=new Dictionary<string,string>();
        await SaveSettingsAsync();
        if(!Config.AutoRefresh)await RefreshQuotaAsync(true);
    }
    internal Task AuthorizeAsync(bool logout=false)
    {
        if(IsDemo){Message="模拟预览不登录或退出账号";Changed?.Invoke();return Task.CompletedTask;}
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
            capacityEstimator.Reset();WeeklyCapacity=[];SessionNames=new Dictionary<string,string>();
            Quota=new([],null,null,logout?"正在退出 UsageLoom 授权账号":"请在浏览器完成官方授权，本地统计不受影响",false);
            Message=Quota.Status;Changed?.Invoke();
            if(logout)await client.LogoutAuthorizedAsync(Config.CliPath,AuthorizedHome,ct);
            else await client.LoginAsync(Config.CliPath,AuthorizedHome,uri=>Windows.System.Launcher.LaunchUriAsync(uri).AsTask(),ct);
            succeeded=true;
            Quota=QuotaState.LocalAccount;
            Message=logout?"已退出 UsageLoom 专用授权，不影响桌面 App 登录":"官方登录完成，正在检查授权账号额度";
        }
        catch(OperationCanceledException){Message="授权已取消或超时；未改变本地统计";}
        catch(Exception){Message="官方授权未完成；请核对后端路径及浏览器授权结果。诊断仅记录失败阶段。";Program.Log.Write("WARN","Login",client.LastStage);}
        finally
        {
            Authorizing=false;authorizationCancellation?.Dispose();authorizationCancellation=null;
            if(!succeeded)Quota=QuotaState.LocalAccount with{Status=Message};
            if(!quitting)Changed?.Invoke();
        }
        if(succeeded&&!logout&&!quitting)await RefreshQuotaAsync(true);
    }
}

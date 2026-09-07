using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed class Dashboard : Window
{
    private readonly LoomApp app;
    private readonly StackPanel quotaPanel=new(){Spacing=14};
    private readonly StackPanel statsPanel=new(){Spacing=16};
    private StackPanel overviewQuota=new(){Spacing=10};
    private Border overviewQuotaCard;
    private Action? updateOverviewLayout;
    private List<UsageEvent>? renderedEvents;
    private string? renderedFilter;
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,FontSize=13};
    private readonly StackPanel historyRangeButtons=new(){Orientation=Orientation.Horizontal,Spacing=4};
    private readonly List<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton> historyRangeButtonList=[];
    private int historyRangeIndex=1;
    private bool updatingHistoryRangeControls;
    private readonly TextBox historySearch=new(){PlaceholderText="筛选 Session、模型、项目或 Agent",MinWidth=200};
    private readonly CalendarDatePicker historyFrom=new(){Header="开始",PlaceholderText="开始日期"};
    private readonly CalendarDatePicker historyThrough=new(){Header="结束",PlaceholderText="结束日期"};
    private readonly ComboBox sessionOrder=new(){ItemsSource=new[]{"用量从高到低","最近活动","Session 名称"},SelectedIndex=0,Width=180};
    private readonly StackPanel dateControls=new(){Orientation=Orientation.Horizontal,Spacing=10};
    private readonly Grid filterBar;
    private readonly ComboBox breakdownKind=new(){ItemsSource=new[]{"模型","项目","Agent 来源"},SelectedIndex=0,Width=160};
    private int sessionPage;
    private readonly NavigationView navigation = new() { IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = false, OpenPaneLength = 208, PaneDisplayMode = NavigationViewPaneDisplayMode.Auto, ExpandedModeThresholdWidth=1000, CompactModeThresholdWidth=0 };
    private readonly ContentControl page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, HorizontalAlignment=HorizontalAlignment.Stretch };
    private readonly ScrollViewer pageScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly ContentControl scrollContent = new() { HorizontalContentAlignment=HorizontalAlignment.Stretch, VerticalContentAlignment=VerticalAlignment.Top, MaxWidth=1600, HorizontalAlignment=HorizontalAlignment.Left };
    private readonly Grid sessionWorkspace = new() { MaxWidth=1600, HorizontalAlignment=HorizontalAlignment.Left, VerticalAlignment=VerticalAlignment.Stretch, RowSpacing=12 };
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly TextBlock pageTitle = new() { FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock pageSubtitle = new() { FontSize = 13, Opacity = .65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock paneFooter = new() { Opacity = .55, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<WeakReference<Border>> surfaces = [];
    private readonly SolidColorBrush accent = new(ColorHelper.FromArgb(255, 163, 138, 245));
    private string selectedPage = "overview";
    private readonly bool compact;
    public bool IsPanelVisible {get;private set;}
    private bool released;
    private bool rendering;
    private bool navigationReady;
    private bool pinned;
    private int minimumWindowWidthPixels;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? navigationTest;
    public Dashboard(LoomApp app,bool compact)
    {
        Program.Log.Write("INFO", "UI", "构建界面开始 compact="+compact);
        this.app=app;this.compact=compact;Title=compact?"UsageLoom · 当前额度":"UsageLoom · 本地用量中心";
        if(app.PreviewHourly)historyRangeIndex=0;
        overviewQuotaCard=Card(overviewQuota);
        historyFrom.Date=DateTimeOffset.Now.Date.AddDays(-6);historyThrough.Date=DateTimeOffset.Now.Date;
        dateControls.Children.Add(historyFrom);dateControls.Children.Add(historyThrough);
        foreach(var (label,index) in new[]{("日",0),("近 7 天",1),("周",2),("月",3),("自定义",4)})
        {
            var button=new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton{Content=label,Padding=new Thickness(12,6,12,6),CornerRadius=new CornerRadius(8),MinWidth=42};
            button.Click+=(_,_)=>SetHistoryRange(index);historyRangeButtonList.Add(button);historyRangeButtons.Children.Add(button);
        }
        filterBar=new Grid{ColumnSpacing=12,RowSpacing=8};
        filterBar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});filterBar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        filterBar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});filterBar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});filterBar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        filterBar.Children.Add(historyRangeButtons);filterBar.Children.Add(dateControls);Grid.SetColumn(dateControls,1);dateControls.Visibility=Visibility.Collapsed;
        filterBar.Children.Add(historySearch);Grid.SetRow(historySearch,1);Grid.SetColumnSpan(historySearch,2);
        historyRangeButtons.VerticalAlignment=VerticalAlignment.Center;
        filterBar.SizeChanged+=(_,e)=>
        {
            var narrow=e.NewSize.Width<720;Grid.SetColumn(dateControls,narrow?0:1);Grid.SetRow(dateControls,narrow?1:0);Grid.SetColumnSpan(dateControls,narrow?2:1);
            Grid.SetRow(historySearch,narrow?2:1);
        };
        dateControls.SizeChanged+=(_,e)=>dateControls.Orientation=e.NewSize.Width<320?Orientation.Vertical:Orientation.Horizontal;
        UpdateHistoryRangeControls();
        SystemBackdrop = compact ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titlebar = new Grid { Height = 48, Padding = new Thickness(20, 0, 140, 0) };
        var titleIdentity=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,VerticalAlignment=VerticalAlignment.Center};
        titleIdentity.Children.Add(new TextBlock { Text = "L   UsageLoom", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var versionBadge=new Border{Padding=new Thickness(7,2,7,2),CornerRadius=new CornerRadius(7),Background=new SolidColorBrush(ColorHelper.FromArgb(28,127,132,150)),Child=new TextBlock{Text=Program.Version,FontSize=10.5,Opacity=.75,TextWrapping=TextWrapping.NoWrap,VerticalAlignment=VerticalAlignment.Center}};
        ToolTipService.SetToolTip(versionBadge,"UsageLoom "+Program.Version+" · 正式版");titleIdentity.Children.Add(versionBadge);titlebar.Children.Add(titleIdentity);
        root.Children.Add(titlebar);
        ExtendsContentIntoTitleBar = true; SetTitleBar(titlebar);
        var body = new Grid { Padding = compact?new Thickness(24,20,24,24):new Thickness(0,20,0,24) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var headingText = new StackPanel { Spacing = 4 };
        headingText.Children.Add(pageTitle);headingText.Children.Add(pageSubtitle);
        var heading = new Grid{ColumnSpacing=16,RowSpacing=10,Margin=new Thickness(compact?0:32,0,compact?0:32,20),MaxWidth=1600,HorizontalAlignment=compact?HorizontalAlignment.Stretch:HorizontalAlignment.Left};
        heading.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        heading.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        heading.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        heading.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        heading.Children.Add(headingText);heading.Children.Add(actions);
        body.SizeChanged+=(_,e)=>
        {
            var narrow=e.NewSize.Width<620;
            var inset=narrow?16:24;
            body.Padding=compact?new Thickness(inset,16,inset,16):new Thickness(0,16,0,16);
            if(compact){quotaPanel.Width=Math.Max(280,e.NewSize.Width-inset*2);DispatcherQueue.TryEnqueue(FitCompactHeight);}
            heading.Margin=compact?new Thickness(0,0,0,12):new Thickness(inset,0,inset,20);
            scrollContent.Margin=new Thickness(inset,0,inset,0);
            sessionWorkspace.Margin=new Thickness(inset,0,inset,0);
            status.Margin=compact?new Thickness(0,12,0,0):new Thickness(inset,12,inset,0);
            if(!compact)
            {
                var contentWidth=Math.Max(0,Math.Min(1600,e.NewSize.Width-inset*2));
                heading.Width=contentWidth;
                scrollContent.Width=contentWidth;
                sessionWorkspace.Width=contentWidth;
                status.Width=contentWidth;
            }
            pageTitle.FontSize=compact?16:narrow?24:30;
            pageSubtitle.Visibility=compact||narrow?Visibility.Collapsed:Visibility.Visible;
            Grid.SetColumn(actions,!compact&&narrow?0:1);Grid.SetRow(actions,!compact&&narrow?1:0);
            Grid.SetColumnSpan(headingText,!compact&&narrow?2:1);
            actions.HorizontalAlignment=narrow?HorizontalAlignment.Left:HorizontalAlignment.Right;
        };
        actions.Children.Add(Button(compact?"刷新":"刷新额度",async()=>await app.RefreshQuotaAsync(true)));
        if(compact)
        {
            actions.Spacing=6;
            actions.Children.Add(Button("详情",()=>{app.ShowDetails();if(!pinned)Hide();return Task.CompletedTask;}));
            var pin=new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton{Content="置顶",Padding=new Thickness(9,6,9,6)};
            ToolTipService.SetToolTip(pin,"保持在其他窗口上方；点击外部不隐藏");
            pin.Click+=(_,_)=>{pinned=pin.IsChecked==true;if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)presenter.IsAlwaysOnTop=pinned;};
            actions.Children.Add(pin);
        }
        else actions.Children.Add(Button("扫描本地日志",async()=>await app.ScanAsync()));
        body.Children.Add(heading);
        body.RowDefinitions[1].Height=new GridLength(1,GridUnitType.Star);
        body.RowDefinitions[2].Height=GridLength.Auto;
        status.Opacity = .65; status.MaxLines=1;status.FontSize=12;status.TextTrimming=TextTrimming.CharacterEllipsis;
        status.Margin = new Thickness(compact?0:32, 12, compact?0:32, 0); status.MaxWidth=1600;status.HorizontalAlignment=compact?HorizontalAlignment.Stretch:HorizontalAlignment.Left;Grid.SetRow(status, 2); body.Children.Add(status);
        Grid.SetRow(page, 1); body.Children.Add(page);
        if(compact)
        {
            pageTitle.Text = "用量速览";pageTitle.FontSize=16;pageSubtitle.Visibility=Visibility.Collapsed;status.Visibility=Visibility.Collapsed;
            quotaPanel.Spacing=10;
            quotaPanel.SizeChanged+=(_,_)=>DispatcherQueue.TryEnqueue(FitCompactHeight);
            page.Content = new Viewbox { Child = quotaPanel, Stretch=Stretch.Uniform, StretchDirection=StretchDirection.DownOnly,VerticalAlignment=VerticalAlignment.Top,HorizontalAlignment=HorizontalAlignment.Stretch };
            Grid.SetRow(body, 1); root.Children.Add(body);
        }
        else
        {
            pageScroll.Content=scrollContent;
            navigation.PaneHeader = new TextBlock { Text = "工作台", Margin = new Thickness(16, 16, 0, 12), Opacity = .6, FontSize = 12 };
            foreach (var (label, tag, icon) in new[] { ("概览", "overview", Symbol.Home), ("当前额度", "quota", Symbol.Clock), ("模型与项目", "breakdown", Symbol.Library), ("Session 明细", "sessions", Symbol.Document), ("设置", "settings", Symbol.Setting), ("诊断与关于", "about", Symbol.Help) })
            {
                var item=new NavigationViewItem { Content = label, Tag = tag, Icon = new SymbolIcon(icon) };
                if(tag is "settings" or "about")navigation.FooterMenuItems.Add(item);else navigation.MenuItems.Add(item);
            }
            navigation.PaneFooter = paneFooter;
            ToolTipService.SetToolTip(paneFooter,"UsageLoom "+Program.Version+" · 本地优先 · 正式版");
            navigation.PaneOpened+=(_,_)=>UpdatePaneFooter();navigation.PaneClosed+=(_,_)=>UpdatePaneFooter();navigation.SizeChanged+=(_,_)=>UpdatePaneFooter();UpdatePaneFooter();
            navigation.Content = body;
            navigation.SelectionChanged += (_, e) => { if (e.SelectedItem is NavigationViewItem item) { selectedPage = (string)item.Tag; SelectPage(); } };
            Grid.SetRow(navigation, 1); root.Children.Add(navigation);
            historySearch.TextChanged+=(_,_)=>{sessionPage=0;Render();};
            historyFrom.DateChanged+=(_,_)=>{if(!updatingHistoryRangeControls&&historyRangeIndex==4){sessionPage=0;Render();}};
            historyThrough.DateChanged+=(_,_)=>{if(!updatingHistoryRangeControls&&historyRangeIndex==4){sessionPage=0;Render();}};
            sessionOrder.SelectionChanged+=(_,_)=>{sessionPage=0;Render();};
            breakdownKind.SelectionChanged+=(_,_)=>Render();
            navigation.Loaded += InitializeNavigation;
        }
        Content=root;ApplyTheme();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"UsageLoom.ico"));
        ApplyMinimumWindowWidth();
        AppWindow.Changed+=(_,_)=>ApplyMinimumWindowWidth();
        Program.Log.Write("INFO", "UI", "界面布局已构建 compact="+compact);
        root.ActualThemeChanged += (_, _) => RefreshSurfaces();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(compact?380:app.PreviewNarrow?620:app.PreviewWide?2048:1280,compact?400:app.PreviewNarrow?640:app.PreviewWide?1100:900));
        AppWindow.Closing+=(_,e)=>{e.Cancel=true;Hide();};
        Activated+=(_,e)=>
        {
            if(compact&&!pinned&&e.WindowActivationState==WindowActivationState.Deactivated)
                DispatcherQueue.TryEnqueue(()=>{Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out var pid);if(!pinned&&pid!=Environment.ProcessId)Hide();});
        };
        app.Changed+=Render;Render();
    }
    private void ApplyMinimumWindowWidth()
    {
        if(AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter presenter)return;
        var minimumWidthDip=compact?380:620;
        var dpi=Math.Max(96u,Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)));
        var minimumWidthPixels=(int)Math.Ceiling(minimumWidthDip*dpi/96d);
        if(minimumWidthPixels==minimumWindowWidthPixels)return;
        presenter.PreferredMinimumWidth=minimumWidthPixels;
        minimumWindowWidthPixels=minimumWidthPixels;
        Program.Log.Write("INFO","UI",$"最小窗口宽度已设置：{minimumWidthDip} DIP ({minimumWidthPixels} px)");
    }
    private void InitializeNavigation(object sender,RoutedEventArgs e)
    {
        if(released||navigationReady)return;
        // Selection invokes page creation synchronously. Wait until templates and XamlRoot exist.
        navigationReady=true;
        navigation.Loaded-=InitializeNavigation;
        Program.Log.Write("INFO","Navigation","Loaded: initializing selected page");
        var initial=navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(item=>(string)item.Tag==app.PreviewPage)
            ??(NavigationViewItem)navigation.MenuItems[0];
        navigation.SelectedItem=initial;
        Program.Log.Write("INFO","Navigation","Initial page ready: "+selectedPage);
        if(app.NavigationCheck)
        {
            var step=0;var route=new[]{"sessions","overview","quota","overview","breakdown","overview","settings","overview","about","overview","overview","overview"};
            navigationTest=DispatcherQueue.CreateTimer();navigationTest.Interval=TimeSpan.FromMilliseconds(200);
            navigationTest.Tick+=(_,_)=>
            {
                if(step>=route.Length){navigationTest.Stop();Program.Log.Write("INFO","NavigationTest","Repeated navigation passed");return;}
                Program.Log.Write("INFO","NavigationTest","Visit "+route[step]);Navigate(route[step++]);
            };navigationTest.Start();
        }
    }
    private void UpdatePaneFooter()
    {
        var expanded=navigation.IsPaneOpen;
        paneFooter.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;
        paneFooter.Text="本地优先 · 只读访问\n"+Program.Version+" 正式版";
        paneFooter.FontSize=12;
        paneFooter.Margin=new Thickness(16,20,8,24);
        paneFooter.TextAlignment=TextAlignment.Left;
        paneFooter.TextWrapping=TextWrapping.NoWrap;
    }
    public void ShowPanel()
    {
        if(compact)
        {
            Native.GetCursorPos(out var point);var info=new Native.MonitorInfo{size=(uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>()};
            if(Native.GetMonitorInfo(Native.MonitorFromPoint(point,2),ref info))
            {
                var scale=Math.Max(1,Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96d);
                var width=Math.Min((int)(380*scale),info.work.right-info.work.left-24);var height=Math.Min((int)(400*scale),info.work.bottom-info.work.top-24);
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(Math.Max(info.work.left,info.work.right-width-12),Math.Max(info.work.top,info.work.bottom-height-12),width,height));
            }
        }
        IsPanelVisible=true;Activate();AppWindow.Show();Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));Render();
        if(compact)DispatcherQueue.TryEnqueue(FitCompactHeight);
    }
    private void FitCompactHeight()
    {
        if(!compact||released||!IsPanelVisible||quotaPanel.ActualHeight<=0)return;
        var scale=Math.Max(1,Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96d);
        var position=AppWindow.Position;
        var point=new Native.Point{x=position.X,y=position.Y};
        var info=new Native.MonitorInfo{size=(uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>()};
        if(!Native.GetMonitorInfo(Native.MonitorFromPoint(point,2),ref info))return;
        quotaPanel.Measure(new Windows.Foundation.Size(double.IsNaN(quotaPanel.Width)?400:quotaPanel.Width,double.PositiveInfinity));
        var height=Math.Min((int)Math.Ceiling((quotaPanel.DesiredSize.Height+136)*scale),info.work.bottom-info.work.top-24);
        if(Math.Abs(AppWindow.Size.Height-height)<3)return;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(position.X,Math.Max(info.work.top+12,info.work.bottom-height-12),AppWindow.Size.Width,height));
    }
    public void Hide(){if(released)return;IsPanelVisible=false;AppWindow.Hide();}
    private void Navigate(string tag)=>navigation.SelectedItem=navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>().First(i=>(string)i.Tag==tag);
    public void Release(){released=true;navigationTest?.Stop();navigation.Loaded-=InitializeNavigation;app.Changed-=Render;}
    public void ApplyTheme(){if(Content is FrameworkElement element)element.RequestedTheme=Enum.TryParse<ElementTheme>(app.Config.Theme,out var theme)?theme:ElementTheme.Default;RefreshSurfaces();}
    private void SelectPage()
    {
        // The filter contains stateful WinUI controls and is shared by overview,
        // breakdown and session workspaces. Detach it while the old page is still
        // connected to the visual tree; detached subtrees do not expose a visual parent.
        Detach(filterBar);
        var (title, subtitle) = selectedPage switch {
            "quota" => ("当前额度", "本机 Token 统计无需登录 · 在线额度独立显示"),
            "breakdown" => ("模型与项目", "从总量到细节，看清每一份用量。"),
            "sessions" => ("Session 明细", "筛选本机历史，按会话查看模型和 Token 明细。"),
            "settings" => ("设置", "按你的节奏，保持轻巧与安静。"),
            "about" => ("诊断与关于", "透明的数据来源，清晰的隐私边界。"),
            _ => ("用量概览", "把分散的使用记录，织成清晰的视图。") };
        pageTitle.Text = title; pageSubtitle.Text = subtitle;
        actions.Children.Clear();
        if(selectedPage=="quota")actions.Children.Add(Button("刷新额度",async()=>await app.RefreshQuotaAsync(true)));
        else if(selectedPage is "overview" or "breakdown" or "sessions")actions.Children.Add(Button("扫描本地日志",async()=>await app.ScanAsync()));
        actions.Visibility=actions.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
        foreach(var button in actions.Children.OfType<Button>()){button.Background=accent;button.Foreground=new SolidColorBrush(ColorHelper.FromArgb(255,24,20,36));}
        var nextContent = selectedPage switch { "quota" => quotaPanel, "settings" => SettingsPanel(), "about" => AboutPanel(), _ => statsPanel };
        if(selectedPage=="sessions")page.Content=sessionWorkspace;
        else
        {
            scrollContent.Content=null;
            scrollContent.Content=nextContent;
            page.Content=pageScroll;
        }
        actions.Visibility=actions.Children.Count==0?Visibility.Collapsed:Visibility.Visible;
        foreach(var button in actions.Children.OfType<Button>()){button.Background=accent;button.Foreground=new SolidColorBrush(ColorHelper.FromArgb(255,24,20,36));}
        pageScroll.ChangeView(null, 0, null);
        Render();
    }
    private Border Card(UIElement child)
    {
        var card = new Border { Child = child, Padding = new Thickness(20), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch };
        surfaces.Add(new WeakReference<Border>(card)); Paint(card); return card;
    }
    private void Paint(Border card)
    {
        if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            var colors = new Windows.UI.ViewManagement.UISettings();
            card.Background = new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background));
            card.BorderBrush = new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
            return;
        }
        var dark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        card.Background = new SolidColorBrush(dark ? ColorHelper.FromArgb(255, 41, 45, 53) : ColorHelper.FromArgb(255, 255, 255, 255));
        card.BorderBrush = new SolidColorBrush(dark ? ColorHelper.FromArgb(30, 255, 255, 255) : ColorHelper.FromArgb(18, 20, 20, 35));
    }
    private void RefreshSurfaces()
    {
        surfaces.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in surfaces) if(reference.TryGetTarget(out var card)) Paint(card);
        var dark=(Content as FrameworkElement)?.ActualTheme==ElementTheme.Dark;
        if(Content is Grid root&&!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            root.Background=new SolidColorBrush(dark?ColorHelper.FromArgb(255,32,35,41):ColorHelper.FromArgb(255,245,246,249));
            navigation.Background=new SolidColorBrush(dark?ColorHelper.FromArgb(255,25,28,34):ColorHelper.FromArgb(255,239,241,246));
        }
        AppWindow.TitleBar.ButtonForegroundColor=dark?Colors.White:Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor=dark?Colors.LightGray:Colors.DimGray;
        AppWindow.TitleBar.ButtonBackgroundColor=Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor=Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor=dark?ColorHelper.FromArgb(70,255,255,255):ColorHelper.FromArgb(25,0,0,0);
        AppWindow.TitleBar.ButtonHoverForegroundColor=dark?Colors.White:Colors.Black;
    }
    private static Grid ResponsiveCards(IReadOnlyList<UIElement> cards, int maximumColumns, double minimumWidth,double firstWeight=1)
    {
        var grid=new Grid{ColumnSpacing=16,RowSpacing=16};var previous=-1;
        void Layout(double width)
        {
            var columns=Math.Clamp((int)((Math.Max(width,minimumWidth)+16)/(minimumWidth+16)),1,Math.Max(1,Math.Min(maximumColumns,cards.Count)));
            if(columns==previous)return;previous=columns;grid.ColumnDefinitions.Clear();grid.RowDefinitions.Clear();
            for(var i=0;i<columns;i++)grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(i==0&&columns>1?firstWeight:1,GridUnitType.Star)});
            for(var i=0;i<(cards.Count+columns-1)/columns;i++)grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            for(var i=0;i<cards.Count;i++){Grid.SetColumn((FrameworkElement)cards[i],i%columns);Grid.SetRow((FrameworkElement)cards[i],i/columns);}
        }
        foreach(var card in cards)grid.Children.Add(card);
        grid.SizeChanged+=(_,e)=>Layout(e.NewSize.Width);Layout(1000);return grid;
    }
    private static void Detach(UIElement element)
    {
        var parent=(element as FrameworkElement)?.Parent??VisualTreeHelper.GetParent(element);
        switch(parent)
        {
            case Panel panel:panel.Children.Remove(element);break;
            case Border border when ReferenceEquals(border.Child,element):border.Child=null;break;
            case ContentControl content when ReferenceEquals(content.Content,element):content.Content=null;break;
        }
    }
    private void SetHistoryRange(int index)
    {
        historyRangeIndex=Math.Clamp(index,0,4);sessionPage=0;renderedFilter=null;UpdateHistoryRangeControls();Render();
    }
    private void UpdateHistoryRangeControls()
    {
        for(var i=0;i<historyRangeButtonList.Count;i++)historyRangeButtonList[i].IsChecked=i==historyRangeIndex;
        dateControls.Visibility=historyRangeIndex==4?Visibility.Visible:Visibility.Collapsed;
    }
    private HistoryDateRange SelectedHistoryRange()
    {
        var kind=historyRangeIndex switch{0=>HistoryRangeKind.Day,1=>HistoryRangeKind.Rolling7Days,2=>HistoryRangeKind.Week,3=>HistoryRangeKind.Month,_=>HistoryRangeKind.Custom};
        DateOnly? Read(CalendarDatePicker picker)=>picker.Date is {} date?DateOnly.FromDateTime(date.LocalDateTime):null;
        return HistoryQuery.ResolveRange(kind,DateOnly.FromDateTime(DateTime.Today),Read(historyFrom),Read(historyThrough));
    }
    private void DrillIntoRange(DateOnly from,DateOnly through)
    {
        updatingHistoryRangeControls=true;
        try
        {
            historyFrom.Date=new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue));historyThrough.Date=new DateTimeOffset(through.ToDateTime(TimeOnly.MinValue));historyRangeIndex=4;UpdateHistoryRangeControls();
        }
        finally{updatingHistoryRangeControls=false;}
        sessionPage=0;renderedFilter=null;Navigate("sessions");
    }
    private UIElement Metric(string label, string value, string detail)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 13, Opacity = .65 });
        var display=double.TryParse(value,System.Globalization.NumberStyles.AllowThousands|System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.CurrentCulture,out var numeric)?UsageNumbers.Compact(numeric):value;
        var number=new TextBlock { Text = display, FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(number,value);panel.Children.Add(number);
        panel.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = .6, TextWrapping = TextWrapping.Wrap });
        return panel;
    }
    private void Render()
    {
        if(released||rendering||!compact&&!navigationReady)return;
        if(!DispatcherQueue.HasThreadAccess){DispatcherQueue.TryEnqueue(Render);return;}
        rendering=true;
        try {
        if(compact){RenderCompact();return;}
        status.Text=compact||selectedPage=="quota"?app.Quota.Status:selectedPage is "overview" or "breakdown" or "sessions"?app.HistoryStatus:app.Message;
        ToolTipService.SetToolTip(status,status.Text);
        quotaPanel.Children.Clear();var quota=app.Quota;
        overviewQuota.Children.Clear();overviewQuotaCard.Visibility=quota.HasQuotaDisplay?Visibility.Visible:Visibility.Collapsed;
        updateOverviewLayout?.Invoke();
        if(quota.HasQuotaDisplay)
        {
            overviewQuota.Children.Add(new TextBlock{Text="在线额度摘要",FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            foreach(var window in quota.PrimaryWindows)
            {
                overviewQuota.Children.Add(new TextBlock{Text=$"{window.Label} · 剩余 {window.RemainingText}",TextWrapping=TextWrapping.Wrap});
                overviewQuota.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=5,Foreground=accent});
            }
            overviewQuota.Children.Add(Button("查看额度详情",()=>{Navigate("quota");return Task.CompletedTask;}));
        }
        surfaces.RemoveAll(reference => !reference.TryGetTarget(out _));
        quotaPanel.Children.Add(new TextBlock{Text=app.IsDemo?"模拟账户":quota.AccountLabel,FontSize=20});
        quotaPanel.Children.Add(PlanBadge(quota,false));
        if(!quota.HasQuotaDisplay)quotaPanel.Children.Add(Card(new TextBlock{Text="暂无可显示的在线额度\n\n"+quota.Status,TextWrapping=TextWrapping.Wrap, FontSize=14}));
        var quotaCards=new List<UIElement>();
        foreach(var window in quota.HasQuotaDisplay?quota.PrimaryWindows:[])
        {
            var card=new StackPanel{Spacing=12};card.Children.Add(new TextBlock{Text=window.Label,FontSize=14,Opacity=.75});
            card.Children.Add(new TextBlock{Text=window.RemainingText,FontSize=44,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            card.Children.Add(new TextBlock{Text="剩余额度",FontSize=12,Opacity=.65});
            card.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Foreground=accent,Height=6});
            var reset=window.ResetsAt is {} at?$"预计重置：{at.ToLocalTime():MM-dd HH:mm}\n{window.ResetCountdown(DateTimeOffset.Now)}":"重置时间暂不可用";
            card.Children.Add(new TextBlock{Text=reset,TextWrapping=TextWrapping.Wrap,Opacity=.7});
            quotaCards.Add(Card(card));
        }
        var quotaGroups=new List<UIElement>();
        if(quotaCards.Count>0)quotaGroups.Add(ResponsiveCards(quotaCards,1,300));
        else if(quota.HasQuotaDisplay)quotaGroups.Add(Card(new TextBlock{Text="服务端暂未提供主 Codex 额度",TextWrapping=TextWrapping.Wrap,Opacity=.7}));
        if(quota.HasQuotaDisplay&&quota.Windows.Any(window=>window.IsSpark))
        {
            var spark=new StackPanel{Spacing=12};
            spark.Children.Add(new TextBlock{Text="GPT-5.3-Codex-Spark 专属额度",FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            spark.Children.Add(new TextBlock{Text="独立于通用额度 · 不计入下方通用周容量估算",FontSize=12,Opacity=.65,TextWrapping=TextWrapping.Wrap});
            foreach(var window in quota.Windows.Where(window=>window.IsSpark).OrderBy(window=>window.Minutes))
            {
                var row=new StackPanel{Spacing=6};
                row.Children.Add(new TextBlock{Text=$"{window.Label} · 剩余 {window.RemainingText}",FontSize=15});
                row.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=4,Foreground=accent});
                row.Children.Add(new TextBlock{Text=window.ResetCountdown(DateTimeOffset.Now),FontSize=12,Opacity=.65,TextWrapping=TextWrapping.Wrap});
                spark.Children.Add(row);
            }
            quotaGroups.Add(Card(spark));
        }
        if(quotaGroups.Count>0)quotaPanel.Children.Add(ResponsiveCards(quotaGroups,2,360));
        var estimates=new StackPanel{Spacing=10};
        estimates.Children.Add(new TextBlock{Text="通用周容量估算（实验）",FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        if(quota.Fresh&&quota.HasQuotaDisplay&&app.WeeklyCapacity.Any(e=>e.ObservedPercent>=5&&e.Samples>=2))
        {
            foreach(var estimate in app.WeeklyCapacity.Where(e=>e.ObservedPercent>=5&&e.Samples>=2))
            {
                estimates.Children.Add(ResponsiveCards(new List<UIElement>{
                    Metric("每周 API 等价价值",estimate.DollarDisplay,$"定价覆盖率 {estimate.PricingCoverage:0.#}%"),
                    Metric("每周 Token 容量",$"{estimate.EstimatedTokens:N0}",$"估算 · 可信度 {estimate.Confidence}")},2,300));
                estimates.Children.Add(new TextBlock{Text=$"样本：{estimate.Samples} 段 · 本机 {estimate.ObservedTokens:N0} Token 对应额度变化 {estimate.ObservedPercent:0.##} 个百分点"+(estimate.ExcludedIntervals>0?$" · 排除 {estimate.ExcludedIntervals} 段无法配对变化":""),TextWrapping=TextWrapping.Wrap,Opacity=.7});
            }
        }
        else estimates.Children.Add(new TextBlock{Text=app.WeeklyCapacityProgress,TextWrapping=TextWrapping.Wrap});
        estimates.Children.Add(new TextBlock{Text=app.CapacityCacheStatus,FontSize=12,Opacity=.65,TextWrapping=TextWrapping.Wrap});
        var calculation=new StackPanel{Spacing=10};
        calculation.Children.Add(new TextBlock{Text="Token 容量 = 配对的本机 Token 增量 ÷ 周额度消耗百分点 × 100。\n美元价值 = 同期配对的 API 等价费用增量 ÷ 周额度消耗百分点 × 100；缺价时仅显示部分估算。\n仅估算通用额度，排除已识别的 Spark 用量。结果不是官方固定上限、订阅账单或余额；其他设备用量、模型组合和额度更新延迟都会影响结果。\n样本按账号、套餐和周窗口隔离，保存到本机数据库。重启后核验并恢复有效样本，重新建立起点，不配对停机期间的消耗。套餐字段不变的扩容可手动重置；不会将全部历史 Token 当成本周用量。",TextWrapping=TextWrapping.Wrap,FontSize=12,Opacity=.7});
        calculation.Children.Add(Button("重置估算缓存",async()=>
        {
            var dialog=new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title="重置周容量估算？",Content="仅清除估算样本并重新采样，不删除本地 Token 历史。",PrimaryButtonText="重置",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
            if(await dialog.ShowAsync()==ContentDialogResult.Primary)await app.ResetCapacityAsync();
        }));
        estimates.Children.Add(new Expander{Header="计算说明",IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=calculation});
        quotaPanel.Children.Add(Card(estimates));
        if(quota.HasQuotaDisplay)quotaPanel.Children.Add(new TextBlock{Text="可用重置次数  ·  "+(quota.ResetCount is {} n?n+" 次":"暂不可用"),FontSize=14,Margin=new Thickness(0,4,0,0)});
        if(!compact)quotaPanel.Children.Add(new Expander{Header="数据来源与限制",Content=new TextBlock{Text=quota.Status+"\n本机 Token 历史独立统计，不归属给此账户。",TextWrapping=TextWrapping.Wrap},HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch});
        quotaPanel.Children.Add(new TextBlock{Text=app.IsDemo?"模拟快照 · 仅用于设计预览":quota.IsLocalAccount?"在线额度不可用 · 本地统计无需登录":quota.FetchedAt is {} date?$"采集：{date:HH:mm:ss} · {(quota.Fresh?"实时读取":"已过期")}":"尚无可靠当前账号数据",Opacity=.65});
        if(!compact&&selectedPage is "overview" or "breakdown" or "sessions")RenderStats();
        } finally { rendering=false; }
    }
    private static FrameworkElement PlanBadge(QuotaState quota,bool small)
    {
        var plan=quota.Plan?.Trim().ToLowerInvariant();
        if(quota.IsLocalAccount||plan is not ("prolite" or "pro"))return new TextBlock{Text=quota.PlanDisplay,FontSize=13,Opacity=.7,TextWrapping=TextWrapping.Wrap};
        var gold=plan=="pro";var label=gold?"Pro 20X":"Pro 5X";
        var stack=new StackPanel{Spacing=3,HorizontalAlignment=HorizontalAlignment.Left};
        if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            stack.Children.Add(new TextBlock{Text=label,FontSize=small?14:20,FontWeight=Microsoft.UI.Text.FontWeights.Bold});
        }
        else
        {
            var layers=new Grid{Padding=new Thickness(1,0,3,3)};
            void Layer(Windows.UI.Color color,double y,double x=0)
            {
                layers.Children.Add(new TextBlock{Text=label,FontFamily=new FontFamily("Segoe UI Variable Display"),FontSize=small?14:20,FontWeight=Microsoft.UI.Text.FontWeights.Bold,CharacterSpacing=30,
                    Foreground=new SolidColorBrush(color),RenderTransform=new TranslateTransform{X=x,Y=y}});
            }
            Layer(ColorHelper.FromArgb(200,0,0,0),3,1);
            Layer(gold?ColorHelper.FromArgb(255,116,76,18):ColorHelper.FromArgb(255,89,101,119),2);
            Layer(gold?ColorHelper.FromArgb(255,184,130,39):ColorHelper.FromArgb(255,153,166,185),1);
            Layer(gold?ColorHelper.FromArgb(255,255,222,137):ColorHelper.FromArgb(255,237,243,251),0);
            var background=new LinearGradientBrush{StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(0,1)};
            background.GradientStops.Add(new GradientStop{Offset=0,Color=gold?ColorHelper.FromArgb(255,83,67,39):ColorHelper.FromArgb(255,69,77,89)});
            background.GradientStops.Add(new GradientStop{Offset=.48,Color=ColorHelper.FromArgb(255,36,39,45)});
            background.GradientStops.Add(new GradientStop{Offset=1,Color=ColorHelper.FromArgb(255,25,27,32)});
            stack.Children.Add(new Border{Child=layers,Background=background,CornerRadius=new CornerRadius(7),Padding=new Thickness(small?8:12,2,small?8:12,2),BorderThickness=new Thickness(1),
                BorderBrush=new SolidColorBrush(gold?ColorHelper.FromArgb(255,161,128,68):ColorHelper.FromArgb(255,135,147,164))});
        }
        if(!quota.Fresh)stack.Children.Add(new TextBlock{Text="待刷新确认",FontSize=11,Opacity=.65});
        ToolTipService.SetToolTip(stack,$"套餐后端标识：{quota.Plan}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stack,label+(quota.Fresh?"":"，待刷新确认"));
        return stack;
    }
    private void RenderCompact()
    {
        quotaPanel.Children.Clear();
        surfaces.RemoveAll(reference=>!reference.TryGetTarget(out _));
        var quota=app.Quota;
        Border CompactCard(UIElement content)
        {
            var card=Card(content);card.Padding=new Thickness(14,12,14,12);return card;
        }
        TextBlock Label(string text,double size=13)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};
        if(quota.HasQuotaDisplay&&quota.PrimaryWindows.Any())
        {
            var limits=new StackPanel{Spacing=12};
            foreach(var window in quota.PrimaryWindows)
            {
                var section=new StackPanel{Spacing=6};
                var row=new Grid{ColumnSpacing=10};row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
                var name=Label(window.Label+"剩余");name.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(name);
                var value=Label(window.RemainingText,28);value.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;Grid.SetColumn(value,1);row.Children.Add(value);
                section.Children.Add(row);
                section.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=4,Foreground=accent});
                var reset=Label(window.ResetCountdown(DateTimeOffset.Now)+(quota.Fresh?"":" · 旧快照"),12);reset.Opacity=.65;section.Children.Add(reset);
                limits.Children.Add(section);
            }
            quotaPanel.Children.Add(CompactCard(limits));
        }
        else
        {
            var unavailable=Label(quota.IsLocalAccount?"本地模式 · 在线额度不可用":"在线额度暂不可用 · 可刷新重试");
            ToolTipService.SetToolTip(unavailable,quota.Status);quotaPanel.Children.Add(CompactCard(unavailable));
        }
        var today=DateTime.Today.ToString("yyyy-MM-dd");
        var rows=app.Events.Where(item=>item.LocalDate==today).ToList();
        var usage=new Grid{ColumnSpacing=16};usage.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});usage.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var tokens=new StackPanel{Spacing=4};tokens.Children.Add(Label("今日本机 Token",12));tokens.Children.Add(Label(app.HasLoadedHistory?UsageNumbers.Compact(rows.Sum(item=>item.Tokens.Total)):"加载中",22));
        ToolTipService.SetToolTip(tokens,$"{rows.Sum(item=>item.Tokens.Total):N0} Token");
        var requests=new StackPanel{Spacing=4};requests.Children.Add(Label("今日请求数",12));requests.Children.Add(Label(app.HasLoadedHistory?rows.Count.ToString("N0"):"加载中",22));
        ToolTipService.SetToolTip(requests,"本机日志中可识别的用量请求记录，不包含没有 Token 记录的失败或取消请求。");
        usage.Children.Add(tokens);Grid.SetColumn(requests,1);usage.Children.Add(requests);quotaPanel.Children.Add(CompactCard(usage));
        var estimate=quota.Fresh&&quota.HasQuotaDisplay?app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedPercent>=5&&e.Samples>=2):null;
        var capacity=Label(estimate is not null?$"{estimate.DollarDisplay} · API 等价\n约 {UsageNumbers.Compact(estimate.EstimatedTokens)} Token · 覆盖 {estimate.PricingCoverage:0.#}%":quota.Fresh&&quota.PrimaryWindows.Any(window=>window.Minutes==10080)?"周额度 API 等价 · 采样中":"周额度 API 等价 · 等待在线周额度",12);
        capacity.Opacity=.75;ToolTipService.SetToolTip(capacity,app.WeeklyCapacityProgress);quotaPanel.Children.Add(capacity);
        var updated=quota.FetchedAt is {} at?$"更新 {at.ToLocalTime():HH:mm}":"尚未更新额度";
        var resets=quota.HasQuotaDisplay&&quota.ResetCount is {} count?$"可用重置 {count} 次":"重置次数暂不可用";
        quotaPanel.Children.Add(PlanBadge(quota,true));
        var footer=Label($"{resets}   ·   {updated}",12);footer.Opacity=.6;quotaPanel.Children.Add(footer);
    }
    private void RenderStats()
    {
        var selectedRange=SelectedHistoryRange();
        var filter=$"{selectedPage}|{historyRangeIndex}|{selectedRange.From}|{selectedRange.Through}|{historySearch.Text}|{sessionOrder.SelectedIndex}|{sessionPage}|{breakdownKind.SelectedIndex}|{DateTime.Today:yyyy-MM-dd}";
        if(ReferenceEquals(renderedEvents,app.Events)&&renderedFilter==filter)return;
        renderedEvents=app.Events;renderedFilter=filter;
        Detach(filterBar);
        updateOverviewLayout=null;
        overviewQuota=new StackPanel{Spacing=10};overviewQuotaCard=Card(overviewQuota);
        overviewQuotaCard.Visibility=app.Quota.HasQuotaDisplay?Visibility.Visible:Visibility.Collapsed;
        if(app.Quota.HasQuotaDisplay)
        {
            overviewQuota.Children.Add(new TextBlock{Text="在线额度摘要",FontSize=18});
            foreach(var window in app.Quota.PrimaryWindows)
            {
                overviewQuota.Children.Add(new TextBlock{Text=$"{window.Label} · 剩余 {window.RemainingText}",TextWrapping=TextWrapping.Wrap});
                overviewQuota.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=5,Foreground=accent});
            }
            overviewQuota.Children.Add(Button("查看额度详情",()=>{Navigate("quota");return Task.CompletedTask;}));
        }
        UIElement? pricingDetails=null;
        statsPanel.Children.Clear();
        sessionWorkspace.Children.Clear();
        historySearch.Visibility=selectedPage=="sessions"?Visibility.Visible:Visibility.Collapsed;
        var rows=historyRangeIndex==4&&!selectedRange.IsBounded?[]:HistoryQuery.Filter(app.Events,new(selectedRange.From,selectedRange.Through,selectedPage=="sessions"?historySearch.Text:""),app.SessionNames);
        var total=rows.Aggregate(new TokenUsage(),(a,e)=>a+e.Tokens);var estimate=Pricing.Summarize(rows);var sessionCount=rows.Select(item=>item.Session).Distinct(StringComparer.Ordinal).Count();
        if(selectedPage=="sessions")
        {
            BuildSessionWorkspace(rows,total,estimate);
            return;
        }
        var rangeHeader=new Grid{ColumnSpacing=12};rangeHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});rangeHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        rangeHeader.Children.Add(filterBar);var rangeLabel=new TextBlock{Text=selectedRange.Label,Opacity=.62,FontSize=12,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,8,0,0)};Grid.SetColumn(rangeLabel,1);rangeHeader.Children.Add(rangeLabel);
        rangeHeader.SizeChanged+=(_,e)=>rangeLabel.Visibility=e.NewSize.Width<820?Visibility.Collapsed:Visibility.Visible;
        statsPanel.Children.Add(Card(rangeHeader));
        if(selectedPage=="overview")
        {
        UIElement[] cards;
        if(app.Quota.AccountKey is {} accountKey)
        {
            var accountTotal=rows.Where(item=>string.Equals(item.AccountScope,accountKey,StringComparison.Ordinal)).Sum(item=>item.Tokens.Total);
            var remainder=Math.Max(0,total.Total-accountTotal);
            var accountCards=new[]{Metric("本机全部历史",total.Total.ToString("N0"),"所选范围内全部本地日志"),Metric("当前账号观察归属",accountTotal.ToString("N0"),"仅计入登录指纹连续一致期间的新记录"),Metric("未归属或其他账号",remainder.ToString("N0"),"旧记录、未登录记录与账号切换边界")};
            statsPanel.Children.Add(Card(ResponsiveCards(accountCards,3,210)));
            cards=[Metric("Session",sessionCount.ToString("N0"),"所选范围去重会话"),Metric("完成请求",rows.Count.ToString("N0"),"本地日志中带 Token 的请求"),Metric(estimate.Status,rows.Count==0?"暂无数据":estimate.DisplayAmount,"API 等价估算，不是订阅账单"),Metric("定价覆盖率",total.Total>0?$"{100d*estimate.Priced/total.Total:0.#}%":"—",$"{estimate.Unpriced:N0} Token 暂不可定价")];
        }
        else cards=[Metric("本机总 Token",total.Total.ToString("N0"),"所选本地历史"),Metric("Session",sessionCount.ToString("N0"),$"{rows.Count:N0} 次带 Token 的完成请求"),Metric(estimate.Status,rows.Count==0?"暂无数据":estimate.DisplayAmount,"API 等价估算，不是订阅账单"),Metric("定价覆盖率",total.Total>0?$"{100d*estimate.Priced/total.Total:0.#}%":"—",$"{estimate.Unpriced:N0} Token 暂不可定价")];
        var metrics=ResponsiveCards(cards,4,190);statsPanel.Children.Add(Card(metrics));
        if(rows.Count>0)
        {
            var details=new StackPanel{Spacing=12};
            details.Children.Add(new TextBlock{Text=$"目录 {Pricing.CatalogVersion} · 按当前 OpenAI API 公开价格重估本机历史\n公式：普通输入、缓存读取、缓存写入、输出分别乘每百万 Token 单价；推理不重复收费；可验证的单请求长上下文倍率自动计入。\n覆盖率只表示哪些 Token 能套用价格，不代表特殊条件、工具费和实际账单完全覆盖；其他工具若采用旧价格目录，金额不会相同。",TextWrapping=TextWrapping.Wrap,FontSize=13});
            if(!string.IsNullOrWhiteSpace(estimate.Reason))details.Children.Add(new TextBlock{Text=estimate.Reason,TextWrapping=TextWrapping.Wrap});
            foreach(var note in estimate.Notes)details.Children.Add(new TextBlock{Text="• "+note,TextWrapping=TextWrapping.Wrap,FontSize=12});
            foreach(var group in rows.GroupBy(e=>e.Model))
            {
                var basis=Pricing.Find(group.Key);var item=Pricing.Summarize(group);
                details.Children.Add(new TextBlock{Text=$"{group.Key} · {item.Status} · {item.DisplayAmount}",FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
                if(basis is null){details.Children.Add(new TextBlock{Text="未收录可核对价格，不按零费用处理。",TextWrapping=TextWrapping.Wrap});continue;}
                var price=basis.Rates;
                details.Children.Add(new TextBlock{Text=$"USD / 1M Token：输入 {price.Input}，缓存读取 {price.Cached?.ToString()??"未知"}，缓存写入 {price.Write?.ToString()??"未知"}，输出 {price.Output}\n核对日期 {basis.CheckedOn:yyyy-MM-dd}；官方生效起止日：{basis.EffectiveFrom?.ToString("yyyy-MM-dd")??"未明确"} / {basis.EffectiveUntil?.ToString("yyyy-MM-dd")??"未明确"}"+(basis.PromotionAtLeastThrough is {} promo?$"\n促销至少持续至 {promo:yyyy-MM-dd}，之后需复核":""),TextWrapping=TextWrapping.Wrap,FontSize=12});
                details.Children.Add(new HyperlinkButton{Content="查看该模型官方价格依据",NavigateUri=basis.Source});
            }
            pricingDetails=new Expander{Header="计算依据、价格来源与缺价说明",HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=details};
        }
        }
        else statsPanel.Children.Add(new TextBlock{Text=$"筛选结果：{rows.Count:N0} 条记录 · {total.Total:N0} Token · {estimate.DisplayAmount}（{estimate.Status}）",TextWrapping=TextWrapping.Wrap,Opacity=.7});
        if(rows.Count==0)statsPanel.Children.Add(Card(new TextBlock{Text=app.Events.Count==0?"暂无本地统计\n\n点击“扫描本地日志”建立用量视图。":historyRangeIndex==4&&!selectedRange.IsBounded?"请选择完整且有效的自定义开始、结束日期。":"当前筛选条件下没有记录，请调整时间范围或搜索条件。",FontSize=18,TextWrapping=TextWrapping.Wrap}));
        if(selectedPage=="overview")
        {
            var hourly=historyRangeIndex==0;
            var trend = UsageCharts.Trend(hourly?HistoryQuery.HourlyTrend(rows,DateOnly.FromDateTime(DateTime.Today)):HistoryQuery.Trend(rows,selectedRange),DrillIntoRange,hourly);
            var modelPanel=new StackPanel{Spacing=12};modelPanel.Children.Add(new TextBlock{Text="模型分布",FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            modelPanel.Children.Add(UsageCharts.Models(rows));
            statsPanel.Children.Add(Card(trend));
            var recent=new StackPanel{Spacing=12};recent.Children.Add(new TextBlock{Text="最近 Session",FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            foreach(var session in HistoryQuery.Sessions(rows,"recent",app.SessionNames).Take(4))recent.Children.Add(new TextBlock{Text=$"{session.Name} · {session.Tokens:N0} Token\n项目：{session.Project} · ID {session.ShortId}",TextWrapping=TextWrapping.Wrap,FontSize=13});
            recent.Children.Add(Button("查看全部 Session",()=>{Navigate("sessions");return Task.CompletedTask;}));
            // The quota summary updates independently without collapsing expanded history controls.
            var recentCard=Card(recent);
            var bottomItems=new List<UIElement>{Card(modelPanel),recentCard,overviewQuotaCard};var bottom=ResponsiveCards(bottomItems,3,300);
            var localQuotaCard=overviewQuotaCard;
            void FitBottom(){Grid.SetColumnSpan(recentCard,localQuotaCard.Visibility==Visibility.Collapsed&&bottom.ColumnDefinitions.Count>=3?2:1);}
            bottom.SizeChanged+=(_,_)=>FitBottom();updateOverviewLayout=FitBottom;FitBottom();
            statsPanel.Children.Add(bottom);
            var composition=new StackPanel{Spacing=10};composition.Children.Add(new TextBlock{Text="Token 构成",FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            composition.Children.Add(new TextBlock{Text=$"输入 {total.Input:N0}    输出 {total.Output:N0}",FontSize=16});
            composition.Children.Add(new TextBlock{Text=$"其中：缓存读取 {total.Cached:N0} · 缓存写入 {total.CacheWrite:N0} · 推理 {total.Reasoning:N0}",TextWrapping=TextWrapping.Wrap,Opacity=.65});
            composition.Children.Add(new TextBlock{Text="缓存属于输入，推理属于输出；子分类不重复计入总量。",FontSize=12,Opacity=.6});statsPanel.Children.Add(new Expander{Header="Token 分类详情",Content=composition,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch});
        }
        else
        {
            statsPanel.Children.Add(breakdownKind);
            AddGroups((string)breakdownKind.SelectedItem,rows.GroupBy(e=>breakdownKind.SelectedIndex switch{1=>e.Project,2=>e.Agent,_=>e.Model}).OrderByDescending(g=>g.Sum(e=>e.Tokens.Total)));
        }
        if(pricingDetails is not null)statsPanel.Children.Add(pricingDetails);
    }
    private void BuildSessionWorkspace(List<UsageEvent> rows,TokenUsage total,Estimate estimate)
    {
        sessionWorkspace.RowDefinitions.Clear();
        sessionWorkspace.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        sessionWorkspace.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        sessionWorkspace.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        sessionWorkspace.Children.Add(filterBar);

        var sessions=HistoryQuery.Sessions(rows,sessionOrder.SelectedIndex switch{1=>"recent",2=>"name",_=>"tokens"},app.SessionNames);
        const int pageSize=30;sessionPage=Math.Clamp(sessionPage,0,Math.Max(0,(sessions.Count-1)/pageSize));
        var sort=new ComboBox{ItemsSource=sessionOrder.ItemsSource,SelectedIndex=sessionOrder.SelectedIndex,Width=160};
        sort.SelectionChanged+=(_,_)=>sessionOrder.SelectedIndex=sort.SelectedIndex;
        var paging=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Right};paging.Children.Add(sort);
        paging.Children.Add(new TextBlock{Text=$"{sessionPage+1}/{Math.Max(1,(sessions.Count+pageSize-1)/pageSize)}",VerticalAlignment=VerticalAlignment.Center});
        paging.Children.Add(Button("‹",()=>{sessionPage=Math.Max(0,sessionPage-1);Render();return Task.CompletedTask;}));
        paging.Children.Add(Button("›",()=>{if((sessionPage+1)*pageSize<sessions.Count)sessionPage++;Render();return Task.CompletedTask;}));
        var summary=new TextBlock{Text=$"筛选结果：{rows.Count:N0} 条记录 · {total.Total:N0} Token · {estimate.DisplayAmount}（{estimate.Status}）",TextWrapping=TextWrapping.Wrap,Opacity=.7,VerticalAlignment=VerticalAlignment.Center};
        var toolbar=new Grid{ColumnSpacing=12,RowSpacing=8};
        toolbar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});toolbar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        toolbar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});toolbar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        toolbar.Children.Add(summary);toolbar.Children.Add(paging);Grid.SetColumn(paging,1);
        toolbar.SizeChanged+=(_,e)=>
        {
            var narrow=e.NewSize.Width<620;
            Grid.SetColumn(paging,narrow?0:1);Grid.SetRow(paging,narrow?1:0);Grid.SetColumnSpan(paging,narrow?2:1);
            paging.HorizontalAlignment=narrow?HorizontalAlignment.Left:HorizontalAlignment.Right;
        };
        Grid.SetRow(toolbar,1);sessionWorkspace.Children.Add(toolbar);

        var list=new ListView{SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
        var detail=new StackPanel{Spacing=12,VerticalAlignment=VerticalAlignment.Top};
        var detailScroll=new ScrollViewer{Content=detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalAlignment=VerticalAlignment.Stretch};
        void ShowSession(SessionSummary session)
        {
            detail.Children.Clear();var tokens=session.Events.Aggregate(new TokenUsage(),(sum,item)=>sum+item.Tokens);
            detail.Children.Add(new TextBlock{Text=session.Name,FontSize=18,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true});
            detail.Children.Add(new TextBlock{Text="内部会话 ID："+session.Session,FontSize=11,Opacity=.55,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true});
            detail.Children.Add(new TextBlock{Text=$"{session.Project} · {session.LastActivity?.ToLocalTime():MM-dd HH:mm}\n{session.Tokens:N0} Token · {Pricing.Summarize(session.Events).DisplayAmount} API 等价估算",TextWrapping=TextWrapping.Wrap});
            detail.Children.Add(new TextBlock{Text=$"输入（含缓存） {tokens.Input:N0}\n输出（含推理） {tokens.Output:N0}\n缓存读取 {tokens.Cached:N0}\n缓存写入 {tokens.CacheWrite:N0}\n推理 {tokens.Reasoning:N0}",TextWrapping=TextWrapping.Wrap,LineHeight=26});
            detail.Children.Add(new TextBlock{Text="Token 记录 · 子分类不重复累加",Opacity=.65});
            var shown=0;
            void More()
            {
                foreach(var item in session.Events.Skip(shown).Take(50))
                {
                    var line=new TextBlock{Text=$"{item.LocalDate} {item.Timestamp?.ToLocalTime():HH:mm:ss} · {item.Model}\n{item.Tokens.Total:N0} Token"+(item.QualityNote is {} note?" · "+note:""),TextWrapping=TextWrapping.Wrap,FontSize=12,Padding=new Thickness(0,8,0,8)};
                    detail.Children.Add(new Border{Child=line,BorderThickness=new Thickness(0,1,0,0),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(28,150,155,170))});
                }
                shown=Math.Min(session.Events.Count,shown+50);
                if(shown<session.Events.Count){Button? more=null;more=Button("加载更多记录",()=>{detail.Children.Remove(more!);More();return Task.CompletedTask;});detail.Children.Add(more);}
            }
            More();
            DispatcherQueue.TryEnqueue(()=>detailScroll.ChangeView(null,0,null));
        }
        foreach(var session in sessions.Skip(sessionPage*pageSize).Take(pageSize))list.Items.Add(new ListViewItem{Tag=session,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=new TextBlock{Text=$"{session.Name} · {session.Tokens:N0} Token\n项目：{session.Project} · ID {session.ShortId}",TextWrapping=TextWrapping.Wrap},Padding=new Thickness(12)});
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is ListViewItem{Tag:SessionSummary session})ShowSession(session);};
        if(list.Items.Count>0)list.SelectedIndex=0;else detail.Children.Add(new TextBlock{Text=app.Events.Count==0?"暂无本地统计\n\n点击“扫描本地日志”建立用量视图。":"当前筛选条件下没有 Session，请清除日期或搜索条件。",FontSize=18,TextWrapping=TextWrapping.Wrap});

        var listCard=Card(list);var detailCard=Card(detailScroll);
        listCard.VerticalAlignment=VerticalAlignment.Stretch;detailCard.VerticalAlignment=VerticalAlignment.Stretch;
        var split=new Grid{ColumnSpacing=16,RowSpacing=16,VerticalAlignment=VerticalAlignment.Stretch};
        split.Children.Add(listCard);split.Children.Add(detailCard);
        var stacked=false;
        void Layout(double width)
        {
            var next=width<720;if(next==stacked&&split.ColumnDefinitions.Count>0)return;stacked=next;
            split.ColumnDefinitions.Clear();split.RowDefinitions.Clear();
            if(stacked)
            {
                split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
                split.RowDefinitions.Add(new RowDefinition{Height=new GridLength(2,GridUnitType.Star)});split.RowDefinitions.Add(new RowDefinition{Height=new GridLength(3,GridUnitType.Star)});
                Grid.SetColumn(listCard,0);Grid.SetRow(listCard,0);Grid.SetColumn(detailCard,0);Grid.SetRow(detailCard,1);
            }
            else
            {
                split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(.9,GridUnitType.Star)});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.4,GridUnitType.Star)});
                split.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
                Grid.SetColumn(listCard,0);Grid.SetRow(listCard,0);Grid.SetColumn(detailCard,1);Grid.SetRow(detailCard,0);
            }
        }
        split.SizeChanged+=(_,e)=>Layout(e.NewSize.Width);Layout(1000);
        Grid.SetRow(split,2);sessionWorkspace.Children.Add(split);
    }
    private void AddGroups(string title,IEnumerable<IGrouping<string,UsageEvent>> groups)
    {
        var panel=new StackPanel{Spacing=0};var table=groups.ToList();var total=table.Sum(g=>g.Sum(e=>e.Tokens.Total));
        Grid Row(params string[] values)
        {
            var row=new Grid{MinWidth=740,Padding=new Thickness(12,14,12,14),ColumnSpacing=16};
            for(var i=0;i<values.Length;i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(i==0?2:1,GridUnitType.Star)});
                var text=new TextBlock{Text=values[i],TextWrapping=TextWrapping.Wrap,FontSize=13,IsTextSelectionEnabled=true};Grid.SetColumn(text,i);row.Children.Add(text);
            }
            return row;
        }
        panel.Children.Add(Row(title,"输入","缓存读取","输出","总 Token","API 等价估算","占比"));
        foreach(var group in table)
        {
            var tokens=group.Aggregate(new TokenUsage(),(sum,item)=>sum+item.Tokens);var estimate=Pricing.Summarize(group);
            var row=Row(group.Key,tokens.Input.ToString("N0"),tokens.Cached.ToString("N0"),tokens.Output.ToString("N0"),tokens.Total.ToString("N0"),estimate.DisplayAmount,total>0?$"{100d*tokens.Total/total:0.#}%":"—");
            ToolTipService.SetToolTip(row,estimate.Status+" · "+estimate.Reason);
            panel.Children.Add(new Border{Child=row,BorderThickness=new Thickness(0,1,0,0),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(35,140,145,160))});
        }
        statsPanel.Children.Add(Card(new ScrollViewer{Content=panel,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollMode=ScrollMode.Enabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollMode=ScrollMode.Disabled}));
        statsPanel.Children.Add(new TextBlock{Text="缓存读取包含在输入中；表格可横向滚动，API 等价估算不代表账单。",Opacity=.65,FontSize=12,TextWrapping=TextWrapping.Wrap});
    }
    private UIElement SettingsPanel()
    {
        var panel=new StackPanel{Spacing=14,Padding=new Thickness(8)};
        var cli=new TextBox{Header="Codex 原生 exe 路径（留空或失效时自动查找桌面 App / CLI）",Text=app.Config.CliPath??""};
        var home=new TextBox{Header="Codex Home（仅扫描 sessions 与 archived_sessions）",Text=app.IsDemo?"设计预览 · 不读取本机目录":app.Config.CodexHome,IsReadOnly=app.IsDemo};
        var auto=new ToggleSwitch{Header="自动额度刷新",IsOn=app.Config.AutoRefresh};
        var seconds=new NumberBox{Header="后台兜底周期（秒）",Minimum=30,Maximum=3600,Value=app.Config.BackgroundSeconds};
        var foreground=new NumberBox{Header="查看面板时的轮询周期（秒）",Minimum=15,Maximum=3600,Value=app.Config.ForegroundSeconds};
        var low=new ToggleSwitch{Header="额度不足通知",IsOn=app.Config.LowNotify};
        var threshold=new NumberBox{Header="剩余比例阈值（%）",Minimum=1,Maximum=99,Value=app.Config.LowPercent};
        var reset=new ToggleSwitch{Header="即将重置通知",IsOn=app.Config.ResetNotify};
        var minutes=new NumberBox{Header="提前提醒（分钟）",Minimum=1,Maximum=120,Value=app.Config.ResetMinutes};
        var theme=new ComboBox{Header="外观",ItemsSource=new[]{"跟随系统","浅色","深色"},SelectedIndex=app.Config.Theme switch{"Light"=>1,"Dark"=>2,_=>0}};
        void Section(string title, params UIElement[] controls)
        {
            var group=new StackPanel{Spacing=12};
            foreach(var control in controls)
            {
                object? label=control switch{TextBox box=>box.Header,ComboBox box=>box.Header,ToggleSwitch box=>box.Header,NumberBox box=>box.Header,_=>null};
                if(label is null){group.Children.Add(control);continue;}
                switch(control){case TextBox box:box.Header=null;break;case ComboBox box:box.Header=null;box.MinWidth=160;box.HorizontalAlignment=HorizontalAlignment.Right;break;case ToggleSwitch box:box.Header=null;box.HorizontalAlignment=HorizontalAlignment.Right;break;case NumberBox box:box.Header=null;box.Width=150;box.HorizontalAlignment=HorizontalAlignment.Right;break;}
                var caption=new TextBlock{Text=label.ToString(),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap};
                group.Children.Add(ResponsiveCards(new UIElement[]{caption,control},2,250));
            }
            if(title is "个性化" or "数据来源" or "更新节奏")
            {var block=new StackPanel{Spacing=18};block.Children.Add(new TextBlock{Text=title,FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});block.Children.Add(group);panel.Children.Add(Card(block));}
            else panel.Children.Add(new Expander{Header=title,Content=group,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch});
        }
        Section("个性化",theme);Section("数据来源",cli,home);Section("更新节奏",auto,foreground,seconds);Section("通知（默认关闭）",low,threshold,reset,minutes);
        Section("优先尝试已有登录缓存",
            new TextBlock{Text="使用 App 附带的 codex.exe 和上面的 Codex Home，由官方后端加载其可见的已有缓存。不会打开 OAuth、复制或读取令牌文件，也不连接 proxy。能否识别 App 登录需要实测。",TextWrapping=TextWrapping.Wrap},
            Button("使用本机已有登录缓存（不重新授权）",async()=>
            {
                await app.UseCachedLoginAsync(cli.Text.Trim(),home.Text.Trim());
            }));
        async Task Authorize(bool logout)
        {
            if(app.Authorizing)throw new InvalidOperationException("已有授权进行中，请先完成或取消");
            var dialog=new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=logout?"退出 UsageLoom 授权账号？":"通过官方后端单独登录",CloseButtonText="取消",PrimaryButtonText=logout?"退出授权账号":"打开官方授权",DefaultButton=ContentDialogButton.Close,
                Content=logout?"只退出 UsageLoom 专用后端，不退出 ChatGPT App，也不删除本地统计。":"浏览器由你完成授权。官方后端会在 UsageLoom 专用 authorized-backend 目录保存并刷新登录缓存；UsageLoom 不读取令牌文件。不要上传该目录。登录不自动跟随桌面 App 切换账号；本实验模式暂不发送额度通知。"};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            if(!app.IsDemo)app.Config.CliPath=cli.Text.Trim();
            await app.AuthorizeAsync(logout);
        }
        Section("高级实验：独立登录（默认不用）",
            new TextBlock{Text="正常情况下请使用上方的本机已有登录缓存。只有缓存复用不可用、且你明确希望 UsageLoom 使用独立账号时，才需要这里的浏览器授权。",TextWrapping=TextWrapping.Wrap},
            Button("通过官方后端登录",async()=>await Authorize(false)),
            Button("取消正在进行的授权",()=>{app.CancelAuthorization();return Task.CompletedTask;}),
            Button("退出 UsageLoom 授权账号",async()=>await Authorize(true)));
        actions.Children.Add(Button("保存设置",async()=>
        {
            if(app.IsDemo){status.Text="设计预览不保存设置；正式模式中可配置外观与刷新。";return;}
            if(app.Authorizing)throw new InvalidOperationException("请先完成或取消浏览器授权");
            if(!double.IsFinite(seconds.Value)||!double.IsFinite(foreground.Value)||!double.IsFinite(threshold.Value)||!double.IsFinite(minutes.Value))throw new ArgumentException("请输入有效数字");
            if(string.IsNullOrWhiteSpace(home.Text)||!Path.IsPathFullyQualified(home.Text.Trim()))throw new ArgumentException("Codex Home 必须是完整目录路径");
            app.Config.CliPath=cli.Text.Trim();app.Config.CodexHome=home.Text.Trim();app.Config.AutoRefresh=auto.IsOn;
            app.Config.BackgroundSeconds=(int)Math.Clamp(seconds.Value,30,3600);app.Config.LowNotify=low.IsOn;app.Config.LowPercent=(int)Math.Clamp(threshold.Value,1,99);
            app.Config.ForegroundSeconds=(int)Math.Clamp(foreground.Value,15,3600);
            app.Config.ResetNotify=reset.IsOn;app.Config.ResetMinutes=(int)Math.Clamp(minutes.Value,1,120);app.Config.Theme=theme.SelectedIndex switch{1=>"Light",2=>"Dark",_=>"Default"};app.Config.AppearanceConfigured=true;
            await app.SaveSettingsAsync();ApplyTheme();
        }));
        panel.Children.Add(new TextBlock{Text="UsageLoom 不读取凭据文件或保存账号列表。官方未提供稳定 ID 时，邮箱只在内存中用于生成本机加密指纹，不保存、显示或记录明文。独立授权缓存由官方后端保存在专用目录，卸载时保留；可先退出授权账号。关闭自动刷新后仍可手动查询。",TextWrapping=TextWrapping.Wrap,Opacity=.7});
        return panel;
    }
    private UIElement AboutPanel()
    {
        var panel=new StackPanel{Spacing=16};
        panel.Children.Add(new TextBlock{Text="UsageLoom  "+Program.Version+"\n本地优先 · 正式版",FontSize=24,TextWrapping=TextWrapping.Wrap});
        var health=new StackPanel{Spacing=12};health.Children.Add(new TextBlock{Text="运行状态",FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        health.Children.Add(new TextBlock{Text=app.HistoryStatus,TextWrapping=TextWrapping.Wrap});health.Children.Add(new TextBlock{Text="额度来源 · "+app.Quota.AccountLabel,TextWrapping=TextWrapping.Wrap});panel.Children.Add(Card(health));
        panel.Children.Add(Card(new TextBlock{Text=app.DiagnosticSummary,FontFamily=new FontFamily("Cascadia Mono"),FontSize=12,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true}));
        panel.Children.Add(new TextBlock{Text="数据维护",FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        panel.Children.Add(Button("完整校验本地日志",async()=>await app.ScanAsync(verifyIntegrity:true)));
        panel.Children.Add(Button("备份并重建本地统计",async()=>
        {
            var dialog=new ContentDialog
            {
                XamlRoot=((FrameworkElement)Content).XamlRoot,
                Title="重建本地统计？",
                Content="将先备份当前统计数据库，再从设置中所选目录的 sessions 和 archived_sessions 重建。其他来源及已删除日志对应的历史将不再出现在当前统计中，但旧统计仍保留在备份。不会修改原始日志；备份含本地路径等隐私信息，请勿上传。日志有警告、半行或读取失败时不会替换旧统计。",
                PrimaryButtonText="备份并重建",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close
            };
            if(await dialog.ShowAsync()==ContentDialogResult.Primary)await app.ScanAsync(rebuild:true);
        }));
        panel.Children.Add(new Expander{Header="隐私、价格与索引说明",HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=new TextBlock{Text="本地日志只读；不读取或复制凭据文件。独立授权缓存由官方后端管理，不要上传登录缓存。\n本机历史不关联当前账号；费用仅为 API 等价估算。价格核对："+Pricing.VerifiedDate+"\n索引变化后按受影响 Session 重放，尚非完整增量聚合。",TextWrapping=TextWrapping.Wrap}});
        actions.Children.Add(Button("复制脱敏诊断",()=>{var data=new Windows.ApplicationModel.DataTransfer.DataPackage();data.SetText(app.DiagnosticSummary);Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);return Task.CompletedTask;}));
        return panel;
    }
    private Button Button(string label,Func<Task> action)
    {
        var button=new Button{Content=label,CornerRadius=new CornerRadius(9),Padding=new Thickness(16,9,16,9)};button.Click+=async(_,_)=>{button.IsEnabled=false;try{await action();}catch(Exception ex){Program.Log.Write("ERROR","UI",ex.Message);status.Text=Privacy.Redact(ex.Message);}finally{button.IsEnabled=true;}};return button;
    }
}

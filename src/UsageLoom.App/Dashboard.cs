using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard : Window
{
    private readonly LoomApp app;
    private TextBlock? capacityStatusText;
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
    private readonly TextBox historySearch=new(){PlaceholderText=L10n.T("s4566FC389381"),MinWidth=200};
    private readonly CalendarDatePicker historyFrom=new(){Header=L10n.T("sD2BB025A2E51"),PlaceholderText=L10n.T("s760506491EEF"),DateFormat="{year.full}-{month.integer(2)}-{day.integer(2)}",MinWidth=140};
    private readonly CalendarDatePicker historyThrough=new(){Header=L10n.T("sC7B24E7997E9"),PlaceholderText=L10n.T("s895CD52FBBB6"),DateFormat="{year.full}-{month.integer(2)}-{day.integer(2)}",MinWidth=140};
    private readonly ComboBox sessionOrder=new(){ItemsSource=new[]{L10n.T("sA821A0A35B3A"),L10n.T("s41F5CADA11ED"),L10n.T("s1034068B43DF")},SelectedIndex=0,Width=180};
    private readonly StackPanel dateControls=new(){Orientation=Orientation.Horizontal,Spacing=10};
    private readonly Grid filterBar;
    private Panel? filterHost;
    private readonly ComboBox breakdownKind=new(){ItemsSource=new[]{L10n.T("sC98E118E0A43"),L10n.T("s79F326BE4409"),L10n.T("sED1EDA4DF65E")},SelectedIndex=0,Width=160};
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
    private string? requestedPage;
    internal void ShowPage(string tag)
    {
        requestedPage=tag;
        if(navigationReady)Navigate(tag);
        ShowPanel();
    }
    public bool IsPanelVisible {get;private set;}
    private bool released;
    private bool rendering;
    private bool navigationReady;
    private bool pinned;
    private int minimumWindowWidthPixels;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? navigationTest;
    private ComboBox? appearanceChoice,languageChoice;
    public Dashboard(LoomApp app,bool compact)
    {
        Program.Log.Write("INFO", "UI", "构建界面开始 compact="+compact);
        this.app=app;this.compact=compact;Title=compact?L10n.T("s6777E778B75E"):L10n.T("sACBDF819A42B");
        if(app.PreviewHourly)historyRangeIndex=0;
        overviewQuotaCard=Card(overviewQuota);
        historyFrom.Date=DateTimeOffset.Now.Date.AddDays(-6);historyThrough.Date=DateTimeOffset.Now.Date;
        dateControls.Children.Add(historyFrom);dateControls.Children.Add(historyThrough);
        foreach(var (label,index) in new[]{(L10n.T("s85217F7AFF77"),0),(L10n.T("s2261B06712A3"),1),(L10n.T("s5C553EC3F6DB"),2),(L10n.T("s1625179BADC0"),3),(L10n.T("s4EAFA9E925B3"),4)})
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
        var root = new Grid{Language=app.Config.Language};
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titlebar = new Grid { Height = 48, Padding = new Thickness(compact?12:20, 0, 138, 0) };
        titlebar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        titlebar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var titleIdentity=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,VerticalAlignment=VerticalAlignment.Center};
        titleIdentity.Children.Add(new TextBlock { Text = "L   UsageLoom", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var versionBadge=new Border{Padding=new Thickness(7,2,7,2),CornerRadius=new CornerRadius(7),Background=new SolidColorBrush(ColorHelper.FromArgb(28,127,132,150)),Child=new TextBlock{Text=Program.Version,FontSize=10.5,Opacity=.75,TextWrapping=TextWrapping.NoWrap,VerticalAlignment=VerticalAlignment.Center}};
        ToolTipService.SetToolTip(versionBadge,"UsageLoom "+Program.Version+L10n.T("sE27F6E708134"));titleIdentity.Children.Add(versionBadge);titlebar.Children.Add(titleIdentity);
        root.Children.Add(titlebar);
        ExtendsContentIntoTitleBar = true; SetTitleBar(titleIdentity);
        if(compact)
        {
            titleIdentity.Spacing=6;
            ((TextBlock)titleIdentity.Children[0]).FontSize=16;
            var pin=new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton{Content=new FontIcon{Glyph="\uE718",FontSize=12},Width=46,Height=32,Padding=new Thickness(0),VerticalAlignment=VerticalAlignment.Top,CornerRadius=new CornerRadius(0),BorderThickness=new Thickness(0)};
            var transparent=new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var hover=new SolidColorBrush(ColorHelper.FromArgb(24,127,132,150));
            var pressed=new SolidColorBrush(ColorHelper.FromArgb(40,127,132,150));
            // Match caption controls: no resting tile, including when pinned.
            foreach(var state in new[]{"","Checked"})
            {
                pin.Resources["ToggleButtonBackground"+state]=transparent;
                pin.Resources["ToggleButtonBackground"+state+"PointerOver"]=hover;
                pin.Resources["ToggleButtonBackground"+state+"Pressed"]=pressed;
                foreach(var interaction in new[]{"","PointerOver","Pressed"})
                    pin.Resources["ToggleButtonBorderBrush"+state+interaction]=transparent;
            }
            foreach(var interaction in new[]{"","PointerOver","Pressed"})
                pin.Resources["ToggleButtonForegroundChecked"+interaction]=accent;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pin,L10n.T("s173F88D28EBF"));
            ToolTipService.SetToolTip(pin,L10n.T("s1847911C6C16"));
            pin.Click+=(_,_)=>{pinned=pin.IsChecked==true;if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)presenter.IsAlwaysOnTop=pinned;};
            Grid.SetColumn(pin,1);titlebar.Children.Add(pin);
        }
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
        actions.Children.Add(Button(compact?L10n.T("sAEE887434131"):L10n.T("s637A0D380AEC"),async()=>await app.RefreshQuotaAsync(true)));
        if(compact)
        {
            actions.Spacing=6;
            actions.Children.Add(Button(L10n.T("s979A332955C8"),()=>{app.ShowDetails();if(!pinned)Hide();return Task.CompletedTask;}));
        }
        else actions.Children.Add(Button(L10n.T("s020FCEFB6A48"),async()=>await app.ScanAsync()));
        body.Children.Add(heading);
        body.RowDefinitions[1].Height=new GridLength(1,GridUnitType.Star);
        body.RowDefinitions[2].Height=GridLength.Auto;
        status.Opacity = .65; status.MaxLines=1;status.FontSize=12;status.TextTrimming=TextTrimming.CharacterEllipsis;
        status.Margin = new Thickness(compact?0:32, 12, compact?0:32, 0); status.MaxWidth=1600;status.HorizontalAlignment=compact?HorizontalAlignment.Stretch:HorizontalAlignment.Left;Grid.SetRow(status, 2); body.Children.Add(status);
        Grid.SetRow(page, 1); body.Children.Add(page);
        if(compact)
        {
            pageTitle.Text = L10n.T("sEA5C9B72650B");pageTitle.FontSize=16;pageSubtitle.Visibility=Visibility.Collapsed;status.Visibility=Visibility.Collapsed;
            quotaPanel.Spacing=10;
            quotaPanel.SizeChanged+=(_,_)=>DispatcherQueue.TryEnqueue(FitCompactHeight);
            page.Content = new Viewbox { Child = quotaPanel, Stretch=Stretch.Uniform, StretchDirection=StretchDirection.DownOnly,VerticalAlignment=VerticalAlignment.Top,HorizontalAlignment=HorizontalAlignment.Stretch };
            Grid.SetRow(body, 1); root.Children.Add(body);
        }
        else
        {
            pageScroll.Content=scrollContent;
            navigation.PaneHeader = new TextBlock { Text = L10n.T("sF9E02D3C13AA"), Margin = new Thickness(16, 16, 0, 12), Opacity = .6, FontSize = 12 };
            foreach (var (label, tag, icon) in new[] { (L10n.T("sFEA405F9B01D"), "overview", Symbol.Home), (L10n.T("s9251EA0BED6B"), "quota", Symbol.Clock), (L10n.T("sB0E9050DAAAC"), "breakdown", Symbol.Library), (L10n.T("sC3A37174E04A"), "sessions", Symbol.Document), (L10n.T("sDF3D58C7D84B"), "settings", Symbol.Setting), (L10n.T("s5E2B0B3F20D6"), "about", Symbol.Help) })
            {
                var item=new NavigationViewItem { Content = label, Tag = tag, Icon = new SymbolIcon(icon) };
                if(tag is "settings" or "about")navigation.FooterMenuItems.Add(item);else navigation.MenuItems.Add(item);
            }
            navigation.PaneFooter = paneFooter;
            ToolTipService.SetToolTip(paneFooter,"UsageLoom "+Program.Version+L10n.T("s9351B06A6B3A"));
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
        var initial=navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(item=>(string)item.Tag==(requestedPage??app.PreviewPage))
            ??(NavigationViewItem)navigation.MenuItems[0];
        navigation.SelectedItem=initial;
        Program.Log.Write("INFO","Navigation","Initial page ready: "+selectedPage);
        if(app.NavigationCheck)
        {
            var restore=selectedPage;
            ShowPage("settings");
            if(selectedPage!="settings")throw new InvalidOperationException("Settings shortcut navigation failed");
            ShowPage("overview");
            if(selectedPage!="overview")throw new InvalidOperationException("Statistics shortcut navigation failed");
            ShowPage(restore);
            Program.Log.Write("INFO","NavigationTest","Settings/statistics shortcut targets passed");
        }
        if(app.PersonalizationCheck){Navigate("settings");_ = VerifyPersonalizationAsync();}
        if(app.NavigationCheck&&!app.PersonalizationCheck)
        {
            var step=0;var route=new[]{"sessions","overview","quota","overview","breakdown","overview","settings","overview","about","overview","overview","overview"};
            navigationTest=DispatcherQueue.CreateTimer();navigationTest.Interval=TimeSpan.FromMilliseconds(200);
            navigationTest.Tick+=(_,_)=>
            {
                if(step>=route.Length){navigationTest.Stop();Program.Log.Write("INFO","NavigationTest","Repeated navigation passed");return;}
                Program.Log.Write("INFO","NavigationTest","Visit "+route[step]);Navigate(route[step++]);
                VerifyHistoryFilterAttached();
            };navigationTest.Start();
        }
    }
    private void VerifyHistoryFilterAttached()
    {
        if(selectedPage is not ("overview" or "breakdown" or "sessions"))return;
        ((FrameworkElement)Content).UpdateLayout();
        DependencyObject? current=filterBar;
        var expected=selectedPage=="sessions"?(DependencyObject)sessionWorkspace:statsPanel;
        while(current is not null&&!ReferenceEquals(current,expected))current=VisualTreeHelper.GetParent(current);
        if(current is null||historyRangeButtons.ActualWidth<=0||historyRangeButtons.ActualHeight<=0||historyRangeButtonList.Count!=5)
            throw new InvalidOperationException("History filter detached or invisible after navigation: "+selectedPage);
        Program.Log.Write("INFO","NavigationTest","History filter visible: "+selectedPage);
    }
    private void UpdatePaneFooter()
    {
        var expanded=navigation.IsPaneOpen;
        paneFooter.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;
        paneFooter.Text=L10n.T("sE58C4E76F892")+Program.Version+L10n.T("s6A7203B5D588");
        paneFooter.FontSize=12;
        paneFooter.Margin=new Thickness(16,20,8,24);
        paneFooter.TextAlignment=TextAlignment.Left;
        paneFooter.TextWrapping=TextWrapping.NoWrap;
    }
    private async Task VerifyPersonalizationAsync()
    {
        try
        {
            var theme=appearanceChoice!;var language=languageChoice!;
            await Task.Delay(100);
            foreach(var expander in ((StackPanel)scrollContent.Content).Children.OfType<Expander>())
            {
                for(var cycle=0;cycle<3;cycle++)
                {
                    expander.IsExpanded=true;expander.UpdateLayout();await Task.Delay(32);
                    if(!StableExpander.HasVisibleContent(expander))throw new InvalidOperationException("Expander content did not appear immediately");
                    expander.IsExpanded=false;expander.UpdateLayout();await Task.Delay(32);
                    if(StableExpander.HasVisibleContent(expander))throw new InvalidOperationException("Expander retained delayed collapse content");
                }
            }
            Program.Log.Write("INFO","PersonalizationTest","Repeated expand/collapse visibility passed");
            foreach(var index in new[]{1,2,0})
            {
                theme.SelectedIndex=index;await Task.Delay(150);
                var expected=index switch{1=>ElementTheme.Light,2=>ElementTheme.Dark,_=>ElementTheme.Default};
                var root=(FrameworkElement)Content;
                if(root.RequestedTheme!=expected||(expected!=ElementTheme.Default&&root.ActualTheme!=expected))throw new InvalidOperationException("Theme did not apply immediately");
                Program.Log.Write("INFO","PersonalizationTest",$"Theme {expected}: actual={root.ActualTheme}");
            }
            var active=L10n.Language;
            language.SelectedIndex=active=="en-US"?0:1;await Task.Delay(100);
            if(app.Config.Language==active||L10n.Language!=active)throw new InvalidOperationException("Language restart boundary failed");
            language.SelectedIndex=active=="en-US"?1:0;
            Program.Log.Write("INFO","PersonalizationTest","Theme controls and language restart boundary passed");
            var date=new DateOnly(2026,9,8);
            var trend=UsageCharts.Trend([new(date,date,"12:00",154650000,1234,178.9078m)],(_,_)=>{},true);
            var settings=(StackPanel)scrollContent.Content;settings.Children.Add(trend);
            try
            {
                foreach(var width in new[]{380d,520d,900d,520d,380d})
                {
                    trend.Width=width;trend.UpdateLayout();await Task.Delay(60);trend.UpdateLayout();
                    var header=(Grid)((StackPanel)trend).Children[0];
                    var controls=(StackPanel)header.Children[1];var caption=(TextBlock)header.Children[2];
                    var controlBottom=controls.TransformToVisual(header).TransformPoint(new Windows.Foundation.Point(0,controls.ActualHeight)).Y;
                    var captionTop=caption.TransformToVisual(header).TransformPoint(new Windows.Foundation.Point()).Y;
                    if(captionTop<controlBottom+7||controls.ActualWidth>header.ActualWidth+.5)
                        throw new InvalidOperationException($"Trend header overlap at {width} DIP");
                }
                Program.Log.Write("INFO","PersonalizationTest","Trend header 380/520/900 DIP resize geometry passed");
            }
            finally{settings.Children.Remove(trend);}
        }
        catch(Exception ex){Program.Log.Write("ERROR","PersonalizationTest",ex.ToString());}
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
    public void Hide(){if(released)return;capacityInfoFlyout?.Hide();IsPanelVisible=false;AppWindow.Hide();}
    private void Navigate(string tag)=>navigation.SelectedItem=navigation.MenuItems.Concat(navigation.FooterMenuItems).OfType<NavigationViewItem>().First(i=>(string)i.Tag==tag);
    public void Release(){released=true;navigationTest?.Stop();navigation.Loaded-=InitializeNavigation;app.Changed-=Render;}
    public void ApplyTheme(){if(Content is FrameworkElement element)element.RequestedTheme=Enum.TryParse<ElementTheme>(app.Config.Theme,out var theme)?theme:ElementTheme.Default;RefreshSurfaces();}
    private void SelectPage()
    {
        capacityInfoFlyout?.Hide();
        // The filter contains stateful WinUI controls and is shared by overview,
        // breakdown and session workspaces. Detach it while the old page is still
        // connected to the visual tree; detached subtrees do not expose a visual parent.
        DetachHistoryFilter();
        // Detaching changes the UI even when usage data and range are unchanged.
        // Force reconstruction on return instead of reusing the now-empty card.
        renderedFilter=null;
        var (title, subtitle) = selectedPage switch {
            "quota" => (L10n.T("s9251EA0BED6B"), L10n.T("s8BC73914D0EC")),
            "breakdown" => (L10n.T("sB0E9050DAAAC"), L10n.T("s0122629EDAE9")),
            "sessions" => (L10n.T("sC3A37174E04A"), L10n.T("sA3F626B6DD88")),
            "settings" => (L10n.T("sDF3D58C7D84B"), L10n.T("sA13402DE8BF2")),
            "about" => (L10n.T("s5E2B0B3F20D6"), L10n.T("sE4E047F974F7")),
            _ => (L10n.T("sA96599373E64"), L10n.T("s66AFCA0BDCC6")) };
        pageTitle.Text = title; pageSubtitle.Text = subtitle;
        actions.Children.Clear();
        if(selectedPage=="quota")actions.Children.Add(Button(L10n.T("s637A0D380AEC"),async()=>await app.RefreshQuotaAsync(true)));
        else if(selectedPage is "overview" or "breakdown" or "sessions")actions.Children.Add(Button(L10n.T("s020FCEFB6A48"),async()=>await app.ScanAsync()));
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
        UpdateCapacityInfo();
        if(capacityStatusText is not null)capacityStatusText.Text=app.CapacityCalculationStatus;
        if(compact){RenderCompact();return;}
        status.Text=compact||selectedPage=="quota"?app.Quota.Status:selectedPage is "overview" or "breakdown" or "sessions"?app.HistoryStatus:app.Message;
        ToolTipService.SetToolTip(status,status.Text);
        quotaPanel.Children.Clear();var quota=app.Quota;
        overviewQuota.Children.Clear();overviewQuotaCard.Visibility=quota.HasQuotaDisplay?Visibility.Visible:Visibility.Collapsed;
        updateOverviewLayout?.Invoke();
        if(quota.HasQuotaDisplay)
        {
            overviewQuota.Children.Add(new TextBlock{Text=L10n.T("s73BE6011896A"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            foreach(var window in quota.PrimaryWindows)
            {
                overviewQuota.Children.Add(new TextBlock{Text=L10n.F("s6D65FE80C728", window.Label, window.RemainingText),TextWrapping=TextWrapping.Wrap});
                overviewQuota.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=5,Foreground=accent});
            }
            overviewQuota.Children.Add(Button(L10n.T("s14B8852CD2D1"),()=>{Navigate("quota");return Task.CompletedTask;}));
        }
        surfaces.RemoveAll(reference => !reference.TryGetTarget(out _));
        quotaPanel.Children.Add(new TextBlock{Text=app.IsDemo?L10n.T("s9EE75C455D70"):quota.AccountLabel,FontSize=20});
        quotaPanel.Children.Add(PlanBadge(quota,false));
        if(!quota.HasQuotaDisplay)quotaPanel.Children.Add(Card(new TextBlock{Text=L10n.T("s540071E2CE7C")+quota.Status,TextWrapping=TextWrapping.Wrap, FontSize=14}));
        var quotaCards=new List<UIElement>();
        foreach(var window in quota.HasQuotaDisplay?quota.PrimaryWindows:[])
        {
            var card=new StackPanel{Spacing=12};card.Children.Add(new TextBlock{Text=window.Label,FontSize=14,Opacity=.75});
            var values=new Grid{ColumnSpacing=16};
            values.ColumnDefinitions.Add(new(){Width=GridLength.Auto});values.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            values.Children.Add(new TextBlock{Text=window.RemainingText,FontSize=44,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            if(window.Minutes==10080&&app.Config.CapacityEnabled)
            {
                var estimate=quota.Fresh?app.WeeklyCapacity.FirstOrDefault(e=>e.WindowKey==window.Key&&e.ObservedPercent>=5&&e.Samples>=2):null;
                var summary=new StackPanel{Spacing=2,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};
                summary.Children.Add(new TextBlock{Text=estimate?.EstimatedDollars is {} dollars?L10n.F("s1F2CD6B8A261", dollars):quota.Fresh?L10n.T("s3568603BDEE3"):L10n.T("s1D50FA7450FD"),FontSize=24,TextWrapping=TextWrapping.Wrap,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
                summary.Children.Add(new TextBlock{Text=estimate?.HistoricalAt is {} historical?L10n.F("capacity.previousAt",historical.ToLocalTime()):estimate is {} result?L10n.F("sEC62E3CDF86A", result.PricingCoverage):L10n.T("s8FCEC6C0C12E"),FontSize=12,Opacity=.65,TextWrapping=TextWrapping.Wrap});
                var infoRow=new Grid{ColumnSpacing=6,HorizontalAlignment=HorizontalAlignment.Right};
                infoRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});infoRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
                infoRow.Children.Add(summary);var info=CapacityInfoButton();Grid.SetColumn(info,1);infoRow.Children.Add(info);
                Grid.SetColumn(infoRow,1);values.Children.Add(infoRow);
            }
            card.Children.Add(values);
            card.Children.Add(new TextBlock{Text=L10n.T("sFF93499BC7DC"),FontSize=12,Opacity=.65});
            card.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Foreground=accent,Height=6});
            var reset=window.ResetsAt is {} at?L10n.F("sE035EFC6DE6D", at.ToLocalTime(), window.ResetCountdown(DateTimeOffset.Now)):L10n.T("sFB4EF6852264");
            card.Children.Add(new TextBlock{Text=reset,TextWrapping=TextWrapping.Wrap,Opacity=.7});
            quotaCards.Add(Card(card));
        }
        var quotaGroups=new List<UIElement>();
        if(quotaCards.Count>0)quotaGroups.Add(ResponsiveCards(quotaCards,1,300));
        else if(quota.HasQuotaDisplay)quotaGroups.Add(Card(new TextBlock{Text=L10n.T("s3073BC52B5B6"),TextWrapping=TextWrapping.Wrap,Opacity=.7}));
        if(quota.HasQuotaDisplay&&quota.Windows.Any(window=>window.IsSpark))
        {
            var spark=new StackPanel{Spacing=12};
            spark.Children.Add(new TextBlock{Text=L10n.T("s5DBFC3E540A5"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            spark.Children.Add(new TextBlock{Text=L10n.T("s890F611B43D4"),FontSize=12,Opacity=.65,TextWrapping=TextWrapping.Wrap});
            foreach(var window in quota.Windows.Where(window=>window.IsSpark).OrderBy(window=>window.Minutes))
            {
                var row=new StackPanel{Spacing=6};
                row.Children.Add(new TextBlock{Text=L10n.F("s6D65FE80C728", window.Label, window.RemainingText),FontSize=15});
                row.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=4,Foreground=accent});
                row.Children.Add(new TextBlock{Text=window.ResetCountdown(DateTimeOffset.Now),FontSize=12,Opacity=.65,TextWrapping=TextWrapping.Wrap});
                spark.Children.Add(row);
            }
            quotaGroups.Add(Card(spark));
        }
        if(quotaGroups.Count>0)quotaPanel.Children.Add(ResponsiveCards(quotaGroups,2,360));
        if(quota.HasQuotaDisplay)quotaPanel.Children.Add(new TextBlock{Text=L10n.T("s9672B36B01C7")+(quota.ResetCount is {} n?n+L10n.T("sF81526EFCE19"):L10n.T("sF36CAC96220B")),FontSize=14,Margin=new Thickness(0,4,0,0)});
        if(!compact)quotaPanel.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("sAD63795746F7"),Content=new TextBlock{Text=quota.Status+L10n.T("s75B413F02FD0"),TextWrapping=TextWrapping.Wrap},HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
        quotaPanel.Children.Add(new TextBlock{Text=app.IsDemo?L10n.T("sC632B4643CF1"):quota.IsLocalAccount?L10n.T("sCEF08E1DE7C2"):quota.FetchedAt is {} date?L10n.F("sDE0A52AD84EA", date, (quota.Fresh?L10n.T("sAF82A5FFDAE8"):L10n.T("s2FE0E3339AC4"))):L10n.T("s66C3773FD559"),Opacity=.65});
        if(!compact&&selectedPage is "overview" or "breakdown" or "sessions")RenderStats();
        } finally { rendering=false; }
    }
    private Button Button(string label,Func<Task> action)
    {
        var button=new Button{Content=label,CornerRadius=new CornerRadius(9),Padding=new Thickness(16,9,16,9)};button.Click+=async(_,_)=>{button.IsEnabled=false;try{await action();}catch(Exception ex){Program.Log.Write("ERROR","UI",ex.Message);status.Text=Privacy.Redact(ex.Message);}finally{button.IsEnabled=true;}};return button;
    }
}

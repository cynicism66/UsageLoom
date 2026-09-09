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
            };navigationTest.Start();
        }
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
                ToolTipService.SetToolTip(summary,app.WeeklyCapacityProgress);Grid.SetColumn(summary,1);values.Children.Add(summary);
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
    private UIElement CapacitySettings()
    {
        var quota=app.Quota;
        var estimates=new StackPanel{Spacing=10};
        var enabled=new ToggleSwitch{Header=L10n.T("s290DF536AEA0"),IsOn=app.Config.CapacityEnabled,OnContent=L10n.Language=="en-US"?"On":"开",OffContent=L10n.Language=="en-US"?"Off":"关"};
        enabled.Toggled+=async(_,_)=>await app.SetCapacityEnabledAsync(enabled.IsOn);
        estimates.Children.Add(enabled);
        estimates.Children.Add(new TextBlock{Text=L10n.T("s12A977A8C9EB"),TextWrapping=TextWrapping.Wrap});
        estimates.Children.Add(new TextBlock{Text=L10n.T("s49E8E1E2DCE1"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        if(app.Config.CapacityEnabled&&quota.Fresh&&quota.HasQuotaDisplay&&app.WeeklyCapacity.Any(e=>e.ObservedPercent>=5&&e.Samples>=2))
        {
            foreach(var estimate in app.WeeklyCapacity.Where(e=>e.ObservedPercent>=5&&e.Samples>=2))
            {
                if(estimate.HistoricalAt is {} historical)estimates.Children.Add(new TextBlock{Text=L10n.F("capacity.previousAt",historical.ToLocalTime()),TextWrapping=TextWrapping.Wrap});
                estimates.Children.Add(ResponsiveCards(new List<UIElement>{
                    Metric(L10n.T("s53D9E8B59877"),estimate.DollarDisplay,L10n.F("s1EA0E159E65E", estimate.PricingCoverage)),
                    Metric(L10n.T("s758E9EDBBD77"),$"{estimate.EstimatedTokens:N0}",L10n.F("s46B941557FE8", estimate.Confidence))},2,300));
                estimates.Children.Add(new TextBlock{Text=L10n.F("s779F484C5099", estimate.Samples, estimate.ObservedTokens, estimate.ObservedPercent)+(estimate.ExcludedIntervals>0?L10n.F("sBF4A33F67127", estimate.ExcludedIntervals):""),TextWrapping=TextWrapping.Wrap,Opacity=.7});
                estimates.Children.Add(new TextBlock{Text=estimate.DollarLow is {} low&&estimate.DollarHigh is {} high?L10n.F("capacity.range",low,high):L10n.T("capacity.rangePending"),TextWrapping=TextWrapping.Wrap,Opacity=.7});
            }
        }
        else estimates.Children.Add(new TextBlock{Text=app.WeeklyCapacityProgress,TextWrapping=TextWrapping.Wrap});
        estimates.Children.Add(new TextBlock{Text=app.CapacityCacheStatus,FontSize=12,Opacity=.65,TextWrapping=TextWrapping.Wrap});
        estimates.Children.Add(Button(L10n.T("s4C6D9D73BFC8"),ShowCapacityHistory));
        estimates.Children.Add(Button(L10n.T("capacity.calculateNow"),async()=>await app.CalculateCapacityNowAsync()));
        capacityStatusText=new TextBlock{Text=app.CapacityCalculationStatus,TextWrapping=TextWrapping.Wrap};
        estimates.Children.Add(capacityStatusText);
        estimates.Children.Add(new TextBlock{Text=L10n.T("capacity.batchNote"),TextWrapping=TextWrapping.Wrap,Opacity=.65});
        estimates.Children.Add(new TextBlock{Text=L10n.T("capacity.retentionNote"),TextWrapping=TextWrapping.Wrap,Opacity=.65});
        var calculation=new StackPanel{Spacing=10};
        calculation.Children.Add(new TextBlock{Text=L10n.T("s0F58A8B1B0E1"),TextWrapping=TextWrapping.Wrap,FontSize=12,Opacity=.7});
        calculation.Children.Add(new TextBlock{Text=L10n.T("capacity.stableNote"),TextWrapping=TextWrapping.Wrap,FontSize=12,Opacity=.7});
        calculation.Children.Add(Button(L10n.T("s89CE4722B00F"),async()=>
        {
            var dialog=new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=L10n.T("s7561011AB974"),Content=L10n.T("sA975EF949AB2"),PrimaryButtonText=L10n.T("sCB5D682BAC3D"),CloseButtonText=L10n.T("s2CD0F3BE8738"),DefaultButton=ContentDialogButton.Close};
            if(await dialog.ShowAsync()==ContentDialogResult.Primary)await app.ResetCapacityAsync();
        }));
        estimates.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("s6B5C96B6A49F"),IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=calculation}));
        return StableExpander.Configure(new Expander{Header=L10n.T("sD9EBFF4C171F"),IsExpanded=false,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=estimates});
    }
    private async Task ShowAttribution(List<UsageEvent> rows,string account)
    {
        var dates=rows.Where(e=>e.AccountScope is null&&e.Timestamp is not null).Select(e=>e.LocalDate).Distinct().OrderDescending().ToList();
        var panel=new StackPanel{Spacing=12};
        panel.Children.Add(new TextBlock{Text=L10n.T("attribution.explanation"),TextWrapping=TextWrapping.Wrap});
        var date=new ComboBox{Header=L10n.T("attribution.date"),ItemsSource=dates,SelectedIndex=dates.Count>0?0:-1};panel.Children.Add(date);
        var summary=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(summary);
        var acknowledgment=new CheckBox{Content=L10n.T("attribution.ack")};panel.Children.Add(acknowledgment);
        var dialog=new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=L10n.T("attribution.inspect"),Content=panel,
            PrimaryButtonText=L10n.T("attribution.confirm"),CloseButtonText=L10n.T("s2CD0F3BE8738"),DefaultButton=ContentDialogButton.Close,IsPrimaryButtonEnabled=false};
        List<UsageEvent> preview=[];
        void Update()
        {
            preview=rows.Where(e=>e.LocalDate==date.SelectedItem as string&&e.AccountScope is null&&e.Timestamp is not null).ToList();
            var other=rows.Where(e=>e.AccountScope is not null&&e.AccountScope!=account).Sum(e=>e.Tokens.Total);
            var unknown=rows.Where(e=>e.AccountScope is null&&e.Timestamp is null).Sum(e=>e.Tokens.Total);
            summary.Text=L10n.F("attribution.preview",preview.Count,UsageNumbers.Compact(preview.Sum(e=>e.Tokens.Total)),UsageNumbers.Compact(other),UsageNumbers.Compact(unknown));
            dialog.IsPrimaryButtonEnabled=acknowledgment.IsChecked==true&&preview.Count>0&&app.Quota.Fresh&&app.Quota.AccountKey==account&&!app.IsDemo;
        }
        date.SelectionChanged+=(_,_)=>{acknowledgment.IsChecked=false;Update();};
        acknowledgment.Checked+=(_,_)=>Update();acknowledgment.Unchecked+=(_,_)=>Update();Update();
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        try{await app.ConfirmAttributionAsync(preview,account);}
        catch(Exception){await new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=L10n.T("attribution.retry"),Content=L10n.T("attribution.unchanged"),CloseButtonText=L10n.T("s2CD0F3BE8738")}.ShowAsync();}
    }
    private async Task ShowCapacityHistory()

    {
        var entries=new StackPanel{Spacing=12};var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};
        var content=new StackPanel{Spacing=12};var pageIndex=0;
        content.Children.Add(new TextBlock{Text=L10n.T("s031841781B24"),TextWrapping=TextWrapping.Wrap});
        content.Children.Add(new ScrollViewer{Content=entries,MaxHeight=440,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});content.Children.Add(controls);
        var previous=new Button{Content=L10n.T("sC9B9AE7A6144"),IsEnabled=false};var next=new Button{Content=L10n.T("s8A8542F69648")};var pageLabel=new TextBlock{VerticalAlignment=VerticalAlignment.Center};
        controls.Children.Add(previous);controls.Children.Add(pageLabel);controls.Children.Add(next);
        async Task Load()
        {
            previous.IsEnabled=next.IsEnabled=false;
            try
            {
                var records=await app.ReadCapacityHistoryAsync(pageIndex);entries.Children.Clear();
                if(records.Count==0)entries.Children.Add(new TextBlock{Text=L10n.T("s2B3447EE0CCC"),TextWrapping=TextWrapping.Wrap});
                foreach(var record in records)foreach(var sample in record.Windows)
                {
                    var ready=sample.Percent>=5&&sample.Samples>=2;
                    var same=record.Account==app.Quota.AccountKey;
                    var scope=record.Version==4&&record.PricingVersion==Pricing.CatalogVersion?L10n.T("s266D6CF4495A"):L10n.T("s6662BBA41E7D");
                    var dollars=sample.Priced>0?$"${sample.Cost*100m/(decimal)sample.Percent:N2}":L10n.T("s2CE9AC771C39");
                    entries.Children.Add(new TextBlock{Text=L10n.F("s403BBCA72EE4", record.SavedAt.ToLocalTime(), (same?L10n.T("sB56996D44E4A"):L10n.T("s92E27D68CB29")), record.Plan, sample.ResetsAt.ToLocalTime(), (ready?L10n.T("s86011ED09937"):L10n.T("s2C63A632051B")), scope, record.Version, dollars, UsageNumbers.Compact(sample.Tokens*100d/sample.Percent), 100d*sample.Priced/sample.Tokens, sample.Samples, sample.Percent, record.PricingVersion),TextWrapping=TextWrapping.Wrap,FontSize=13});
                }
                previous.IsEnabled=pageIndex>0;next.IsEnabled=records.Count==20;pageLabel.Text=L10n.F("s94289AA33F49", pageIndex+1);
            }
            catch(Exception ex){entries.Children.Clear();entries.Children.Add(new TextBlock{Text=L10n.T("s1B070A03BD9B")+Privacy.Redact(ex.Message),TextWrapping=TextWrapping.Wrap});previous.IsEnabled=pageIndex>0;}
        }
        previous.Click+=async(_,_)=>{pageIndex=Math.Max(0,pageIndex-1);await Load();};next.Click+=async(_,_)=>{pageIndex++;await Load();};
        await Load();
        await new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=L10n.T("s4C6D9D73BFC8"),Content=content,CloseButtonText=L10n.T("s3FD47EDCE45B")}.ShowAsync();
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
        if(!quota.Fresh)stack.Children.Add(new TextBlock{Text=L10n.T("s4F6E26963CA2"),FontSize=11,Opacity=.65});
        ToolTipService.SetToolTip(stack,L10n.F("s43DABF58694C", quota.Plan));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stack,label+(quota.Fresh?"":L10n.T("s525117F3D826")));
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
                var name=Label(window.Label+L10n.T("sD6822B04178D"));name.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(name);
                var value=Label(window.RemainingText,28);value.FontWeight=Microsoft.UI.Text.FontWeights.SemiBold;Grid.SetColumn(value,1);row.Children.Add(value);
                section.Children.Add(row);
                section.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=4,Foreground=accent});
                var reset=Label(window.ResetCountdown(DateTimeOffset.Now)+(quota.Fresh?"":L10n.T("s6749C5BF4AEA")),12);reset.Opacity=.65;section.Children.Add(reset);
                limits.Children.Add(section);
            }
            quotaPanel.Children.Add(CompactCard(limits));
        }
        else
        {
            var unavailable=Label(quota.IsLocalAccount?L10n.T("sF2D563561B79"):L10n.T("sF7A79B776C96"));
            ToolTipService.SetToolTip(unavailable,quota.Status);quotaPanel.Children.Add(CompactCard(unavailable));
        }
        var today=DateTime.Today.ToString("yyyy-MM-dd");
        var rows=app.Events.Where(item=>item.LocalDate==today).ToList();
        var usage=new Grid{ColumnSpacing=16};usage.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});usage.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var tokens=new StackPanel{Spacing=4};tokens.Children.Add(Label(L10n.T("s8B4B6EA0DA7D"),12));tokens.Children.Add(Label(app.HasLoadedHistory?UsageNumbers.Compact(rows.Sum(item=>item.Tokens.Total)):L10n.T("sD04FCBDA737F"),22));
        ToolTipService.SetToolTip(tokens,$"{rows.Sum(item=>item.Tokens.Total):N0} Token");
        var requests=new StackPanel{Spacing=4};requests.Children.Add(Label(L10n.T("sBDBF4C48B0A9"),12));requests.Children.Add(Label(app.HasLoadedHistory?rows.Count.ToString("N0"):L10n.T("sD04FCBDA737F"),22));
        ToolTipService.SetToolTip(requests,L10n.T("sA19F66EB1728"));
        usage.Children.Add(tokens);Grid.SetColumn(requests,1);usage.Children.Add(requests);quotaPanel.Children.Add(CompactCard(usage));
        var estimate=quota.Fresh&&quota.HasQuotaDisplay?app.WeeklyCapacity.FirstOrDefault(e=>e.ObservedPercent>=5&&e.Samples>=2):null;
        var capacity=Label(estimate is not null?L10n.F("s99933FEC8200", estimate.DollarDisplay, UsageNumbers.Compact(estimate.EstimatedTokens), estimate.PricingCoverage):quota.Fresh&&quota.PrimaryWindows.Any(window=>window.Minutes==10080)?L10n.T("s51D06B357E84"):L10n.T("s5AFCDACD073E"),12);
        // Reserve the same two-line slot in every sampling state. Otherwise the
        // content-fitting window grows and moves when an estimate becomes ready.
        if(estimate?.HistoricalAt is {} historical)capacity.Text=L10n.T("capacity.previous")+": "+estimate.DollarDisplay;
        capacity.Height=40;capacity.MaxLines=2;capacity.TextTrimming=TextTrimming.CharacterEllipsis;
        capacity.Visibility=app.Config.CapacityEnabled?Visibility.Visible:Visibility.Collapsed;
        capacity.Opacity=.75;ToolTipService.SetToolTip(capacity,capacity.Text+"\n"+(estimate?.HistoricalAt is {} saved?L10n.F("capacity.previousAt",saved.ToLocalTime())+"\n":"")+app.WeeklyCapacityProgress);quotaPanel.Children.Add(capacity);
        var updated=quota.FetchedAt is {} at?L10n.F("s6843540FA5C7", at.ToLocalTime()):L10n.T("s0D4EDA666026");
        var resets=quota.HasQuotaDisplay&&quota.ResetCount is {} count?L10n.F("s26ABA9EC2EFB", count):L10n.T("s382254F4153B");
        quotaPanel.Children.Add(PlanBadge(quota,true));
        var footer=Label($"{resets}   ·   {updated}",12);footer.Opacity=.6;
        footer.MaxLines=1;footer.TextTrimming=TextTrimming.CharacterEllipsis;footer.VerticalAlignment=VerticalAlignment.Center;
        var footerRow=new Grid{ColumnSpacing=8};
        footerRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        footerRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        footerRow.Children.Add(footer);
        var settings=new Button{Content=new FontIcon{Glyph="\uE713",FontSize=18},Width=32,Height=32,Padding=new Thickness(0),
            Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(6)};
        ToolTipService.SetToolTip(settings,L10n.T("sDF3D58C7D84B"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settings,L10n.T("sDF3D58C7D84B"));
        settings.Click+=(_,_)=>{app.ShowSettings();if(!pinned)Hide();};
        Grid.SetColumn(settings,1);footerRow.Children.Add(settings);quotaPanel.Children.Add(footerRow);
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
            overviewQuota.Children.Add(new TextBlock{Text=L10n.T("s73BE6011896A"),FontSize=18});
            foreach(var window in app.Quota.PrimaryWindows)
            {
                overviewQuota.Children.Add(new TextBlock{Text=L10n.F("s6D65FE80C728", window.Label, window.RemainingText),TextWrapping=TextWrapping.Wrap});
                overviewQuota.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=5,Foreground=accent});
            }
            overviewQuota.Children.Add(Button(L10n.T("s14B8852CD2D1"),()=>{Navigate("quota");return Task.CompletedTask;}));
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
            var inferredTotal=rows.Where(item=>item.AccountScope==accountKey&&item.AccountAttribution is ("restart-inferred" or "user-confirmed")).Sum(item=>item.Tokens.Total);
            var remainder=Math.Max(0,total.Total-accountTotal);
            var accountCards=new[]{Metric(L10n.T("s106A6D3ED1C9"),total.Total.ToString("N0"),L10n.T("s89FA01F64D00")),Metric(L10n.T(inferredTotal>0?"restart.account":"sF866B39A1323"),accountTotal.ToString("N0"),inferredTotal>0?L10n.F("restart.note",UsageNumbers.Compact(inferredTotal)):L10n.T("s9D26E20C07EF")),Metric(L10n.T("s79420D8F355C"),remainder.ToString("N0"),L10n.T("s2544E8C23B66"))};
            statsPanel.Children.Add(Card(ResponsiveCards(accountCards,3,210)));
            statsPanel.Children.Add(Button(L10n.T("attribution.inspect"),()=>ShowAttribution(rows.ToList(),accountKey)));
            cards=[Metric("Session",sessionCount.ToString("N0"),L10n.T("s65188C08136A")),Metric(L10n.T("s9386F02260C5"),rows.Count.ToString("N0"),L10n.T("s1C7995A11FC3")),Metric(estimate.Status,rows.Count==0?L10n.T("s497C85690C4C"):estimate.DisplayAmount,L10n.T("s479A28B8FEA5")),Metric(L10n.T("s79868BCABA0B"),total.Total>0?$"{100d*estimate.Priced/total.Total:0.#}%":"—",L10n.F("s7A93A1B1657A", estimate.Unpriced))];
        }
        else cards=[Metric(L10n.T("s52497358558A"),total.Total.ToString("N0"),L10n.T("sC9084B3EDE58")),Metric("Session",sessionCount.ToString("N0"),L10n.F("s16BF7608AEAB", rows.Count)),Metric(estimate.Status,rows.Count==0?L10n.T("s497C85690C4C"):estimate.DisplayAmount,L10n.T("s479A28B8FEA5")),Metric(L10n.T("s79868BCABA0B"),total.Total>0?$"{100d*estimate.Priced/total.Total:0.#}%":"—",L10n.F("s7A93A1B1657A", estimate.Unpriced))];
        var metrics=ResponsiveCards(cards,4,190);statsPanel.Children.Add(Card(metrics));
        if(rows.Count>0)
        {
            var details=new StackPanel{Spacing=12};
            details.Children.Add(new TextBlock{Text=L10n.F("s217DBBB4BEE1", Pricing.CatalogVersion),TextWrapping=TextWrapping.Wrap,FontSize=13});
            if(!string.IsNullOrWhiteSpace(estimate.Reason))details.Children.Add(new TextBlock{Text=estimate.Reason,TextWrapping=TextWrapping.Wrap});
            foreach(var note in estimate.Notes)details.Children.Add(new TextBlock{Text="• "+note,TextWrapping=TextWrapping.Wrap,FontSize=12});
            foreach(var group in rows.GroupBy(e=>e.Model))
            {
                var basis=Pricing.Find(group.Key);var item=Pricing.Summarize(group);
                details.Children.Add(new TextBlock{Text=$"{group.Key} · {item.Status} · {item.DisplayAmount}",FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
                if(basis is null){details.Children.Add(new TextBlock{Text=L10n.T("sF37B3009AB7F"),TextWrapping=TextWrapping.Wrap});continue;}
                var price=basis.Rates;
                details.Children.Add(new TextBlock{Text=L10n.F("sBB890CE3940F", price.Input, price.Cached?.ToString()??L10n.T("s4D8C1C5B4283"), price.Write?.ToString()??L10n.T("s4D8C1C5B4283"), price.Output, basis.CheckedOn, basis.EffectiveFrom?.ToString("yyyy-MM-dd")??L10n.T("sB40CA4D5DDA7"), basis.EffectiveUntil?.ToString("yyyy-MM-dd")??L10n.T("sB40CA4D5DDA7"))+(basis.PromotionAtLeastThrough is {} promo?L10n.F("s45A4A87C6785", promo):""),TextWrapping=TextWrapping.Wrap,FontSize=12});
                details.Children.Add(new HyperlinkButton{Content=L10n.T("sEA5EC54A64AB"),NavigateUri=basis.Source});
            }
            pricingDetails=StableExpander.Configure(new Expander{Header=L10n.T("s0ADC58E779A7"),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=details});
        }
        }
        else statsPanel.Children.Add(new TextBlock{Text=L10n.F("sDC533DFEA032", rows.Count, total.Total, estimate.DisplayAmount, estimate.Status),TextWrapping=TextWrapping.Wrap,Opacity=.7});
        if(rows.Count==0)statsPanel.Children.Add(Card(new TextBlock{Text=app.Events.Count==0?L10n.T("s9610A3870D65"):historyRangeIndex==4&&!selectedRange.IsBounded?L10n.T("sFC65DDBF820C"):L10n.T("sFB1B61C7B33C"),FontSize=18,TextWrapping=TextWrapping.Wrap}));
        if(selectedPage=="overview")
        {
            var hourly=selectedRange.IsSingleDay;
            var trend = UsageCharts.Trend(hourly?HistoryQuery.HourlyTrend(rows,selectedRange.From!.Value):HistoryQuery.Trend(rows,selectedRange),DrillIntoRange,hourly);
            var modelPanel=new StackPanel{Spacing=12};modelPanel.Children.Add(new TextBlock{Text=L10n.T("s39F1A54A74FF"),FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            modelPanel.Children.Add(UsageCharts.Models(rows));
            statsPanel.Children.Add(Card(trend));
            var recent=new StackPanel{Spacing=12};recent.Children.Add(new TextBlock{Text=L10n.T("sEB6D48FEBAB7"),FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            foreach(var session in HistoryQuery.Sessions(rows,"recent",app.SessionNames).Take(4))recent.Children.Add(new TextBlock{Text=L10n.F("sE1A5A63BFA3F", session.Name, session.Tokens, session.Project, session.ShortId),TextWrapping=TextWrapping.Wrap,FontSize=13});
            recent.Children.Add(Button(L10n.T("sD24CA3C355C7"),()=>{Navigate("sessions");return Task.CompletedTask;}));
            // The quota summary updates independently without collapsing expanded history controls.
            var recentCard=Card(recent);
            var bottomItems=new List<UIElement>{Card(modelPanel),recentCard,overviewQuotaCard};var bottom=ResponsiveCards(bottomItems,3,300);
            var localQuotaCard=overviewQuotaCard;
            void FitBottom(){Grid.SetColumnSpan(recentCard,localQuotaCard.Visibility==Visibility.Collapsed&&bottom.ColumnDefinitions.Count>=3?2:1);}
            bottom.SizeChanged+=(_,_)=>FitBottom();updateOverviewLayout=FitBottom;FitBottom();
            statsPanel.Children.Add(bottom);
            var composition=new StackPanel{Spacing=10};composition.Children.Add(new TextBlock{Text=L10n.T("s719CAC683641"),FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            composition.Children.Add(new TextBlock{Text=L10n.F("sF6A9702B386C", total.Input, total.Output),FontSize=16});
            composition.Children.Add(new TextBlock{Text=L10n.F("sE1435C7E009C", total.Cached, total.CacheWrite, total.Reasoning),TextWrapping=TextWrapping.Wrap,Opacity=.65});
            composition.Children.Add(new TextBlock{Text=L10n.T("s4D584456798B"),FontSize=12,Opacity=.6});statsPanel.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("sA29E4482CC69"),Content=composition,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
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
        var summary=new TextBlock{Text=L10n.F("sDC533DFEA032", rows.Count, total.Total, estimate.DisplayAmount, estimate.Status),TextWrapping=TextWrapping.Wrap,Opacity=.7,VerticalAlignment=VerticalAlignment.Center};
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
            detail.Children.Add(new TextBlock{Text=L10n.T("s37C7E8C78184")+session.Session,FontSize=11,Opacity=.55,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true});
            detail.Children.Add(new TextBlock{Text=L10n.F("s89DF7DB2285B", session.Project, session.LastActivity?.ToLocalTime(), session.Tokens, Pricing.Summarize(session.Events).DisplayAmount),TextWrapping=TextWrapping.Wrap});
            detail.Children.Add(new TextBlock{Text=L10n.F("s06B0C7A9C706", tokens.Input, tokens.Output, tokens.Cached, tokens.CacheWrite, tokens.Reasoning),TextWrapping=TextWrapping.Wrap,LineHeight=26});
            detail.Children.Add(new TextBlock{Text=L10n.T("s80F5416751AC"),Opacity=.65});
            var shown=0;
            void More()
            {
                foreach(var item in session.Events.Skip(shown).Take(50))
                {
                    var line=new TextBlock{Text=$"{item.LocalDate} {item.Timestamp?.ToLocalTime():HH:mm:ss} · {item.Model}\n{item.Tokens.Total:N0} Token"+(item.QualityNote is {} note?" · "+note:""),TextWrapping=TextWrapping.Wrap,FontSize=12,Padding=new Thickness(0,8,0,8)};
                    detail.Children.Add(new Border{Child=line,BorderThickness=new Thickness(0,1,0,0),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(28,150,155,170))});
                }
                shown=Math.Min(session.Events.Count,shown+50);
                if(shown<session.Events.Count){Button? more=null;more=Button(L10n.T("s1E3B71D39432"),()=>{detail.Children.Remove(more!);More();return Task.CompletedTask;});detail.Children.Add(more);}
            }
            More();
            DispatcherQueue.TryEnqueue(()=>detailScroll.ChangeView(null,0,null));
        }
        foreach(var session in sessions.Skip(sessionPage*pageSize).Take(pageSize))list.Items.Add(new ListViewItem{Tag=session,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=new TextBlock{Text=L10n.F("sE1A5A63BFA3F", session.Name, session.Tokens, session.Project, session.ShortId),TextWrapping=TextWrapping.Wrap},Padding=new Thickness(12)});
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is ListViewItem{Tag:SessionSummary session})ShowSession(session);};
        if(list.Items.Count>0)list.SelectedIndex=0;else detail.Children.Add(new TextBlock{Text=app.Events.Count==0?L10n.T("s9610A3870D65"):L10n.T("s8A69095CA983"),FontSize=18,TextWrapping=TextWrapping.Wrap});

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
        panel.Children.Add(Row(title,L10n.T("s2087C777C06F"),L10n.T("s1DED6559F626"),L10n.T("sFB04ADDB4C26"),L10n.T("s9638021CAEE5"),L10n.T("sC6CA9057CACD"),L10n.T("sCC87B27B644A")));
        foreach(var group in table)
        {
            var tokens=group.Aggregate(new TokenUsage(),(sum,item)=>sum+item.Tokens);var estimate=Pricing.Summarize(group);
            var row=Row(group.Key,tokens.Input.ToString("N0"),tokens.Cached.ToString("N0"),tokens.Output.ToString("N0"),tokens.Total.ToString("N0"),estimate.DisplayAmount,total>0?$"{100d*tokens.Total/total:0.#}%":"—");
            ToolTipService.SetToolTip(row,estimate.Status+" · "+estimate.Reason);
            panel.Children.Add(new Border{Child=row,BorderThickness=new Thickness(0,1,0,0),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(35,140,145,160))});
        }
        statsPanel.Children.Add(Card(new ScrollViewer{Content=panel,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollMode=ScrollMode.Enabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollMode=ScrollMode.Disabled}));
        statsPanel.Children.Add(new TextBlock{Text=L10n.T("s5689C7F8C468"),Opacity=.65,FontSize=12,TextWrapping=TextWrapping.Wrap});
    }
    private UIElement SettingsPanel()
    {
        var panel=new StackPanel{Spacing=14,Padding=new Thickness(8)};
        var cli=new TextBox{Header=L10n.T("s7BA827935C21"),Text=app.Config.CliPath??""};
        var home=new TextBox{Header=L10n.T("s629E14E0CB84"),Text=app.IsDemo?L10n.T("sF99723F1D4A1"):app.Config.CodexHome,IsReadOnly=app.IsDemo};
        var auto=new ToggleSwitch{Header=L10n.T("sB91E861CB0A1"),IsOn=app.Config.AutoRefresh,OnContent=L10n.Language=="en-US"?"On":"开",OffContent=L10n.Language=="en-US"?"Off":"关"};
        var seconds=new NumberBox{Header=L10n.T("sEE9FB7A5B7ED"),Minimum=30,Maximum=3600,Value=app.Config.BackgroundSeconds};
        var foreground=new NumberBox{Header=L10n.T("s2791B0EC8468"),Minimum=15,Maximum=3600,Value=app.Config.ForegroundSeconds};
        var low=new ToggleSwitch{Header=L10n.T("s843B9B07A843"),IsOn=app.Config.LowNotify,OnContent=L10n.Language=="en-US"?"On":"开",OffContent=L10n.Language=="en-US"?"Off":"关"};
        var threshold=new NumberBox{Header=L10n.T("sE511B92C189E"),Minimum=1,Maximum=99,Value=app.Config.LowPercent};
        var reset=new ToggleSwitch{Header=L10n.T("sD4CF61025E3C"),IsOn=app.Config.ResetNotify,OnContent=L10n.Language=="en-US"?"On":"开",OffContent=L10n.Language=="en-US"?"Off":"关"};
        var minutes=new NumberBox{Header=L10n.T("s87603CAC4453"),Minimum=1,Maximum=120,Value=app.Config.ResetMinutes};
        var theme=new ComboBox{Header=L10n.T("s86A63F23A076"),ItemsSource=new[]{L10n.T("s217CFE7DB1E3"),L10n.T("sAA0819DFC4D8"),L10n.T("sA6B75D068032")},SelectedIndex=app.Config.Theme switch{"Light"=>1,"Dark"=>2,_=>0}};
        theme.SelectionChanged+=(_,_)=>app.SetTheme(theme.SelectedIndex switch{1=>"Light",2=>"Dark",_=>"Default"});
        var language=new ComboBox{Header=L10n.T("s9087B82BC720"),ItemsSource=new[]{"简体中文","English"},SelectedIndex=app.Config.Language=="en-US"?1:0};
        appearanceChoice=theme;languageChoice=language;
        language.SelectionChanged+=(_,_)=>app.SetLanguage(language.SelectedIndex==1?"en-US":"zh-CN");
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
            panel.Children.Add(StableExpander.Configure(new Expander{Header=title,IsExpanded=false,Content=group,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
        }
        Section(L10n.T("s38C043E08502"),theme,language);Section(L10n.T("s6E89737A00E1"),cli,home);Section(L10n.T("s16685D3221B9"),auto,foreground,seconds);Section(L10n.T("sA28590E6B1D8"),low,threshold,reset,minutes);
        panel.Children.Add(CapacitySettings());
        Section(L10n.T("sD39DC68172D7"),
            new TextBlock{Text=L10n.T("s9371F74C3221"),TextWrapping=TextWrapping.Wrap},
            Button(L10n.T("sE400A5FF247B"),async()=>
            {
                await app.UseCachedLoginAsync(cli.Text.Trim(),home.Text.Trim());
            }));
        panel.Children.Add(AppUpdatePanel());
        async Task Authorize(bool logout)
        {
            if(app.Authorizing)throw new InvalidOperationException(L10n.T("s081CDEB65923"));
            var dialog=new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=logout?L10n.T("sFEED4D9A5148"):L10n.T("s3B66445DA90A"),CloseButtonText=L10n.T("s2CD0F3BE8738"),PrimaryButtonText=logout?L10n.T("sADF861A2A343"):L10n.T("sC33740D74933"),DefaultButton=ContentDialogButton.Close,
                Content=logout?L10n.T("s8CF30C934669"):L10n.T("sA591BDDE3429")};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            if(!app.IsDemo)app.Config.CliPath=cli.Text.Trim();
            await app.AuthorizeAsync(logout);
        }
        Section(L10n.T("s5AB943671D29"),
            new TextBlock{Text=L10n.T("s9DD1731C07FC"),TextWrapping=TextWrapping.Wrap},
            Button(L10n.T("s3B0F18AAC8CE"),async()=>await Authorize(false)),
            Button(L10n.T("s16302475FAEB"),()=>{app.CancelAuthorization();return Task.CompletedTask;}),
            Button(L10n.T("s0F7D6E35193A"),async()=>await Authorize(true)));
        actions.Children.Add(Button(L10n.T("sC8550237BA70"),async()=>
        {
            if(app.IsDemo){status.Text=L10n.T("sAF0109C061C5");return;}
            if(app.Authorizing)throw new InvalidOperationException(L10n.T("s3B5CB5F80AA4"));
            if(!double.IsFinite(seconds.Value)||!double.IsFinite(foreground.Value)||!double.IsFinite(threshold.Value)||!double.IsFinite(minutes.Value))throw new ArgumentException(L10n.T("sD1E6C6F01819"));
            if(string.IsNullOrWhiteSpace(home.Text)||!Path.IsPathFullyQualified(home.Text.Trim()))throw new ArgumentException(L10n.T("s772A83DE6A61"));
            app.Config.CliPath=cli.Text.Trim();app.Config.CodexHome=home.Text.Trim();app.Config.AutoRefresh=auto.IsOn;
            app.Config.BackgroundSeconds=(int)Math.Clamp(seconds.Value,30,3600);app.Config.LowNotify=low.IsOn;app.Config.LowPercent=(int)Math.Clamp(threshold.Value,1,99);
            app.Config.ForegroundSeconds=(int)Math.Clamp(foreground.Value,15,3600);
            app.Config.ResetNotify=reset.IsOn;app.Config.ResetMinutes=(int)Math.Clamp(minutes.Value,1,120);app.Config.Theme=theme.SelectedIndex switch{1=>"Light",2=>"Dark",_=>"Default"};app.Config.AppearanceConfigured=true;
            await app.SaveSettingsAsync();ApplyTheme();
        }));
        panel.Children.Add(new TextBlock{Text=L10n.T("sD22BE3762C11"),TextWrapping=TextWrapping.Wrap,Opacity=.7});
        return panel;
    }
    private UIElement AboutPanel()
    {
        var panel=new StackPanel{Spacing=16};
        panel.Children.Add(new TextBlock{Text="UsageLoom  "+Program.Version+L10n.T("s150A42617962"),FontSize=24,TextWrapping=TextWrapping.Wrap});
        var health=new StackPanel{Spacing=12};health.Children.Add(new TextBlock{Text=L10n.T("sE46B0272BD64"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        health.Children.Add(new TextBlock{Text=app.HistoryStatus,TextWrapping=TextWrapping.Wrap});health.Children.Add(new TextBlock{Text=L10n.T("s58D76C9D3286")+app.Quota.AccountLabel,TextWrapping=TextWrapping.Wrap});panel.Children.Add(Card(health));
        panel.Children.Add(Card(new TextBlock{Text=app.DiagnosticSummary,FontFamily=new FontFamily("Cascadia Mono"),FontSize=12,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true}));
        panel.Children.Add(new TextBlock{Text=L10n.T("s89327228F8FF"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        panel.Children.Add(Button(L10n.T("s1481ECABDF10"),async()=>await app.ScanAsync(verifyIntegrity:true)));
        panel.Children.Add(Button(L10n.T("s64525FE00BF7"),async()=>
        {
            var dialog=new ContentDialog
            {
                XamlRoot=((FrameworkElement)Content).XamlRoot,
                Title=L10n.T("sE135A1996A45"),
                Content=L10n.T("sC929716D2A4A"),
                PrimaryButtonText=L10n.T("s3A52FE1583F8"),CloseButtonText=L10n.T("s2CD0F3BE8738"),DefaultButton=ContentDialogButton.Close
            };
            if(await dialog.ShowAsync()==ContentDialogResult.Primary)await app.ScanAsync(rebuild:true);
        }));
        panel.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("sAA0BA9B301E8"),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=new TextBlock{Text=L10n.T("sB22119FAE065")+Pricing.VerifiedDate+L10n.T("sF6F917118B67"),TextWrapping=TextWrapping.Wrap}}));
        actions.Children.Add(Button(L10n.T("s6F7EA9FAD61C"),()=>{var data=new Windows.ApplicationModel.DataTransfer.DataPackage();data.SetText(app.DiagnosticSummary);Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);return Task.CompletedTask;}));
        return panel;
    }
    private Button Button(string label,Func<Task> action)
    {
        var button=new Button{Content=label,CornerRadius=new CornerRadius(9),Padding=new Thickness(16,9,16,9)};button.Click+=async(_,_)=>{button.IsEnabled=false;try{await action();}catch(Exception ex){Program.Log.Write("ERROR","UI",ex.Message);status.Text=Privacy.Redact(ex.Message);}finally{button.IsEnabled=true;}};return button;
    }
}

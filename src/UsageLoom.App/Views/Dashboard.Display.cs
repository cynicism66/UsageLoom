using Microsoft.UI.Xaml;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? displayTimer;
    private (nint Monitor,Native.Rect Work,uint Dpi,double Raster)? displayState;
    private bool fittingDisplay;
    private long lastContentMinute=-1;
    // DIP above and below the cards (title bar, heading, padding, frame) used by the last fit.
    private double compactChrome=136;

    private void StartDisplayTracking()
    {
        if(!compact)return;
        // Check only display geometry, never content. This also covers work-area
        // changes and removed monitors without handling WinUI's WM_DPICHANGED.
        displayTimer=DispatcherQueue.CreateTimer();displayTimer.Interval=TimeSpan.FromMilliseconds(250);
        displayTimer.Tick+=(_,_)=>
        {
            if(released||!IsPanelVisible||fittingDisplay)return;
            var minute=DateTimeOffset.Now.ToUnixTimeSeconds()/60;
            if(minute!=lastContentMinute){lastContentMinute=minute;if(compactContent is not null)UpdateCompactContent();}
            var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);
            var monitor=Native.MonitorFromWindow(hwnd,2);
            var info=new Native.MonitorInfo{size=(uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>()};
            if(!Native.GetMonitorInfo(monitor,ref info))return;
            var next=(monitor,info.work,Native.GetDpiForWindow(hwnd),(Content as FrameworkElement)?.XamlRoot?.RasterizationScale??1);
            if(displayState is {} previous&&previous.Equals(next))return;
            FitCompactWindow(info,false);
        };
        displayTimer.Start();
        // The heading's real height is known only after the first layout. Correct the
        // window once then, so the Viewbox never shrinks the cards beside an empty strip.
        page.SizeChanged+=(_,_)=>
        {
            if(released||!IsPanelVisible||fittingDisplay||LaidOutCompactChrome() is not {} chrome||Math.Abs(chrome-compactChrome)<.5)return;
            var info=new Native.MonitorInfo{size=(uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>()};
            if(!Native.GetMonitorInfo(Native.MonitorFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this),2),ref info))return;
            // Keep a corner-anchored window anchored (as every open is), so reopening does not move it.
            var work=new WindowBounds(info.work.left,info.work.top,info.work.right-info.work.left,info.work.bottom-info.work.top);
            FitCompactWindow(info,Math.Abs(AppWindow.Position.Y+AppWindow.Size.Height-(work.Y+work.Height-CompactWindowLayout.Inset(work)))<=2);
        };
    }
    private double? LaidOutCompactChrome()
    {
        // Everything in the window except the card area, frame included.
        if(page.ActualHeight<=0||AppWindow.Size.Height<=0)return null;
        var scale=Math.Max(1,Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96d);
        return AppWindow.Size.Height/scale-page.ActualHeight;
    }

    private void FitCompactWindow(Native.MonitorInfo info,bool anchor)
    {
        if(fittingDisplay||released)return;
        fittingDisplay=true;
        try
        {
            capacityInfoFlyout?.Hide();ApplyMinimumWindowWidth();
            var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi=Native.GetDpiForWindow(hwnd);var scale=Math.Max(1,dpi/96d);
            var work=new WindowBounds(info.work.left,info.work.top,info.work.right-info.work.left,info.work.bottom-info.work.top);
            var current=new WindowBounds(AppWindow.Position.X,AppWindow.Position.Y,AppWindow.Size.Width,AppWindow.Size.Height);
            var target=CompactWindowLayout.Fit(work,scale,0,current,anchor);
            var frame=Math.Max(0,AppWindow.Size.Width-AppWindow.ClientSize.Width);
            // The only writer of the natural card width. SizeChanged must not
            // replace it with a stale client width during a DPI transition.
            quotaPanel.Width=Math.Max(280,(target.Width-frame)/scale-32);
            quotaPanel.Measure(new Windows.Foundation.Size(quotaPanel.Width,double.PositiveInfinity));
            // Use the laid-out title bar, heading and padding once known; before the first layout,
            // keep the largest height seen so far. Too little makes the Viewbox shrink the cards.
            compactChrome=LaidOutCompactChrome() is {} laidOut&&double.IsFinite(laidOut)?laidOut:Math.Max(136,compactChrome);
            target=CompactWindowLayout.Fit(work,scale,quotaPanel.DesiredSize.Height,current,anchor,compactChrome);
            if(target!=current)AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(target.X,target.Y,target.Width,target.Height));
            // XAML can settle its scale after the native DPI notification. A
            // later tick will remeasure once if that happened asynchronously.
            displayState=(Native.MonitorFromWindow(hwnd,2),info.work,dpi,(Content as FrameworkElement)?.XamlRoot?.RasterizationScale??1);
        }
        finally{fittingDisplay=false;}
    }
}

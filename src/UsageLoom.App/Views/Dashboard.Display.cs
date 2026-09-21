using Microsoft.UI.Xaml;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? displayTimer;
    private (nint Monitor,Native.Rect Work,uint Dpi,double Raster)? displayState;
    private bool fittingDisplay;

    private void StartDisplayTracking()
    {
        if(!compact)return;
        // Check only display geometry, never content. This also covers work-area
        // changes and removed monitors without handling WinUI's WM_DPICHANGED.
        displayTimer=DispatcherQueue.CreateTimer();displayTimer.Interval=TimeSpan.FromMilliseconds(250);
        displayTimer.Tick+=(_,_)=>
        {
            if(released||!IsPanelVisible||fittingDisplay)return;
            var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);
            var monitor=Native.MonitorFromWindow(hwnd,2);
            var info=new Native.MonitorInfo{size=(uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>()};
            if(!Native.GetMonitorInfo(monitor,ref info))return;
            var next=(monitor,info.work,Native.GetDpiForWindow(hwnd),(Content as FrameworkElement)?.XamlRoot?.RasterizationScale??1);
            if(displayState is {} previous&&previous.Equals(next))return;
            FitCompactWindow(info,false);
        };
        displayTimer.Start();
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
            target=CompactWindowLayout.Fit(work,scale,quotaPanel.DesiredSize.Height,current,anchor);
            if(target!=current)AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(target.X,target.Y,target.Width,target.Height));
            // XAML can settle its scale after the native DPI notification. A
            // later tick will remeasure once if that happened asynchronously.
            displayState=(Native.MonitorFromWindow(hwnd,2),info.work,dpi,(Content as FrameworkElement)?.XamlRoot?.RasterizationScale??1);
        }
        finally{fittingDisplay=false;}
    }
}

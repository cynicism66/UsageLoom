using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using UsageLoom.Core;

namespace UsageLoom.App;

// Experimental, opt-in companion owned by Explorer's primary taskbar. It is
// never injected into Explorer and is hidden unless a free gap is proven.
internal sealed class TaskbarQuotaStrip : IDisposable
{
    private sealed record Anchor(nint Taskbar,WindowBounds? Bounds,TaskbarStripHiddenReason Reason);
    private readonly DispatcherQueue queue;
    private readonly Action openPanel;
    private readonly Native.WindowProc callback;
    private readonly string className="UsageLoom.TaskbarStrip."+Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource stop=new();
    private readonly Task monitor;
    private nint window,owner;
    private WindowBounds? placed;
    private bool visible,disposed,faulted;
    private int? fiveHour,weekly;
    internal bool Faulted=>faulted;
    internal TaskbarStripHiddenReason HiddenReason {get;private set;}=TaskbarStripHiddenReason.MissingElements;

    internal TaskbarQuotaStrip(DispatcherQueue queue,Action openPanel)
    {
        this.queue=queue;this.openPanel=openPanel;callback=OnMessage;
        var cls=new Native.WindowClass{size=(uint)Marshal.SizeOf<Native.WindowClass>(),procedure=callback,
            instance=Native.GetModuleHandle(null),className=className};
        if(Native.RegisterClassEx(ref cls)==0)throw new InvalidOperationException("Taskbar strip window class unavailable");
        monitor=Task.Run(MonitorAsync);
    }
    internal void Update(TrayQuotaPresentation state)
    {
        if(disposed)return;
        static int? Number(TrayQuotaWindow? row)=>row is null?null:(int)Math.Clamp(Math.Round(row.Remaining,MidpointRounding.AwayFromZero),0,100);
        var nextFive=Number(state.FiveHour);var nextWeekly=Number(state.Weekly);
        if(fiveHour==nextFive&&weekly==nextWeekly)return;
        fiveHour=nextFive;weekly=nextWeekly;
        if(window!=0&&visible)Native.InvalidateRect(window,0,false);
    }
    private static WindowBounds Bounds(Native.Rect rect)=>new(rect.left,rect.top,rect.right-rect.left,rect.bottom-rect.top);
    private static bool Intersects(WindowBounds a,WindowBounds b)=>a.X<b.X+b.Width&&b.X<a.X+a.Width&&a.Y<b.Y+b.Height&&b.Y<a.Y+a.Height;
    private static bool OverlapsTaskbarElement(nint taskbar,WindowBounds candidate)
    {
        var overlap=false;
        var visited=false;
        Native.EnumChildWindows(taskbar,(child,_)=>
        {
            visited=true;
            // Container windows can cover the full taskbar; only visible leaf
            // controls are evidence of occupied pixels. Unclear layouts hide.
            if(Native.IsWindowVisible(child)&&Native.FindWindowEx(child,0,null,null)==0)
            {
                if(!Native.GetWindowRect(child,out var rect)||Intersects(candidate,Bounds(rect)))
                {
                    overlap=true;return false;
                }
            }
            return true;
        },0);
        return overlap||!visited;
    }
    private static nint FindTaskButtons(nint taskbar)
    {
        foreach(var cls in new[]{"MSTaskSwWClass","MSTaskListWClass"})
        {
            var direct=Native.FindWindowEx(taskbar,0,cls,null);
            if(direct!=0)return direct;
        }
        var rebar=Native.FindWindowEx(taskbar,0,"ReBarWindow32",null);
        if(rebar!=0)
            foreach(var cls in new[]{"MSTaskSwWClass","MSTaskListWClass"})
            {
                var nested=Native.FindWindowEx(rebar,0,cls,null);
                if(nested!=0)return nested;
            }
        return 0;
    }
    private static Anchor Probe()
    {
        var taskbar=Native.FindWindow("Shell_TrayWnd",null!);
        if(taskbar==0||!Native.GetWindowRect(taskbar,out var taskRect))return new(0,null,TaskbarStripHiddenReason.MissingElements);
        var monitor=Native.MonitorFromWindow(taskbar,2);
        var info=new Native.MonitorInfo{size=(uint)Marshal.SizeOf<Native.MonitorInfo>()};
        if(monitor==0||!Native.GetMonitorInfo(monitor,ref info))return new(taskbar,null,TaskbarStripHiddenReason.MissingElements);
        var appbar=new Native.AppBarData{size=(uint)Marshal.SizeOf<Native.AppBarData>()};
        var autoHide=(Native.SHAppBarMessage(4,ref appbar)&1)!=0;
        var notification=Native.FindWindowEx(taskbar,0,"TrayNotifyWnd",null);
        var buttons=FindTaskButtons(taskbar);
        WindowBounds? notifyBounds=notification!=0&&Native.GetWindowRect(notification,out var notifyRect)?Bounds(notifyRect):null;
        WindowBounds? buttonBounds=buttons!=0&&Native.GetWindowRect(buttons,out var buttonRect)?Bounds(buttonRect):null;
        var foreground=Native.GetForegroundWindow();
        var fullscreen=false;
        if(foreground!=0&&foreground!=taskbar&&Native.IsWindowVisible(foreground)&&
            Native.GetWindowThreadProcessId(foreground,out var pid)!=0&&pid!=Environment.ProcessId&&
            Native.GetWindowRect(foreground,out var frontRect))
        {
            var outer=info.monitor;
            fullscreen=frontRect.left<=outer.left+1&&frontRect.top<=outer.top+1&&
                frontRect.right>=outer.right-1&&frontRect.bottom>=outer.bottom-1;
        }
        var result=TaskbarStripLayout.Resolve(Bounds(info.monitor),Bounds(taskRect),notifyBounds,buttonBounds,
            Native.GetDpiForWindow(taskbar),autoHide,(info.flags&1)!=0,fullscreen);
        if(result.Bounds is {} candidate&&OverlapsTaskbarElement(taskbar,candidate))
            return new(taskbar,null,TaskbarStripHiddenReason.NoSafeSpace);
        return new(taskbar,result.Bounds,result.HiddenReason);
    }
    private async Task MonitorAsync()
    {
        Anchor? last=null;
        try
        {
            while(!stop.IsCancellationRequested)
            {
                var next=Probe();
                if(next!=last||(next.Bounds is not null&&(window==0||!Native.IsWindow(window))))
                {
                    last=next;
                    queue.TryEnqueue(()=>Apply(next));
                }
                await Task.Delay(TimeSpan.FromSeconds(2),stop.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){queue.TryEnqueue(()=>Fail(ex));}
    }
    private void Apply(Anchor next)
    {
        if(disposed)return;
        try
        {
            if(window!=0&&!Native.IsWindow(window))
            {
                window=0;owner=0;placed=null;visible=false;
            }
            HiddenReason=next.Reason;
            if(next.Taskbar!=owner)
            {
                if(window!=0)Native.DestroyWindow(window);
                window=0;owner=next.Taskbar;placed=null;visible=false;
            }
            if(next.Bounds is not {} bounds)
            {
                if(window!=0&&visible){Native.ShowWindow(window,0);visible=false;placed=null;}
                return;
            }
            if(window==0)
            {
                // An owned, no-activate tool popup stays out of Alt+Tab and the
                // taskbar button list. Explorer owns only its z-order, not data.
                window=Native.CreateWindowEx(0x08000080,className,"UsageLoom quota",0x80000000,
                    bounds.X,bounds.Y,bounds.Width,bounds.Height,owner,0,Native.GetModuleHandle(null),0);
                if(window==0)throw new InvalidOperationException("Taskbar strip window unavailable");
            }
            if(!visible||placed!=bounds)
            {
                var flags=visible?0x0014u:0x0050u; // NOZORDER|NOACTIVATE or SHOWWINDOW|NOACTIVATE
                if(!Native.SetWindowPos(window,visible?0:(nint)(-1),bounds.X,bounds.Y,bounds.Width,bounds.Height,flags))
                    throw new InvalidOperationException("Taskbar strip position unavailable");
                visible=true;placed=bounds;Native.InvalidateRect(window,0,false);
            }
        }
        catch(Exception ex){Fail(ex);}
    }
    private static uint Color(TrayIconTier tier)=>tier switch
    {
        TrayIconTier.Red=>0x5751E8,
        TrayIconTier.Orange=>0x37A3F5,
        TrayIconTier.Unavailable=>0x938984,
        _=>0xF58AA3
    };
    private void Paint(nint dc,Native.Rect client)
    {
        var background=Native.CreateSolidBrush(0x302A26);
        if(background==0)throw new InvalidOperationException("Taskbar strip paint brush unavailable");
        try{Native.FillRect(dc,ref client,background);}
        finally{Native.DeleteObject(background);}
        Native.SetBkMode(dc,1);
        var height=client.bottom-client.top;var width=client.right-client.left;
        var circle=Math.Max(16,Math.Min(height-8,(int)(height*.72)));
        var cx=width-circle/2-6;var cy=height/2;
        var bigArea=new Native.Rect{left=4,top=1,right=width-circle-3,bottom=height-1};
        var bigFont=Native.CreateFont(-(int)(height*.62),0,0,0,700,0,0,0,1,0,0,5,0,"Segoe UI");
        if(bigFont==0)throw new InvalidOperationException("Taskbar strip font unavailable");
        var oldFont=Native.SelectObject(dc,bigFont);
        try
        {
            Native.SetTextColor(dc,Color(TrayIconRaster.Tier(fiveHour)));
            var text=fiveHour?.ToString()??"--";
            Native.DrawText(dc,text,text.Length,ref bigArea,0x25);
        }
        finally{Native.SelectObject(dc,oldFont);Native.DeleteObject(bigFont);}
        var oldBrush=Native.SelectObject(dc,Native.GetStockObject(5)); // NULL_BRUSH
        var basePen=Native.CreatePen(0,Math.Max(2,height/18),0x534843);
        if(basePen==0){Native.SelectObject(dc,oldBrush);throw new InvalidOperationException("Taskbar strip ring pen unavailable");}
        var oldPen=Native.SelectObject(dc,basePen);
        try
        {
            var radius=circle/2-2;
            Native.Ellipse(dc,cx-radius,cy-radius,cx+radius,cy+radius);
            if(weekly is {} amount&&amount>0)
            {
                var activePen=Native.CreatePen(0,Math.Max(2,height/18),Color(TrayIconRaster.Tier(amount)));
                if(activePen==0)throw new InvalidOperationException("Taskbar strip active pen unavailable");
                Native.SelectObject(dc,activePen);
                try{Native.MoveToEx(dc,cx,cy-radius,0);Native.AngleArc(dc,cx,cy,(uint)radius,90,-360f*amount/100);}
                finally{Native.SelectObject(dc,basePen);Native.DeleteObject(activePen);}
            }
        }
        finally{Native.SelectObject(dc,oldPen);Native.SelectObject(dc,oldBrush);Native.DeleteObject(basePen);}
        var smallFont=Native.CreateFont(-(int)(height*.27),0,0,0,600,0,0,0,1,0,0,5,0,"Segoe UI");
        if(smallFont==0)throw new InvalidOperationException("Taskbar strip small font unavailable");
        oldFont=Native.SelectObject(dc,smallFont);
        try
        {
            Native.SetTextColor(dc,weekly is null?Color(TrayIconTier.Unavailable):0xFAF6F5);
            var text=weekly?.ToString()??"–";
            var smallArea=new Native.Rect{left=cx-circle/2,top=cy-circle/2,right=cx+circle/2,bottom=cy+circle/2};
            Native.DrawText(dc,text,text.Length,ref smallArea,0x25);
        }
        finally{Native.SelectObject(dc,oldFont);Native.DeleteObject(smallFont);}
    }
    private nint OnMessage(nint hwnd,uint message,nuint wParam,nint lParam)
    {
        try
        {
            if(message==0x0014)return 1; // WM_ERASEBKGND: redraw in WM_PAINT
            if(message==0x0202){openPanel();return 0;}
            if(message==0x000F)
            {
                var dc=Native.BeginPaint(hwnd,out var paint);
                try{if(dc!=0&&Native.GetClientRect(hwnd,out var client))Paint(dc,client);}
                finally{Native.EndPaint(hwnd,ref paint);}
                return 0;
            }
        }
        catch(Exception ex){Fail(ex);return 0;}
        return Native.DefWindowProc(hwnd,message,wParam,lParam);
    }
    private void Fail(Exception ex)
    {
        if(faulted||disposed)return;
        faulted=true;Program.Log.Write("ERROR","TaskbarStrip",ex.Message);
        if(window!=0)Native.ShowWindow(window,0);
        queue.TryEnqueue(Dispose);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;stop.Cancel();
        if(window!=0){Native.DestroyWindow(window);window=0;}
        Native.UnregisterClass(className,Native.GetModuleHandle(null));
        _=monitor.ContinueWith(_=>stop.Dispose(),TaskScheduler.Default);
    }
}

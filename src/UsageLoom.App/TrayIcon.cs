using System.Runtime.InteropServices;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed class TrayIcon : IDisposable
{
    private readonly Native.WindowProc callback;
    private readonly Action show, details, settings, refresh, exit;
    private readonly nint hwnd, fixedIcon;
    private nint currentIcon;
    private (int Size,int? Remaining)? currentVisual;
    private TrayQuotaPresentation presentation=new("none",null,null);
    private bool useFixedIcon;
    private string currentTip="";
    private readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private Native.NotifyData data;
    private bool disposed;
    public TrayIcon(Action show, Action details, Action settings, Action refresh, Action exit)
    {
        (this.show, this.details, this.settings, this.refresh, this.exit) = (show, details, settings, refresh, exit);
        callback = OnMessage;
        var cls = new Native.WindowClass { size = (uint)Marshal.SizeOf<Native.WindowClass>(), procedure = callback, instance = Native.GetModuleHandle(null), className = "UsageLoom.TrayHost" };
        if (Native.RegisterClassEx(ref cls) == 0) throw new InvalidOperationException(L10n.T("sFEFC45E665BA"));
        hwnd = Native.CreateWindowEx(0, cls.className, "UsageLoom", 0, 0, 0, 0, 0, 0, 0, cls.instance, 0);
        if (hwnd == 0) throw new InvalidOperationException(L10n.T("s2FCDBA60F66B"));
        var pixels = new byte[32 * 32 * 4];
        for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
        {
            var offset = (y * 32 + x) * 4;
            var white = x is >= 8 and <= 12 && y is >= 7 and <= 24 || y is >= 20 and <= 24 && x is >= 8 and <= 24;
            pixels[offset] = white ? (byte)255 : (byte)246; pixels[offset + 1] = white ? (byte)255 : (byte)82;
            pixels[offset + 2] = white ? (byte)255 : (byte)120; pixels[offset + 3] = 255;
        }
        fixedIcon = Native.LoadImage(0,Path.Combine(AppContext.BaseDirectory,"UsageLoom.ico"),1,32,32,0x10);
        if(fixedIcon==0)fixedIcon = Native.CreateIcon(0, 32, 32, 1, 32, new byte[128], pixels);
        currentIcon=fixedIcon;currentTip=L10n.T("sD3ECED23109C");
        data = new Native.NotifyData { size = (uint)Marshal.SizeOf<Native.NotifyData>(), window = hwnd, id = 1, flags = 7, callback = 0x8001, icon = currentIcon, tip = currentTip, info = "", title = "" };
        Add();
    }
    private static nint CreateRenderedIcon(TrayIconPixels pixels)
    {
        var bitmap=new Native.BitmapInfo{size=(uint)Marshal.SizeOf<Native.BitmapInfo>(),width=pixels.Size,height=-pixels.Size,
            planes=1,bits=32,compression=0,imageSize=(uint)pixels.Bgra.Length};
        var color=Native.CreateDIBSection(0,ref bitmap,0,out var bits,0,0);
        if(color==0||bits==0){if(color!=0)Native.DeleteObject(color);return 0;}
        nint mask=0;
        try
        {
            Marshal.Copy(pixels.Bgra,0,bits,pixels.Bgra.Length);
            mask=Native.CreateBitmap(pixels.Size,pixels.Size,1,1,pixels.Mask);
            if(mask==0)return 0;
            var info=new Native.IconInfo{isIcon=true,color=color,mask=mask};
            return Native.CreateIconIndirect(ref info);
        }
        finally
        {
            if(mask!=0)Native.DeleteObject(mask);
            Native.DeleteObject(color);
        }
    }
    internal static string Tooltip(TrayQuotaPresentation value)
    {
        string Window(TrayQuotaWindow? window)=>window is null?"--":$"{window.Remaining:0.#}% {L10n.T("tray.reset")} "+
            (window.Reset is {} at?$"{at.ToLocalTime():MM-dd HH:mm}":"--")+
            (window.Pace==QuotaPaceKind.Unavailable?"":" · "+L10n.T(window.Pace switch{QuotaPaceKind.Fast=>"tray.pace.fast",QuotaPaceKind.Exhausted=>"tray.pace.exhausted",_=>"tray.pace.normal"}));
        var source=value.Source switch{"codex"=>"Codex","claude"=>"Claude",_=>L10n.T("tray.noSource")};
        var tip=$"{source} · {L10n.T("tray.snapshotNote")} · {L10n.T("tray.fiveHour")} {Window(value.FiveHour)} · {L10n.T("tray.weekly")} {Window(value.Weekly)}";
        return tip[..Math.Min(tip.Length,127)];
    }
    public void Update(TrayQuotaPresentation next,bool fixedMode)
    {
        if(disposed)return;
        var wasFixed=useFixedIcon;presentation=next;useFixedIcon=fixedMode;
        var size=TrayIconRaster.SizeForDpi(Native.GetDpiForWindow(hwnd));
        var visual=(Size:size,Remaining:fixedMode?(int?)null:next.DisplayRemaining);
        var tip=Tooltip(next);
        if(wasFixed==fixedMode&&currentVisual==visual&&currentTip==tip)return;
        var old=currentIcon;
        var icon=fixedMode?fixedIcon:!wasFixed&&currentVisual==visual&&currentIcon!=fixedIcon?currentIcon:CreateRenderedIcon(TrayIconRaster.Render(size,next.DisplayRemaining));
        if(icon==0){Program.Log.Write("WARN","Tray","动态图标生成失败，使用固定图标");icon=fixedIcon;}
        currentIcon=icon;data.icon=icon;data.tip=tip;data.flags=7;
        if(!Native.Shell_NotifyIcon(1,ref data))Program.Log.Write("WARN","Tray","图标更新失败，等待 Explorer 恢复");
        if(old!=0&&old!=fixedIcon&&old!=currentIcon)Native.DestroyIcon(old);
        currentVisual=icon==fixedIcon&&!fixedMode?null:visual;currentTip=tip;
    }
    private void Add() { if (!Native.Shell_NotifyIcon(0, ref data)) Program.Log.Write("WARN", "Tray", "图标添加失败，等待 Explorer 恢复"); else Program.Log.Write("INFO", "Tray", "托盘图标已创建"); }
    public void Notify(string title, string body)
    {
        if (disposed) return;
        data.flags = 0x10; data.title = title[..Math.Min(title.Length, 63)]; data.info = body[..Math.Min(body.Length, 255)]; data.infoFlags = 1; data.timeout = 10000;
        Native.Shell_NotifyIcon(1, ref data); data.flags = 7; data.info = ""; data.title = "";
    }
    private nint OnMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == taskbarCreated) Add();
            else if(message is 0x02E0 or 0x007E)Update(presentation,useFixedIcon);
            else if (message == Native.ActivateMessage) details();
            else if (message == 0x8001)
            {
                if ((uint)lParam == 0x0202) show();
                else if ((uint)lParam == 0x0205) Menu();
                else if ((uint)lParam == 0x0405) show();
            }
        }
        catch (Exception ex) { Program.Log.Write("ERROR", "Tray", ex.Message); }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }
    private void Menu()
    {
        var menu = Native.CreatePopupMenu();
        try
        {
            Native.AppendMenu(menu, 0, 1, L10n.T("s6ED9D8BDE086")); Native.AppendMenu(menu, 0, 2, L10n.T("tray.statistics")); Native.AppendMenu(menu, 0, 5, L10n.T("sDF3D58C7D84B")); Native.AppendMenu(menu, 0, 3, L10n.T("s130123E01753")); Native.AppendMenu(menu, 0x800, 0, ""); Native.AppendMenu(menu, 0, 4, L10n.T("s1CA553FD86AE"));
            Native.GetCursorPos(out var p); Native.SetForegroundWindow(hwnd);
            switch (Native.TrackPopupMenu(menu, 0x100 | 0x2, p.x, p.y, 0, hwnd, 0)) { case 1: show(); break; case 2: details(); break; case 5: settings(); break; case 3: refresh(); break; case 4: exit(); break; }
            Native.PostMessage(hwnd, 0, 0, 0);
        }
        finally { Native.DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Native.Shell_NotifyIcon(2, ref data); Native.DestroyWindow(hwnd);
        if(currentIcon!=0&&currentIcon!=fixedIcon)Native.DestroyIcon(currentIcon);
        if(fixedIcon!=0)Native.DestroyIcon(fixedIcon);
        Native.UnregisterClass("UsageLoom.TrayHost", Native.GetModuleHandle(null));
    }
}

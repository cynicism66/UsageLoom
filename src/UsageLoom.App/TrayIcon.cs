using System.Runtime.InteropServices;

namespace UsageLoom.App;

internal sealed class TrayIcon : IDisposable
{
    private readonly Native.WindowProc callback;
    private readonly Action show, details, refresh, exit;
    private readonly nint hwnd, icon;
    private readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private Native.NotifyData data;
    private bool disposed;
    public TrayIcon(Action show, Action details, Action refresh, Action exit)
    {
        (this.show, this.details, this.refresh, this.exit) = (show, details, refresh, exit);
        callback = OnMessage;
        var cls = new Native.WindowClass { size = (uint)Marshal.SizeOf<Native.WindowClass>(), procedure = callback, instance = Native.GetModuleHandle(null), className = "UsageLoom.TrayHost" };
        if (Native.RegisterClassEx(ref cls) == 0) throw new InvalidOperationException("托盘窗口类注册失败");
        hwnd = Native.CreateWindowEx(0, cls.className, "UsageLoom", 0, 0, 0, 0, 0, 0, 0, cls.instance, 0);
        if (hwnd == 0) throw new InvalidOperationException("托盘窗口创建失败");
        var pixels = new byte[32 * 32 * 4];
        for (var y = 0; y < 32; y++) for (var x = 0; x < 32; x++)
        {
            var offset = (y * 32 + x) * 4;
            var white = x is >= 8 and <= 12 && y is >= 7 and <= 24 || y is >= 20 and <= 24 && x is >= 8 and <= 24;
            pixels[offset] = white ? (byte)255 : (byte)246; pixels[offset + 1] = white ? (byte)255 : (byte)82;
            pixels[offset + 2] = white ? (byte)255 : (byte)120; pixels[offset + 3] = 255;
        }
        icon = Native.LoadImage(0,Path.Combine(AppContext.BaseDirectory,"UsageLoom.ico"),1,32,32,0x10);
        if(icon==0)icon = Native.CreateIcon(0, 32, 32, 1, 32, new byte[128], pixels);
        data = new Native.NotifyData { size = (uint)Marshal.SizeOf<Native.NotifyData>(), window = hwnd, id = 1, flags = 7, callback = 0x8001, icon = icon, tip = "UsageLoom · 本地用量与额度", info = "", title = "" };
        Add();
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
            Native.AppendMenu(menu, 0, 1, "额度面板"); Native.AppendMenu(menu, 0, 2, "详细统计与设置"); Native.AppendMenu(menu, 0, 3, "立即刷新"); Native.AppendMenu(menu, 0x800, 0, ""); Native.AppendMenu(menu, 0, 4, "退出 UsageLoom");
            Native.GetCursorPos(out var p); Native.SetForegroundWindow(hwnd);
            switch (Native.TrackPopupMenu(menu, 0x100 | 0x2, p.x, p.y, 0, hwnd, 0)) { case 1: show(); break; case 2: details(); break; case 3: refresh(); break; case 4: exit(); break; }
            Native.PostMessage(hwnd, 0, 0, 0);
        }
        finally { Native.DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Native.Shell_NotifyIcon(2, ref data); Native.DestroyWindow(hwnd); Native.DestroyIcon(icon); Native.UnregisterClass("UsageLoom.TrayHost", Native.GetModuleHandle(null));
    }
}

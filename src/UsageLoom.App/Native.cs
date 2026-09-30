using System.Runtime.InteropServices;

namespace UsageLoom.App;

internal static class Native
{
    public const uint ActivateMessage = 0x8002;
    public delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    public delegate bool EnumWindowProc(nint hwnd,nint parameter);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClass { public uint size, style; public WindowProc procedure; public int classExtra, windowExtra; public nint instance, icon, cursor, background; public string? menu; public string className; public nint smallIcon; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyData
    {
        public uint size; public nint window; public uint id, flags, callback; public nint icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string tip;
        public uint state, stateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string info;
        public uint timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string title;
        public uint infoFlags; public Guid guid; public nint balloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public uint size; public Rect monitor, work; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] public struct BitmapInfo
    {
        public uint size; public int width,height; public ushort planes,bits; public uint compression,imageSize;
        public int xPixelsPerMeter,yPixelsPerMeter; public uint colorsUsed,importantColors;
    }
    [StructLayout(LayoutKind.Sequential)] public struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool isIcon;
        public uint xHotspot,yHotspot; public nint mask,color;
    }
    [StructLayout(LayoutKind.Sequential)] public struct AppBarData
    {
        public uint size; public nint window; public uint callback,edge; public Rect bounds; public nint parameter;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PaintData
    {
        public nint dc; public int erase; public Rect paint; public int restore,update;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] reserved;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] public static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string text);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool Shell_NotifyIcon(uint action, ref NotifyData data);
    [DllImport("user32.dll")] public static extern nint CreateIcon(nint instance, int width, int height, byte planes, byte bits, byte[] andBits, byte[] xorBits);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] public static extern nint CreateIconIndirect(ref IconInfo info);
    [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll")] public static extern nint CreateBitmap(int width,int height,uint planes,uint bits,byte[] data);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint value);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern nint LoadImage(nint instance,string name,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] public static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string cls, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindowEx(nint parent,nint after,string? cls,string? name);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(nint parent,EnumWindowProc callback,nint parameter);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd,out Rect bounds);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd,out Rect bounds);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(nint hwnd,nint rect,bool erase);
    [DllImport("user32.dll")] public static extern nint BeginPaint(nint hwnd,out PaintData data);
    [DllImport("user32.dll")] public static extern bool EndPaint(nint hwnd,ref PaintData data);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("shell32.dll")] public static extern nuint SHAppBarMessage(uint message,ref AppBarData data);
    [DllImport("gdi32.dll")] public static extern nint CreateSolidBrush(uint color);
    [DllImport("user32.dll")] public static extern int FillRect(nint dc,ref Rect rect,nint brush);
    [DllImport("gdi32.dll")] public static extern nint CreatePen(int style,int width,uint color);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc,nint value);
    [DllImport("gdi32.dll")] public static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll")] public static extern bool Ellipse(nint dc,int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] public static extern bool AngleArc(nint dc,int x,int y,uint radius,float start,float sweep);
    [DllImport("gdi32.dll")] public static extern bool MoveToEx(nint dc,int x,int y,nint previous);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] public static extern nint CreateFont(int height,int width,int escapement,int orientation,int weight,uint italic,uint underline,uint strikeout,uint charset,uint outputPrecision,uint clipPrecision,uint quality,uint pitchAndFamily,string face);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(nint dc,int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(nint dc,uint color);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int DrawText(nint dc,string text,int length,ref Rect rect,uint format);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
}

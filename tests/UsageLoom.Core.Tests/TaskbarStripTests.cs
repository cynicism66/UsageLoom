using UsageLoom.Core;

static class TaskbarStripTests
{
    private static void Check(bool value){if(!value)throw new Exception("Taskbar strip assertion");}
    internal static void Run(Action<string,Action> test)
    {
        var monitor=new WindowBounds(0,0,1920,1080);
        var bottom=new WindowBounds(0,1032,1920,48);
        var notify=new WindowBounds(1710,1032,210,48);
        var buttons=new WindowBounds(400,1032,1000,48);
        test("数字条：底部任务栏空档与 DPI 尺寸",() =>
        {
            var normal=TaskbarStripLayout.Resolve(monitor,bottom,notify,buttons,96,false,true,false);
            var scaled=TaskbarStripLayout.Resolve(monitor,bottom,notify,buttons,144,false,true,false);
            Check(normal.Bounds is not null&&scaled.Bounds is not null);
            var a=normal.Bounds!.Value;var b=scaled.Bounds!.Value;
            Check(a.X>=buttons.X+buttons.Width+6&&a.X+a.Width<notify.X);
            Check(b.Width>a.Width&&b.X+b.Width<notify.X);
        });
        test("数字条：方向、自动隐藏、副屏、全屏和占用重叠时保守隐藏",() =>
        {
            Check(TaskbarStripLayout.Resolve(monitor,new(0,0,1920,48),notify,buttons,96,false,true,false).HiddenReason==TaskbarStripHiddenReason.UnsupportedPosition);
            Check(TaskbarStripLayout.Resolve(monitor,new(0,0,48,1080),notify,buttons,96,false,true,false).HiddenReason==TaskbarStripHiddenReason.UnsupportedPosition);
            Check(TaskbarStripLayout.Resolve(monitor,bottom,notify,buttons,96,true,true,false).HiddenReason==TaskbarStripHiddenReason.AutoHide);
            Check(TaskbarStripLayout.Resolve(monitor,bottom,notify,buttons,96,false,false,false).HiddenReason==TaskbarStripHiddenReason.SecondaryTaskbar);
            Check(TaskbarStripLayout.Resolve(monitor,bottom,notify,buttons,96,false,true,true).HiddenReason==TaskbarStripHiddenReason.Fullscreen);
            Check(TaskbarStripLayout.Resolve(monitor,bottom,null,buttons,96,false,true,false).HiddenReason==TaskbarStripHiddenReason.MissingElements);
            Check(TaskbarStripLayout.Resolve(monitor,bottom,notify,new(400,1032,1260,48),96,false,true,false).HiddenReason==TaskbarStripHiddenReason.NoSafeSpace);
        });
    }
}

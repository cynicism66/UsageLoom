namespace UsageLoom.Core;

public enum TaskbarStripHiddenReason
{
    None,UnsupportedPosition,AutoHide,SecondaryTaskbar,Fullscreen,MissingElements,NoSafeSpace
}
public sealed record TaskbarStripPlacement(WindowBounds? Bounds,TaskbarStripHiddenReason HiddenReason)
{
    public bool Visible=>Bounds is not null;
}

// Physical-pixel placement. Do not guess whether a gap is free: both the
// notification area and task-button boundary must be observable.
public static class TaskbarStripLayout
{
    public static TaskbarStripPlacement Resolve(WindowBounds monitor,WindowBounds taskbar,WindowBounds? notification,
        WindowBounds? taskButtons,uint dpi,bool autoHide,bool primary,bool fullscreen)
    {
        if(autoHide)return new(null,TaskbarStripHiddenReason.AutoHide);
        if(!primary)return new(null,TaskbarStripHiddenReason.SecondaryTaskbar);
        if(fullscreen)return new(null,TaskbarStripHiddenReason.Fullscreen);
        if(taskbar.Width<=taskbar.Height||Math.Abs(taskbar.Y+taskbar.Height-(monitor.Y+monitor.Height))>2||
            taskbar.X<monitor.X-2||taskbar.X+taskbar.Width>monitor.X+monitor.Width+2)
            return new(null,TaskbarStripHiddenReason.UnsupportedPosition);
        if(notification is not {} notify||taskButtons is not {} buttons||notify.Width<=0||buttons.Width<=0)
            return new(null,TaskbarStripHiddenReason.MissingElements);
        var scale=Math.Clamp((dpi==0?96:dpi)/96d,1,2.5);
        var gap=(int)Math.Ceiling(6*scale);
        var width=(int)Math.Ceiling(86*scale);
        var height=Math.Min(taskbar.Height-4,(int)Math.Ceiling(34*scale));
        var x=notify.X-gap-width;
        var y=taskbar.Y+(taskbar.Height-height)/2;
        if(height<20||notify.X<taskbar.X||notify.X+notify.Width>taskbar.X+taskbar.Width||
            x<taskbar.X+gap||x<buttons.X+buttons.Width+gap||y<taskbar.Y||y+height>taskbar.Y+taskbar.Height)
            return new(null,TaskbarStripHiddenReason.NoSafeSpace);
        return new(new WindowBounds(x,y,width,height),TaskbarStripHiddenReason.None);
    }
}

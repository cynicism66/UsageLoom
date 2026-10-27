namespace UsageLoom.Core;

// Screen coordinates and window bounds are physical pixels; content is DIP.
public readonly record struct WindowBounds(int X,int Y,int Width,int Height);
public static class CompactWindowLayout
{
    // Gap between the window and the work-area edges it is anchored to.
    public static int Inset(WindowBounds work)=>Math.Min(12,Math.Max(0,(Math.Min(work.Width,work.Height)-1)/2));
    // chrome: DIP above and below the cards (title bar, heading, padding, frame).
    public static WindowBounds Fit(WindowBounds work,double scale,double contentHeight,WindowBounds current,bool anchor,double chrome=136)
    {
        scale=double.IsFinite(scale)?Math.Clamp(scale,1,8):1;
        var inset=Inset(work);
        var width=Math.Min((int)Math.Ceiling(380*scale),Math.Max(1,work.Width-inset*2));
        var height=Math.Min((int)Math.Ceiling((Math.Max(0,contentHeight)+(double.IsFinite(chrome)?Math.Max(0,chrome):136))*scale),Math.Max(1,work.Height-inset*2));
        var right=work.X+work.Width-inset-width;var bottom=work.Y+work.Height-inset-height;
        return new(anchor?right:Math.Clamp(current.X,work.X+inset,right),
            anchor?bottom:Math.Clamp(current.Y,work.Y+inset,bottom),width,height);
    }
}

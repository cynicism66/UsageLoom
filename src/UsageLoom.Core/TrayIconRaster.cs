namespace UsageLoom.Core;

public enum TrayIconTier { Unavailable,Normal,Orange,Red }
public sealed record TrayIconPixels(int Size,byte[] Bgra,byte[] Mask);

// Platform-independent bitmap parameters. The Win32 host owns and destroys HICONs.
public static class TrayIconRaster
{
    private static readonly string[][] Digits=
    [
        ["111","101","101","101","111"], ["010","110","010","010","111"],
        ["111","001","111","100","111"], ["111","001","111","001","111"],
        ["101","101","111","001","001"], ["111","100","111","001","111"],
        ["111","100","111","101","111"], ["111","001","001","001","001"],
        ["111","101","111","101","111"], ["111","101","111","001","111"]
    ];
    public static int SizeForDpi(uint dpi)
    {
        var desired=16*Math.Clamp(dpi==0?96:dpi,96u,192u)/96d;
        return new[]{16,20,24,32}.MinBy(size=>Math.Abs(size-desired));
    }
    public static TrayIconTier Tier(int? remaining)=>remaining switch
    {
        null=>TrayIconTier.Unavailable,
        <=10=>TrayIconTier.Red,
        <=20=>TrayIconTier.Orange,
        _=>TrayIconTier.Normal
    };
    public static TrayIconPixels Render(int size,int? remaining)
    {
        if(size is not (16 or 20 or 24 or 32))throw new ArgumentOutOfRangeException(nameof(size));
        if(remaining is <0 or >100)throw new ArgumentOutOfRangeException(nameof(remaining));
        var pixels=new byte[size*size*4];var stride=((size+15)/16)*2;var mask=new byte[stride*size];
        var color=Tier(remaining) switch
        {
            TrayIconTier.Red=>(R:(byte)232,G:(byte)81,B:(byte)87),
            TrayIconTier.Orange=>(R:(byte)245,G:(byte)163,B:(byte)55),
            TrayIconTier.Unavailable=>(R:(byte)132,G:(byte)137,B:(byte)147),
            _=>(R:(byte)163,G:(byte)138,B:(byte)245)
        };
        void Pixel(int x,int y,byte r,byte g,byte b,byte alpha)
        {
            if(x<0||x>=size||y<0||y>=size)return;
            var offset=(y*size+x)*4;
            pixels[offset]=b;pixels[offset+1]=g;pixels[offset+2]=r;pixels[offset+3]=alpha;
        }
        var center=size/2d;var outer=size*.46;var inner=size*.34;
        for(var y=0;y<size;y++)for(var x=0;x<size;x++)
        {
            var dx=x+.5-center;var dy=y+.5-center;var distance=Math.Sqrt(dx*dx+dy*dy);
            var coverage=Math.Clamp(Math.Min(outer+.5-distance,distance-inner+.5),0,1);
            if(coverage<=0)continue;
            var angle=(Math.Atan2(dy,dx)+Math.PI/2+Math.PI*2)%(Math.PI*2);
            var lit=remaining is null||angle/(Math.PI*2)<=remaining.Value/100d;
            var shade=lit?color:(R:(byte)67,G:(byte)72,B:(byte)83);
            Pixel(x,y,shade.R,shade.G,shade.B,(byte)Math.Round(coverage*255));
        }
        if(remaining is {} number)
        {
            var text=number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var scale=size>=24?2:1;var width=(text.Length*3+text.Length-1)*scale;
            var left=(size-width)/2;var top=(size-5*scale)/2;
            for(var index=0;index<text.Length;index++)
                for(var row=0;row<5;row++)for(var col=0;col<3;col++)
                    if(Digits[text[index]-'0'][row][col]=='1')
                        for(var yy=0;yy<scale;yy++)for(var xx=0;xx<scale;xx++)
                            Pixel(left+(index*4+col)*scale+xx,top+row*scale+yy,245,246,250,255);
        }
        for(var y=0;y<size;y++)for(var x=0;x<size;x++)
            if(pixels[(y*size+x)*4+3]==0)mask[y*stride+x/8]|=(byte)(0x80>>(x%8));
        return new(size,pixels,mask);
    }
}

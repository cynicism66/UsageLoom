using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using UsageLoom.Core;
using Windows.Foundation;

namespace UsageLoom.App;

internal static class UsageCharts
{
    private enum TrendMetric { Tokens, Requests, Cost }
    internal static readonly Windows.UI.Color[] Colorset=[ColorHelper.FromArgb(255,163,138,245),ColorHelper.FromArgb(255,199,181,255),ColorHelper.FromArgb(255,116,134,215),ColorHelper.FromArgb(255,101,179,184),ColorHelper.FromArgb(255,184,151,201),ColorHelper.FromArgb(255,130,139,157)];
    internal static FrameworkElement Trend(IReadOnlyList<HistoryTrendBucket> buckets,Action<DateOnly,DateOnly> select,bool hourly=false)
    {
        const double height=286,left=36,right=18,top=34,bottom=58;
        var root=new StackPanel{Spacing=12};var header=new Grid{ColumnSpacing=16,RowSpacing=8};
        header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        header.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});header.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        header.Children.Add(new TextBlock{Text=L10n.T("s1BB33A9E6313"),FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        var headerRight=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Right};Grid.SetColumn(headerRight,1);header.Children.Add(headerRight);
        var totalText=new TextBlock{FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,Foreground=new SolidColorBrush(Colorset[0]),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,4,0)};headerRight.Children.Add(totalText);
        var buttons=new Dictionary<TrendMetric,Button>();
        foreach(var (metric,label) in new[]{(TrendMetric.Tokens,L10n.T("s9638021CAEE5")),(TrendMetric.Requests,L10n.T("s855754C132F3")),(TrendMetric.Cost,L10n.T("sB8D69B30E00B"))})
        {
            var button=new Button{Content=label,Padding=new Thickness(11,6,11,6),CornerRadius=new CornerRadius(8),FontSize=12};
            buttons[metric]=button;headerRight.Children.Add(button);
        }
        var caption=new TextBlock{Text=(hourly?L10n.T("s3A3C6EC37497"):L10n.T("s18A912CFCF51"))+L10n.T("s94186ECB2771"),FontSize=11,Opacity=.62,TextWrapping=TextWrapping.Wrap};Grid.SetRow(caption,1);Grid.SetColumnSpan(caption,2);header.Children.Add(caption);
        root.Children.Add(header);
        var chartHost=new Grid{Height=292,MinWidth=0};
        var canvas=new Canvas{Height=height,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,Background=new SolidColorBrush(Colors.Transparent)};chartHost.Children.Add(canvas);root.Children.Add(chartHost);
        if(buckets.Count==0)
        {
            chartHost.Children.Clear();chartHost.Children.Add(new TextBlock{Text=L10n.T("s6518A3BADD61"),FontSize=15,Opacity=.65,TextAlignment=TextAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
            totalText.Text="—";foreach(var button in buttons.Values)button.IsEnabled=false;return root;
        }
        var metricValue=TrendMetric.Tokens;
        static string FormatNumber(double value)=>UsageNumbers.Compact(value);
        static string FormatCost(double value)=>value<=0?"$0":value<.01?$"${value:0.0000}":$"${value:0.##}";
        string Format(double value)=>metricValue switch{TrendMetric.Requests=>$"{value:0}",TrendMetric.Cost=>FormatCost(value),_=>FormatNumber(value)};
        double Value(HistoryTrendBucket bucket)=>metricValue switch{TrendMetric.Requests=>bucket.Requests,TrendMetric.Cost=>(double)bucket.EstimatedCost,_=>bucket.Tokens};
        string Exact(HistoryTrendBucket bucket)=>metricValue switch{TrendMetric.Requests=>L10n.F("s6E078A45FC35", bucket.Requests),TrendMetric.Cost=>$"${bucket.EstimatedCost:N4}",_=>$"{bucket.Tokens:N0} Token"};
        var accent=new SolidColorBrush(Colorset[0]);var gridBrush=new SolidColorBrush(ColorHelper.FromArgb(46,127,132,150));var baselineBrush=new SolidColorBrush(ColorHelper.FromArgb(86,127,132,150));
        var currentWidth=960d;var plotWidth=currentWidth-left-right;var plotHeight=height-top-bottom;Point[] currentPoints=[];Line? indicator=null;Border? tooltip=null;TextBlock? tooltipText=null;
        void Draw()
        {
            currentWidth=Math.Max(280,canvas.ActualWidth>1?canvas.ActualWidth:960);plotWidth=currentWidth-left-right;var width=currentWidth;
            canvas.Children.Clear();
            var values=buckets.Select(Value).ToArray();var maximum=Math.Max(1d,values.Max()*1.12);
            var points=buckets.Select((bucket,index)=>new Point(left+(buckets.Count==1?plotWidth/2:index*plotWidth/(buckets.Count-1)),top+(1-values[index]/maximum)*plotHeight)).ToArray();
            currentPoints=points;
            foreach(var fraction in new[]{0d,.5,1d})
            {
                var y=top+plotHeight*fraction;var line=new Line{X1=left,X2=width-right,Y1=y,Y2=y,Stroke=gridBrush,StrokeThickness=1,StrokeDashArray=new DoubleCollection{2,7}};canvas.Children.Add(line);
            }
            canvas.Children.Add(new Line{X1=left,X2=width-right,Y1=top+plotHeight,Y2=top+plotHeight,Stroke=baselineBrush,StrokeThickness=1});
            var lineFigure=new PathFigure{StartPoint=points[0]};var areaFigure=new PathFigure{StartPoint=points[0],IsClosed=true};
            for(var i=0;i<points.Length-1;i++)
            {
                var current=points[i];var next=points[i+1];var previous=points[Math.Max(0,i-1)];var after=points[Math.Min(points.Length-1,i+2)];
                var c1=new Point(current.X+(next.X-previous.X)/6,current.Y+(next.Y-previous.Y)/6);var c2=new Point(next.X-(after.X-current.X)/6,next.Y-(after.Y-current.Y)/6);
                lineFigure.Segments.Add(new BezierSegment{Point1=c1,Point2=c2,Point3=next});areaFigure.Segments.Add(new BezierSegment{Point1=c1,Point2=c2,Point3=next});
            }
            areaFigure.Segments.Add(new LineSegment{Point=new Point(points[^1].X,top+plotHeight)});areaFigure.Segments.Add(new LineSegment{Point=new Point(points[0].X,top+plotHeight)});
            var areaGeometry=new PathGeometry();areaGeometry.Figures.Add(areaFigure);
            var areaBrush=new LinearGradientBrush{StartPoint=new Point(.5,0),EndPoint=new Point(.5,1)};areaBrush.GradientStops.Add(new GradientStop{Color=ColorHelper.FromArgb(72,163,138,245),Offset=0});areaBrush.GradientStops.Add(new GradientStop{Color=ColorHelper.FromArgb(5,163,138,245),Offset=1});
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path{Data=areaGeometry,Fill=areaBrush});
            var lineGeometry=new PathGeometry();lineGeometry.Figures.Add(lineFigure);canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path{Data=lineGeometry,Stroke=accent,StrokeThickness=3.5,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round});
            var occupied=new List<(double Start,double End)>();
            foreach(var index in Enumerable.Range(0,buckets.Count).Where(i=>values[i]>0).OrderByDescending(i=>values[i]))
            {
                var text=Format(values[index]);var labelWidth=Math.Clamp(text.Length*7+18,44,116);var x=Math.Clamp(points[index].X,left+labelWidth/2,width-right-labelWidth/2);var start=x-labelWidth/2-4;var end=x+labelWidth/2+4;
                if(occupied.Any(item=>start<item.End&&end>item.Start))continue;occupied.Add((start,end));
                var label=new Border{Width=labelWidth,Height=22,CornerRadius=new CornerRadius(7),Background=new SolidColorBrush(ColorHelper.FromArgb(38,163,138,245)),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(110,163,138,245)),BorderThickness=new Thickness(1),Child=new TextBlock{Text=text,FontSize=10.5,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,Foreground=accent,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
                Canvas.SetLeft(label,x-labelWidth/2);Canvas.SetTop(label,Math.Max(top,points[index].Y-29));canvas.Children.Add(label);
            }
            var visibleAxisLabels=Math.Clamp((int)Math.Floor(width/105),2,7);var axisStep=Math.Max(1,(int)Math.Ceiling(buckets.Count/(double)visibleAxisLabels));
            for(var i=0;i<buckets.Count;i++)
            {
                if(values[i]>0){var point=new Ellipse{Width=9,Height=9,Fill=new SolidColorBrush(ColorHelper.FromArgb(255,42,45,54)),Stroke=accent,StrokeThickness=2};Canvas.SetLeft(point,points[i].X-4.5);Canvas.SetTop(point,points[i].Y-4.5);canvas.Children.Add(point);ToolTipService.SetToolTip(point,$"{buckets[i].Label} · {Exact(buckets[i])}");}
                if(i%axisStep!=0&&i!=buckets.Count-1)continue;
                var label=new TextBlock{Text=buckets[i].Label+"\n"+Format(values[i]),Width=100,FontSize=10.5,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.NoWrap};Canvas.SetLeft(label,Math.Clamp(points[i].X-50,0,width-100));Canvas.SetTop(label,height-bottom+18);canvas.Children.Add(label);
            }
            indicator=new Line{Y1=top,Y2=top+plotHeight,Stroke=accent,StrokeThickness=1,StrokeDashArray=new DoubleCollection{3,4},Visibility=Visibility.Collapsed};canvas.Children.Add(indicator);
            tooltipText=new TextBlock{FontSize=11,TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Colors.White)};tooltip=new Border{Width=204,Padding=new Thickness(12,9,12,9),CornerRadius=new CornerRadius(10),Background=new SolidColorBrush(ColorHelper.FromArgb(245,36,39,48)),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(110,163,138,245)),BorderThickness=new Thickness(1),Child=tooltipText,Visibility=Visibility.Collapsed};canvas.Children.Add(tooltip);
            totalText.Text=metricValue switch{TrendMetric.Requests=>$"{buckets.Sum(bucket=>bucket.Requests):N0}",TrendMetric.Cost=>FormatCost((double)buckets.Sum(bucket=>bucket.EstimatedCost)),_=>FormatNumber(buckets.Sum(bucket=>bucket.Tokens))};
            foreach(var pair in buttons)
            {
                var active=pair.Key==metricValue;
                pair.Value.Background=new SolidColorBrush(active?ColorHelper.FromArgb(42,163,138,245):Colors.Transparent);
                pair.Value.BorderBrush=new SolidColorBrush(active?ColorHelper.FromArgb(120,163,138,245):ColorHelper.FromArgb(42,127,132,150));
                if(active)pair.Value.Foreground=accent;else pair.Value.ClearValue(Control.ForegroundProperty);
            }
        }
        foreach(var pair in buttons){var selected=pair.Key;pair.Value.Click+=(_,_)=>{metricValue=selected;Draw();};}
        int Nearest(double x)=>buckets.Count==1?0:(int)Math.Round(Math.Clamp((x-left)/plotWidth,0,1)*(buckets.Count-1));
        canvas.PointerMoved+=(_,e)=>
        {
            if(currentPoints.Length==0||indicator is null||tooltip is null||tooltipText is null)return;
            var index=Nearest(e.GetCurrentPoint(canvas).Position.X);indicator.X1=indicator.X2=currentPoints[index].X;indicator.Visibility=Visibility.Visible;
            tooltipText.Text=$"{buckets[index].From:yyyy-MM-dd}"+(hourly?$" · {buckets[index].Label}":buckets[index].From==buckets[index].Through?"":L10n.F("sBB1DCF021E6A", buckets[index].Through))+L10n.F("s0B429D8FF175", buckets[index].Tokens, buckets[index].Requests, buckets[index].EstimatedCost);
            Canvas.SetLeft(tooltip,Math.Clamp(currentPoints[index].X-102,8,currentWidth-212));Canvas.SetTop(tooltip,8);tooltip.Visibility=Visibility.Visible;
        };
        canvas.PointerExited+=(_,_)=>{if(indicator is not null)indicator.Visibility=Visibility.Collapsed;if(tooltip is not null)tooltip.Visibility=Visibility.Collapsed;};
        canvas.PointerPressed+=(_,e)=>{if(hourly||currentPoints.Length==0)return;var index=Nearest(e.GetCurrentPoint(canvas).Position.X);select(buckets[index].From,buckets[index].Through);};
        header.SizeChanged+=(_,e)=>
        {
            var narrow=e.NewSize.Width<700;Grid.SetColumn(headerRight,narrow?0:1);Grid.SetRow(headerRight,narrow?1:0);Grid.SetColumnSpan(headerRight,narrow?2:1);headerRight.HorizontalAlignment=narrow?HorizontalAlignment.Left:HorizontalAlignment.Right;headerRight.Orientation=narrow?Orientation.Horizontal:Orientation.Horizontal;
        };
        canvas.SizeChanged+=(_,e)=>{if(e.NewSize.Width>1&&Math.Abs(e.NewSize.Width-currentWidth)>.5)Draw();};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(canvas,L10n.T("sDE3A2D3FA72A"));Draw();return root;
    }
    internal static FrameworkElement Bars(IReadOnlyList<(string Day,long Total)> days,Action<string> select)
    {
        var grid=new Grid{Height=190,ColumnSpacing=8,Margin=new Thickness(0,8,0,0)};
        if(days.Count==0){grid.Children.Add(new TextBlock{Text=L10n.T("s26FDD367E960"),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Opacity=.65});return grid;}
        var maximum=Math.Max(1,days.Max(d=>d.Total));
        for(var i=0;i<days.Count;i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            var day=days[i];var column=new Grid{RowSpacing=8};
            column.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            column.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var bar=new StackPanel{VerticalAlignment=VerticalAlignment.Bottom,HorizontalAlignment=HorizontalAlignment.Stretch,Spacing=6};
            bar.Children.Add(new TextBlock{Text=day.Total>=1000?$"{day.Total/1000d:0.#}k":day.Total.ToString(),FontSize=11,HorizontalAlignment=HorizontalAlignment.Center});
            var rectangle=new Border{Width=48,Height=Math.Max(day.Total>0?3:0,140d*day.Total/maximum),HorizontalAlignment=HorizontalAlignment.Center,Background=new SolidColorBrush(Colorset[0]),CornerRadius=new CornerRadius(4,4,0,0),Margin=new Thickness(5,0,5,0)};
            bar.Children.Add(rectangle);column.Children.Add(bar);
            var button=new Button{Content=day.Day.Length==10?day.Day[5..]:day.Day,Padding=new Thickness(2),FontSize=11,HorizontalAlignment=HorizontalAlignment.Stretch,Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new Thickness(0)};
            button.Click+=(_,_)=>select(day.Day);ToolTipService.SetToolTip(button,L10n.F("sF43D590F6A4C", day.Day, day.Total));
            Grid.SetRow(button,1);column.Children.Add(button);Grid.SetColumn(column,i);grid.Children.Add(column);
        }
        return grid;
    }
    internal static FrameworkElement Models(IEnumerable<UsageEvent> events)
    {
        var groups=events.GroupBy(e=>e.Model).Select(g=>(Name:g.Key,Total:g.Sum(e=>e.Tokens.Total))).OrderByDescending(g=>g.Total).ToList();
        if(groups.Count>5){var rest=groups.Skip(5).Sum(g=>g.Total);groups=groups.Take(5).Append((Name:L10n.T("sE010BA9D2A30"),Total:rest)).ToList();}
        var total=groups.Sum(g=>g.Total);var panel=new StackPanel{Spacing=14};var ring=new Grid{Width=166,Height=166,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,12,0,0)};
        ring.Children.Add(new Ellipse{Stroke=new SolidColorBrush(ColorHelper.FromArgb(50,150,150,170)),StrokeThickness=20,Margin=new Thickness(9)});
        double angle=-90;
        for(var i=0;i<groups.Count&&total>0;i++)
        {
            var sweep=360d*groups[i].Total/total;if(sweep<=0)continue;
            if(sweep>=359.999){ring.Children.Add(new Ellipse{Stroke=new SolidColorBrush(Colorset[i]),StrokeThickness=20,Margin=new Thickness(9)});continue;}
            Point At(double degrees)=>new(83+64*Math.Cos(degrees*Math.PI/180),83+64*Math.Sin(degrees*Math.PI/180));
            var figure=new PathFigure{StartPoint=At(angle),IsClosed=false};
            figure.Segments.Add(new ArcSegment{Point=At(angle+sweep),Size=new Size(64,64),IsLargeArc=sweep>180,SweepDirection=SweepDirection.Clockwise});
            var geometry=new PathGeometry();geometry.Figures.Add(figure);
            ring.Children.Add(new Microsoft.UI.Xaml.Shapes.Path{Data=geometry,Stroke=new SolidColorBrush(Colorset[i]),StrokeThickness=20});angle+=sweep;
        }
        ring.Children.Add(new TextBlock{Text=$"{total:N0}\nToken",FontSize=18,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center});panel.Children.Add(ring);
        for(var i=0;i<groups.Count;i++)
        {
            var row=new Grid{ColumnSpacing=10};row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            row.Children.Add(new Ellipse{Width=9,Height=9,Fill=new SolidColorBrush(Colorset[i]),VerticalAlignment=VerticalAlignment.Center});
            var label=new TextBlock{Text=groups[i].Name,FontSize=12,TextWrapping=TextWrapping.Wrap};Grid.SetColumn(label,1);row.Children.Add(label);
            var percentage=new TextBlock{Text=total>0?$"{100d*groups[i].Total/total:0.#}%":"—",FontSize=12};Grid.SetColumn(percentage,2);row.Children.Add(percentage);panel.Children.Add(row);
        }
        if(total==0)panel.Children.Add(new TextBlock{Text=L10n.T("s1ABA7CBE2A2C"),Opacity=.65});return panel;
    }
}

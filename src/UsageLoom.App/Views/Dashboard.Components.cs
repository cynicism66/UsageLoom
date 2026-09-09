using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private Border Card(UIElement child)
    {
        var card = new Border { Child = child, Padding = new Thickness(20), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch };
        surfaces.Add(new WeakReference<Border>(card)); Paint(card); return card;
    }
    private void Paint(Border card)
    {
        if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            var colors = new Windows.UI.ViewManagement.UISettings();
            card.Background = new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background));
            card.BorderBrush = new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
            return;
        }
        var dark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        card.Background = new SolidColorBrush(dark ? ColorHelper.FromArgb(255, 41, 45, 53) : ColorHelper.FromArgb(255, 255, 255, 255));
        card.BorderBrush = new SolidColorBrush(dark ? ColorHelper.FromArgb(30, 255, 255, 255) : ColorHelper.FromArgb(18, 20, 20, 35));
    }
    private void RefreshSurfaces()
    {
        surfaces.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in surfaces) if(reference.TryGetTarget(out var card)) Paint(card);
        var dark=(Content as FrameworkElement)?.ActualTheme==ElementTheme.Dark;
        if(Content is Grid root&&!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            root.Background=new SolidColorBrush(dark?ColorHelper.FromArgb(255,32,35,41):ColorHelper.FromArgb(255,245,246,249));
            navigation.Background=new SolidColorBrush(dark?ColorHelper.FromArgb(255,25,28,34):ColorHelper.FromArgb(255,239,241,246));
        }
        AppWindow.TitleBar.ButtonForegroundColor=dark?Colors.White:Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor=dark?Colors.LightGray:Colors.DimGray;
        AppWindow.TitleBar.ButtonBackgroundColor=Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor=Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor=dark?ColorHelper.FromArgb(70,255,255,255):ColorHelper.FromArgb(25,0,0,0);
        AppWindow.TitleBar.ButtonHoverForegroundColor=dark?Colors.White:Colors.Black;
    }
    private static Grid ResponsiveCards(IReadOnlyList<UIElement> cards, int maximumColumns, double minimumWidth,double firstWeight=1)
    {
        var grid=new Grid{ColumnSpacing=16,RowSpacing=16};var previous=-1;
        void Layout(double width)
        {
            var columns=Math.Clamp((int)((Math.Max(width,minimumWidth)+16)/(minimumWidth+16)),1,Math.Max(1,Math.Min(maximumColumns,cards.Count)));
            if(columns==previous)return;previous=columns;grid.ColumnDefinitions.Clear();grid.RowDefinitions.Clear();
            for(var i=0;i<columns;i++)grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(i==0&&columns>1?firstWeight:1,GridUnitType.Star)});
            for(var i=0;i<(cards.Count+columns-1)/columns;i++)grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            for(var i=0;i<cards.Count;i++){Grid.SetColumn((FrameworkElement)cards[i],i%columns);Grid.SetRow((FrameworkElement)cards[i],i/columns);}
        }
        foreach(var card in cards)grid.Children.Add(card);
        grid.SizeChanged+=(_,e)=>Layout(e.NewSize.Width);Layout(1000);return grid;
    }
    private void DetachHistoryFilter()
    {
        // Keep the logical owner explicitly: WinUI may hide visual parents while
        // the old subtree is unloaded or awaiting layout.
        filterHost?.Children.Remove(filterBar);
        filterHost=null;
        Detach(filterBar);
    }
    private static void Detach(UIElement element)
    {
        var parent=(element as FrameworkElement)?.Parent??VisualTreeHelper.GetParent(element);
        switch(parent)
        {
            case Panel panel:panel.Children.Remove(element);break;
            case Border border when ReferenceEquals(border.Child,element):border.Child=null;break;
            case ContentControl content when ReferenceEquals(content.Content,element):content.Content=null;break;
        }
    }
}

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private static FrameworkElement PlanBadge(QuotaState quota,bool small)
    {
        var plan=quota.Plan?.Trim().ToLowerInvariant();
        if(quota.IsLocalAccount||plan is not ("prolite" or "pro"))return new TextBlock{Text=quota.PlanDisplay,FontSize=13,Opacity=.7,TextWrapping=TextWrapping.Wrap};
        var gold=plan=="pro";var label=gold?"Pro 20X":"Pro 5X";
        var stack=new StackPanel{Spacing=3,HorizontalAlignment=HorizontalAlignment.Left};
        if(new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            stack.Children.Add(new TextBlock{Text=label,FontSize=small?14:20,FontWeight=Microsoft.UI.Text.FontWeights.Bold});
        }
        else
        {
            var layers=new Grid{Padding=new Thickness(1,0,3,3)};
            void Layer(Windows.UI.Color color,double y,double x=0)
            {
                layers.Children.Add(new TextBlock{Text=label,FontFamily=new FontFamily("Segoe UI Variable Display"),FontSize=small?14:20,FontWeight=Microsoft.UI.Text.FontWeights.Bold,CharacterSpacing=30,
                    Foreground=new SolidColorBrush(color),RenderTransform=new TranslateTransform{X=x,Y=y}});
            }
            Layer(ColorHelper.FromArgb(200,0,0,0),3,1);
            Layer(gold?ColorHelper.FromArgb(255,116,76,18):ColorHelper.FromArgb(255,89,101,119),2);
            Layer(gold?ColorHelper.FromArgb(255,184,130,39):ColorHelper.FromArgb(255,153,166,185),1);
            Layer(gold?ColorHelper.FromArgb(255,255,222,137):ColorHelper.FromArgb(255,237,243,251),0);
            var background=new LinearGradientBrush{StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(0,1)};
            background.GradientStops.Add(new GradientStop{Offset=0,Color=gold?ColorHelper.FromArgb(255,83,67,39):ColorHelper.FromArgb(255,69,77,89)});
            background.GradientStops.Add(new GradientStop{Offset=.48,Color=ColorHelper.FromArgb(255,36,39,45)});
            background.GradientStops.Add(new GradientStop{Offset=1,Color=ColorHelper.FromArgb(255,25,27,32)});
            stack.Children.Add(new Border{Child=layers,Background=background,CornerRadius=new CornerRadius(7),Padding=new Thickness(small?8:12,2,small?8:12,2),BorderThickness=new Thickness(1),
                BorderBrush=new SolidColorBrush(gold?ColorHelper.FromArgb(255,161,128,68):ColorHelper.FromArgb(255,135,147,164))});
        }
        if(!quota.Fresh)stack.Children.Add(new TextBlock{Text=L10n.T("s4F6E26963CA2"),FontSize=11,Opacity=.65});
        ToolTipService.SetToolTip(stack,L10n.F("s43DABF58694C", quota.Plan));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stack,label+(quota.Fresh?"":L10n.T("s525117F3D826")));
        return stack;
    }
    private void RenderCompact()
    {
        EnsureCompactContent();
        UpdateCompactContent();
    }
}

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private UIElement SettingsPanel()
    {
        var panel=new StackPanel{Spacing=14,Padding=new Thickness(8)};
        var cli=new TextBox{Header=L10n.T("s7BA827935C21"),Text=app.Config.CliPath??""};
        var home=new TextBox{Header=L10n.T("s629E14E0CB84"),Text=app.IsDemo?L10n.T("sF99723F1D4A1"):app.Config.CodexHome,IsReadOnly=app.IsDemo};
        var auto=new ToggleSwitch{Header=L10n.T("sB91E861CB0A1"),IsOn=app.Config.AutoRefresh,OnContent=L10n.Language=="en-US"?"On":"开",OffContent=L10n.Language=="en-US"?"Off":"关"};
        var seconds=new NumberBox{Header=L10n.T("sEE9FB7A5B7ED"),Minimum=30,Maximum=3600,Value=app.Config.BackgroundSeconds};
        var foreground=new NumberBox{Header=L10n.T("s2791B0EC8468"),Minimum=15,Maximum=3600,Value=app.Config.ForegroundSeconds};
        var low=new ToggleSwitch{Header=L10n.T("s843B9B07A843"),IsOn=app.Config.LowNotify,OnContent=L10n.Language=="en-US"?"On":"开",OffContent=L10n.Language=="en-US"?"Off":"关"};
        var threshold=new NumberBox{Header=L10n.T("sE511B92C189E"),Minimum=1,Maximum=99,Value=app.Config.LowPercent};
        var reset=new ToggleSwitch{Header=L10n.T("sD4CF61025E3C"),IsOn=app.Config.ResetNotify,OnContent=L10n.Language=="en-US"?"On":"开",OffContent=L10n.Language=="en-US"?"Off":"关"};
        var minutes=new NumberBox{Header=L10n.T("s87603CAC4453"),Minimum=1,Maximum=120,Value=app.Config.ResetMinutes};
        var theme=new ComboBox{Header=L10n.T("s86A63F23A076"),ItemsSource=new[]{L10n.T("s217CFE7DB1E3"),L10n.T("sAA0819DFC4D8"),L10n.T("sA6B75D068032")},SelectedIndex=app.Config.Theme switch{"Light"=>1,"Dark"=>2,_=>0}};
        theme.SelectionChanged+=(_,_)=>app.SetTheme(theme.SelectedIndex switch{1=>"Light",2=>"Dark",_=>"Default"});
        var language=new ComboBox{Header=L10n.T("s9087B82BC720"),ItemsSource=new[]{"简体中文","English"},SelectedIndex=app.Config.Language=="en-US"?1:0};
        appearanceChoice=theme;languageChoice=language;
        language.SelectionChanged+=(_,_)=>app.SetLanguage(language.SelectedIndex==1?"en-US":"zh-CN");
        void Section(string title, params UIElement[] controls)
        {
            var group=new StackPanel{Spacing=12};
            foreach(var control in controls)
            {
                object? label=control switch{TextBox box=>box.Header,ComboBox box=>box.Header,ToggleSwitch box=>box.Header,NumberBox box=>box.Header,_=>null};
                if(label is null){group.Children.Add(control);continue;}
                switch(control){case TextBox box:box.Header=null;break;case ComboBox box:box.Header=null;box.MinWidth=160;box.HorizontalAlignment=HorizontalAlignment.Right;break;case ToggleSwitch box:box.Header=null;box.HorizontalAlignment=HorizontalAlignment.Right;break;case NumberBox box:box.Header=null;box.Width=150;box.HorizontalAlignment=HorizontalAlignment.Right;break;}
                var caption=new TextBlock{Text=label.ToString(),VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap};
                group.Children.Add(ResponsiveCards(new UIElement[]{caption,control},2,250));
            }
            panel.Children.Add(StableExpander.Configure(new Expander{Header=title,IsExpanded=false,Content=group,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
        }
        Section(L10n.T("s38C043E08502"),theme,language);Section(L10n.T("s6E89737A00E1"),cli,home);Section(L10n.T("s16685D3221B9"),auto,foreground,seconds);Section(L10n.T("sA28590E6B1D8"),low,threshold,reset,minutes);
        panel.Children.Add(CapacitySettings());
        Section(L10n.T("sD39DC68172D7"),
            new TextBlock{Text=L10n.T("s9371F74C3221"),TextWrapping=TextWrapping.Wrap},
            Button(L10n.T("sE400A5FF247B"),async()=>
            {
                await app.UseCachedLoginAsync(cli.Text.Trim(),home.Text.Trim());
            }));
        panel.Children.Add(AppUpdatePanel());
        async Task Authorize(bool logout)
        {
            if(app.Authorizing)throw new InvalidOperationException(L10n.T("s081CDEB65923"));
            var dialog=new ContentDialog{XamlRoot=((FrameworkElement)Content).XamlRoot,Title=logout?L10n.T("sFEED4D9A5148"):L10n.T("s3B66445DA90A"),CloseButtonText=L10n.T("s2CD0F3BE8738"),PrimaryButtonText=logout?L10n.T("sADF861A2A343"):L10n.T("sC33740D74933"),DefaultButton=ContentDialogButton.Close,
                Content=logout?L10n.T("s8CF30C934669"):L10n.T("sA591BDDE3429")};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            if(!app.IsDemo)app.Config.CliPath=cli.Text.Trim();
            await app.AuthorizeAsync(logout);
        }
        Section(L10n.T("s5AB943671D29"),
            new TextBlock{Text=L10n.T("s9DD1731C07FC"),TextWrapping=TextWrapping.Wrap},
            Button(L10n.T("s3B0F18AAC8CE"),async()=>await Authorize(false)),
            Button(L10n.T("s16302475FAEB"),()=>{app.CancelAuthorization();return Task.CompletedTask;}),
            Button(L10n.T("s0F7D6E35193A"),async()=>await Authorize(true)));
        actions.Children.Add(Button(L10n.T("sC8550237BA70"),async()=>
        {
            if(app.IsDemo){status.Text=L10n.T("sAF0109C061C5");return;}
            if(app.Authorizing)throw new InvalidOperationException(L10n.T("s3B5CB5F80AA4"));
            if(!double.IsFinite(seconds.Value)||!double.IsFinite(foreground.Value)||!double.IsFinite(threshold.Value)||!double.IsFinite(minutes.Value))throw new ArgumentException(L10n.T("sD1E6C6F01819"));
            if(string.IsNullOrWhiteSpace(home.Text)||!Path.IsPathFullyQualified(home.Text.Trim()))throw new ArgumentException(L10n.T("s772A83DE6A61"));
            app.Config.CliPath=cli.Text.Trim();app.Config.CodexHome=home.Text.Trim();app.Config.AutoRefresh=auto.IsOn;
            app.Config.BackgroundSeconds=(int)Math.Clamp(seconds.Value,30,3600);app.Config.LowNotify=low.IsOn;app.Config.LowPercent=(int)Math.Clamp(threshold.Value,1,99);
            app.Config.ForegroundSeconds=(int)Math.Clamp(foreground.Value,15,3600);
            app.Config.ResetNotify=reset.IsOn;app.Config.ResetMinutes=(int)Math.Clamp(minutes.Value,1,120);app.Config.Theme=theme.SelectedIndex switch{1=>"Light",2=>"Dark",_=>"Default"};app.Config.AppearanceConfigured=true;
            await app.SaveSettingsAsync();ApplyTheme();
        }));
        panel.Children.Add(new TextBlock{Text=L10n.T("sD22BE3762C11"),TextWrapping=TextWrapping.Wrap,Opacity=.7});
        return panel;
    }
}

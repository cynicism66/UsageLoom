using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UsageLoom.Core;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private void RenderStats()
    {
        var selectedRange=SelectedHistoryRange();
        var filter=$"{selectedPage}|{historyRangeIndex}|{selectedRange.From}|{selectedRange.Through}|{historySearch.Text}|{sessionOrder.SelectedIndex}|{sessionPage}|{breakdownKind.SelectedIndex}|{DateTime.Today:yyyy-MM-dd}";
        if(ReferenceEquals(renderedEvents,app.Events)&&renderedFilter==filter)return;
        renderedEvents=app.Events;renderedFilter=filter;
        DetachHistoryFilter();
        updateOverviewLayout=null;
        overviewQuota=new StackPanel{Spacing=10};overviewQuotaCard=Card(overviewQuota);
        overviewQuotaCard.Visibility=app.Quota.HasQuotaDisplay?Visibility.Visible:Visibility.Collapsed;
        if(app.Quota.HasQuotaDisplay)
        {
            overviewQuota.Children.Add(new TextBlock{Text=L10n.T("s73BE6011896A"),FontSize=18});
            foreach(var window in app.Quota.PrimaryWindows)
            {
                overviewQuota.Children.Add(new TextBlock{Text=L10n.F("s6D65FE80C728", window.Label, window.RemainingText),TextWrapping=TextWrapping.Wrap});
                overviewQuota.Children.Add(new ProgressBar{Minimum=0,Maximum=100,Value=window.Remaining,Height=5,Foreground=accent});
            }
            overviewQuota.Children.Add(Button(L10n.T("s14B8852CD2D1"),()=>{Navigate("quota");return Task.CompletedTask;}));
        }
        UIElement? pricingDetails=null;
        statsPanel.Children.Clear();
        sessionWorkspace.Children.Clear();
        historySearch.Visibility=selectedPage=="sessions"?Visibility.Visible:Visibility.Collapsed;
        var rows=historyRangeIndex==4&&!selectedRange.IsBounded?[]:HistoryQuery.Filter(app.Events,new(selectedRange.From,selectedRange.Through,selectedPage=="sessions"?historySearch.Text:""),app.SessionNames);
        var total=rows.Aggregate(new TokenUsage(),(a,e)=>a+e.Tokens);var estimate=Pricing.Summarize(rows);var sessionCount=rows.Select(item=>item.Session).Distinct(StringComparer.Ordinal).Count();
        if(selectedPage=="sessions")
        {
            BuildSessionWorkspace(rows,total,estimate);
            return;
        }
        var rangeHeader=new Grid{ColumnSpacing=12};rangeHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});rangeHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        rangeHeader.Children.Add(filterBar);filterHost=rangeHeader;var rangeLabel=new TextBlock{Text=selectedRange.Label,Opacity=.62,FontSize=12,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(0,8,0,0)};Grid.SetColumn(rangeLabel,1);rangeHeader.Children.Add(rangeLabel);
        rangeHeader.SizeChanged+=(_,e)=>rangeLabel.Visibility=e.NewSize.Width<820?Visibility.Collapsed:Visibility.Visible;
        statsPanel.Children.Add(Card(rangeHeader));
        if(selectedPage=="overview")
        {
        UIElement[] cards;
        if(app.Quota.AccountKey is {} accountKey)
        {
            var accountTotal=rows.Where(item=>string.Equals(item.AccountScope,accountKey,StringComparison.Ordinal)).Sum(item=>item.Tokens.Total);
            var inferredTotal=rows.Where(item=>item.AccountScope==accountKey&&item.AccountAttribution is ("restart-inferred" or "user-confirmed")).Sum(item=>item.Tokens.Total);
            var remainder=Math.Max(0,total.Total-accountTotal);
            var accountCards=new[]{Metric(L10n.T("s106A6D3ED1C9"),total.Total.ToString("N0"),L10n.T("s89FA01F64D00")),Metric(L10n.T(inferredTotal>0?"restart.account":"sF866B39A1323"),accountTotal.ToString("N0"),inferredTotal>0?L10n.F("restart.note",UsageNumbers.Compact(inferredTotal)):L10n.T("s9D26E20C07EF")),Metric(L10n.T("s79420D8F355C"),remainder.ToString("N0"),L10n.T("s2544E8C23B66"))};
            statsPanel.Children.Add(Card(ResponsiveCards(accountCards,3,210)));
            statsPanel.Children.Add(Button(L10n.T("attribution.inspect"),()=>ShowAttribution(rows.ToList(),accountKey)));
            cards=[Metric("Session",sessionCount.ToString("N0"),L10n.T("s65188C08136A")),Metric(L10n.T("s9386F02260C5"),rows.Count.ToString("N0"),L10n.T("s1C7995A11FC3")),Metric(estimate.Status,rows.Count==0?L10n.T("s497C85690C4C"):estimate.DisplayAmount,L10n.T("s479A28B8FEA5")),Metric(L10n.T("s79868BCABA0B"),total.Total>0?$"{100d*estimate.Priced/total.Total:0.#}%":"—",L10n.F("s7A93A1B1657A", estimate.Unpriced))];
        }
        else cards=[Metric(L10n.T("s52497358558A"),total.Total.ToString("N0"),L10n.T("sC9084B3EDE58")),Metric("Session",sessionCount.ToString("N0"),L10n.F("s16BF7608AEAB", rows.Count)),Metric(estimate.Status,rows.Count==0?L10n.T("s497C85690C4C"):estimate.DisplayAmount,L10n.T("s479A28B8FEA5")),Metric(L10n.T("s79868BCABA0B"),total.Total>0?$"{100d*estimate.Priced/total.Total:0.#}%":"—",L10n.F("s7A93A1B1657A", estimate.Unpriced))];
        var metrics=ResponsiveCards(cards,4,190);statsPanel.Children.Add(Card(metrics));
        if(rows.Count>0)
        {
            var details=new StackPanel{Spacing=12};
            details.Children.Add(new TextBlock{Text=L10n.F("s217DBBB4BEE1", Pricing.CatalogVersion),TextWrapping=TextWrapping.Wrap,FontSize=13});
            if(!string.IsNullOrWhiteSpace(estimate.Reason))details.Children.Add(new TextBlock{Text=estimate.Reason,TextWrapping=TextWrapping.Wrap});
            foreach(var note in estimate.Notes)details.Children.Add(new TextBlock{Text="• "+note,TextWrapping=TextWrapping.Wrap,FontSize=12});
            foreach(var group in rows.GroupBy(e=>e.Model))
            {
                var basis=Pricing.Find(group.Key);var item=Pricing.Summarize(group);
                details.Children.Add(new TextBlock{Text=$"{group.Key} · {item.Status} · {item.DisplayAmount}",FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
                if(basis is null){details.Children.Add(new TextBlock{Text=L10n.T("sF37B3009AB7F"),TextWrapping=TextWrapping.Wrap});continue;}
                var price=basis.Rates;
                details.Children.Add(new TextBlock{Text=L10n.F("sBB890CE3940F", price.Input, price.Cached?.ToString()??L10n.T("s4D8C1C5B4283"), price.Write?.ToString()??L10n.T("s4D8C1C5B4283"), price.Output, basis.CheckedOn, basis.EffectiveFrom?.ToString("yyyy-MM-dd")??L10n.T("sB40CA4D5DDA7"), basis.EffectiveUntil?.ToString("yyyy-MM-dd")??L10n.T("sB40CA4D5DDA7"))+(basis.PromotionAtLeastThrough is {} promo?L10n.F("s45A4A87C6785", promo):""),TextWrapping=TextWrapping.Wrap,FontSize=12});
                details.Children.Add(new HyperlinkButton{Content=L10n.T("sEA5EC54A64AB"),NavigateUri=basis.Source});
            }
            pricingDetails=StableExpander.Configure(new Expander{Header=L10n.T("s0ADC58E779A7"),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=details});
        }
        }
        else statsPanel.Children.Add(new TextBlock{Text=L10n.F("sDC533DFEA032", rows.Count, total.Total, estimate.DisplayAmount, estimate.Status),TextWrapping=TextWrapping.Wrap,Opacity=.7});
        if(rows.Count==0)statsPanel.Children.Add(Card(new TextBlock{Text=app.Events.Count==0?L10n.T("s9610A3870D65"):historyRangeIndex==4&&!selectedRange.IsBounded?L10n.T("sFC65DDBF820C"):L10n.T("sFB1B61C7B33C"),FontSize=18,TextWrapping=TextWrapping.Wrap}));
        if(selectedPage=="overview")
        {
            var hourly=selectedRange.IsSingleDay;
            var trend = UsageCharts.Trend(hourly?HistoryQuery.HourlyTrend(rows,selectedRange.From!.Value):HistoryQuery.Trend(rows,selectedRange),DrillIntoRange,hourly);
            var modelPanel=new StackPanel{Spacing=12};modelPanel.Children.Add(new TextBlock{Text=L10n.T("s39F1A54A74FF"),FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            modelPanel.Children.Add(UsageCharts.Models(rows));
            statsPanel.Children.Add(Card(trend));
            var recent=new StackPanel{Spacing=12};recent.Children.Add(new TextBlock{Text=L10n.T("sEB6D48FEBAB7"),FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            foreach(var session in HistoryQuery.Sessions(rows,"recent",app.SessionNames).Take(4))recent.Children.Add(new TextBlock{Text=L10n.F("sE1A5A63BFA3F", session.Name, session.Tokens, session.Project, session.ShortId),TextWrapping=TextWrapping.Wrap,FontSize=13});
            recent.Children.Add(Button(L10n.T("sD24CA3C355C7"),()=>{Navigate("sessions");return Task.CompletedTask;}));
            // The quota summary updates independently without collapsing expanded history controls.
            var recentCard=Card(recent);
            var bottomItems=new List<UIElement>{Card(modelPanel),recentCard,overviewQuotaCard};var bottom=ResponsiveCards(bottomItems,3,300);
            var localQuotaCard=overviewQuotaCard;
            void FitBottom(){Grid.SetColumnSpan(recentCard,localQuotaCard.Visibility==Visibility.Collapsed&&bottom.ColumnDefinitions.Count>=3?2:1);}
            bottom.SizeChanged+=(_,_)=>FitBottom();updateOverviewLayout=FitBottom;FitBottom();
            statsPanel.Children.Add(bottom);
            var composition=new StackPanel{Spacing=10};composition.Children.Add(new TextBlock{Text=L10n.T("s719CAC683641"),FontSize=19,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            composition.Children.Add(new TextBlock{Text=L10n.F("sF6A9702B386C", total.Input, total.Output),FontSize=16});
            composition.Children.Add(new TextBlock{Text=L10n.F("sE1435C7E009C", total.Cached, total.CacheWrite, total.Reasoning),TextWrapping=TextWrapping.Wrap,Opacity=.65});
            composition.Children.Add(new TextBlock{Text=L10n.T("s4D584456798B"),FontSize=12,Opacity=.6});statsPanel.Children.Add(StableExpander.Configure(new Expander{Header=L10n.T("sA29E4482CC69"),Content=composition,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch}));
        }
        else
        {
            statsPanel.Children.Add(breakdownKind);
            AddGroups((string)breakdownKind.SelectedItem,rows.GroupBy(e=>breakdownKind.SelectedIndex switch{1=>e.Project,2=>e.Agent,_=>e.Model}).OrderByDescending(g=>g.Sum(e=>e.Tokens.Total)));
        }
        if(pricingDetails is not null)statsPanel.Children.Add(pricingDetails);
    }
    private void BuildSessionWorkspace(List<UsageEvent> rows,TokenUsage total,Estimate estimate)
    {
        sessionWorkspace.RowDefinitions.Clear();
        sessionWorkspace.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        sessionWorkspace.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        sessionWorkspace.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        sessionWorkspace.Children.Add(filterBar);filterHost=sessionWorkspace;

        var sessions=HistoryQuery.Sessions(rows,sessionOrder.SelectedIndex switch{1=>"recent",2=>"name",_=>"tokens"},app.SessionNames);
        const int pageSize=30;sessionPage=Math.Clamp(sessionPage,0,Math.Max(0,(sessions.Count-1)/pageSize));
        var sort=new ComboBox{ItemsSource=sessionOrder.ItemsSource,SelectedIndex=sessionOrder.SelectedIndex,Width=160};
        sort.SelectionChanged+=(_,_)=>sessionOrder.SelectedIndex=sort.SelectedIndex;
        var paging=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Right};paging.Children.Add(sort);
        paging.Children.Add(new TextBlock{Text=$"{sessionPage+1}/{Math.Max(1,(sessions.Count+pageSize-1)/pageSize)}",VerticalAlignment=VerticalAlignment.Center});
        paging.Children.Add(Button("‹",()=>{sessionPage=Math.Max(0,sessionPage-1);Render();return Task.CompletedTask;}));
        paging.Children.Add(Button("›",()=>{if((sessionPage+1)*pageSize<sessions.Count)sessionPage++;Render();return Task.CompletedTask;}));
        var summary=new TextBlock{Text=L10n.F("sDC533DFEA032", rows.Count, total.Total, estimate.DisplayAmount, estimate.Status),TextWrapping=TextWrapping.Wrap,Opacity=.7,VerticalAlignment=VerticalAlignment.Center};
        var toolbar=new Grid{ColumnSpacing=12,RowSpacing=8};
        toolbar.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});toolbar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        toolbar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});toolbar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        toolbar.Children.Add(summary);toolbar.Children.Add(paging);Grid.SetColumn(paging,1);
        toolbar.SizeChanged+=(_,e)=>
        {
            var narrow=e.NewSize.Width<620;
            Grid.SetColumn(paging,narrow?0:1);Grid.SetRow(paging,narrow?1:0);Grid.SetColumnSpan(paging,narrow?2:1);
            paging.HorizontalAlignment=narrow?HorizontalAlignment.Left:HorizontalAlignment.Right;
        };
        Grid.SetRow(toolbar,1);sessionWorkspace.Children.Add(toolbar);

        var list=new ListView{SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
        var detail=new StackPanel{Spacing=12,VerticalAlignment=VerticalAlignment.Top};
        var detailScroll=new ScrollViewer{Content=detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalAlignment=VerticalAlignment.Stretch};
        void ShowSession(SessionSummary session)
        {
            detail.Children.Clear();var tokens=session.Events.Aggregate(new TokenUsage(),(sum,item)=>sum+item.Tokens);
            detail.Children.Add(new TextBlock{Text=session.Name,FontSize=18,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true});
            detail.Children.Add(new TextBlock{Text=L10n.T("s37C7E8C78184")+session.Session,FontSize=11,Opacity=.55,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true});
            detail.Children.Add(new TextBlock{Text=L10n.F("s89DF7DB2285B", session.Project, session.LastActivity?.ToLocalTime(), session.Tokens, Pricing.Summarize(session.Events).DisplayAmount),TextWrapping=TextWrapping.Wrap});
            detail.Children.Add(new TextBlock{Text=L10n.F("s06B0C7A9C706", tokens.Input, tokens.Output, tokens.Cached, tokens.CacheWrite, tokens.Reasoning),TextWrapping=TextWrapping.Wrap,LineHeight=26});
            detail.Children.Add(new TextBlock{Text=L10n.T("s80F5416751AC"),Opacity=.65});
            var shown=0;
            void More()
            {
                foreach(var item in session.Events.Skip(shown).Take(50))
                {
                    var line=new TextBlock{Text=$"{item.LocalDate} {item.Timestamp?.ToLocalTime():HH:mm:ss} · {item.Model}\n{item.Tokens.Total:N0} Token"+(item.QualityNote is {} note?" · "+note:""),TextWrapping=TextWrapping.Wrap,FontSize=12,Padding=new Thickness(0,8,0,8)};
                    detail.Children.Add(new Border{Child=line,BorderThickness=new Thickness(0,1,0,0),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(28,150,155,170))});
                }
                shown=Math.Min(session.Events.Count,shown+50);
                if(shown<session.Events.Count){Button? more=null;more=Button(L10n.T("s1E3B71D39432"),()=>{detail.Children.Remove(more!);More();return Task.CompletedTask;});detail.Children.Add(more);}
            }
            More();
            DispatcherQueue.TryEnqueue(()=>detailScroll.ChangeView(null,0,null));
        }
        foreach(var session in sessions.Skip(sessionPage*pageSize).Take(pageSize))list.Items.Add(new ListViewItem{Tag=session,HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=new TextBlock{Text=L10n.F("sE1A5A63BFA3F", session.Name, session.Tokens, session.Project, session.ShortId),TextWrapping=TextWrapping.Wrap},Padding=new Thickness(12)});
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is ListViewItem{Tag:SessionSummary session})ShowSession(session);};
        if(list.Items.Count>0)list.SelectedIndex=0;else detail.Children.Add(new TextBlock{Text=app.Events.Count==0?L10n.T("s9610A3870D65"):L10n.T("s8A69095CA983"),FontSize=18,TextWrapping=TextWrapping.Wrap});

        var listCard=Card(list);var detailCard=Card(detailScroll);
        listCard.VerticalAlignment=VerticalAlignment.Stretch;detailCard.VerticalAlignment=VerticalAlignment.Stretch;
        var split=new Grid{ColumnSpacing=16,RowSpacing=16,VerticalAlignment=VerticalAlignment.Stretch};
        split.Children.Add(listCard);split.Children.Add(detailCard);
        var stacked=false;
        void Layout(double width)
        {
            var next=width<720;if(next==stacked&&split.ColumnDefinitions.Count>0)return;stacked=next;
            split.ColumnDefinitions.Clear();split.RowDefinitions.Clear();
            if(stacked)
            {
                split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
                split.RowDefinitions.Add(new RowDefinition{Height=new GridLength(2,GridUnitType.Star)});split.RowDefinitions.Add(new RowDefinition{Height=new GridLength(3,GridUnitType.Star)});
                Grid.SetColumn(listCard,0);Grid.SetRow(listCard,0);Grid.SetColumn(detailCard,0);Grid.SetRow(detailCard,1);
            }
            else
            {
                split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(.9,GridUnitType.Star)});split.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.4,GridUnitType.Star)});
                split.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
                Grid.SetColumn(listCard,0);Grid.SetRow(listCard,0);Grid.SetColumn(detailCard,1);Grid.SetRow(detailCard,0);
            }
        }
        split.SizeChanged+=(_,e)=>Layout(e.NewSize.Width);Layout(1000);
        Grid.SetRow(split,2);sessionWorkspace.Children.Add(split);
    }
    private void AddGroups(string title,IEnumerable<IGrouping<string,UsageEvent>> groups)
    {
        var panel=new StackPanel{Spacing=0};var table=groups.ToList();var total=table.Sum(g=>g.Sum(e=>e.Tokens.Total));
        Grid Row(params string[] values)
        {
            var row=new Grid{MinWidth=740,Padding=new Thickness(12,14,12,14),ColumnSpacing=16};
            for(var i=0;i<values.Length;i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(i==0?2:1,GridUnitType.Star)});
                var text=new TextBlock{Text=values[i],TextWrapping=TextWrapping.Wrap,FontSize=13,IsTextSelectionEnabled=true};Grid.SetColumn(text,i);row.Children.Add(text);
            }
            return row;
        }
        panel.Children.Add(Row(title,L10n.T("s2087C777C06F"),L10n.T("s1DED6559F626"),L10n.T("sFB04ADDB4C26"),L10n.T("s9638021CAEE5"),L10n.T("sC6CA9057CACD"),L10n.T("sCC87B27B644A")));
        foreach(var group in table)
        {
            var tokens=group.Aggregate(new TokenUsage(),(sum,item)=>sum+item.Tokens);var estimate=Pricing.Summarize(group);
            var row=Row(group.Key,tokens.Input.ToString("N0"),tokens.Cached.ToString("N0"),tokens.Output.ToString("N0"),tokens.Total.ToString("N0"),estimate.DisplayAmount,total>0?$"{100d*tokens.Total/total:0.#}%":"—");
            ToolTipService.SetToolTip(row,estimate.Status+" · "+estimate.Reason);
            panel.Children.Add(new Border{Child=row,BorderThickness=new Thickness(0,1,0,0),BorderBrush=new SolidColorBrush(ColorHelper.FromArgb(35,140,145,160))});
        }
        statsPanel.Children.Add(Card(new ScrollViewer{Content=panel,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollMode=ScrollMode.Enabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollMode=ScrollMode.Disabled}));
        statsPanel.Children.Add(new TextBlock{Text=L10n.T("s5689C7F8C468"),Opacity=.65,FontSize=12,TextWrapping=TextWrapping.Wrap});
    }
}

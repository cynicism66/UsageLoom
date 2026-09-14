using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UsageLoom.App;

internal sealed partial class Dashboard
{
    private void VerifySessionTitleRefresh()
    {
        if(compact||!app.NavigationCheck||app.Events.Count==0)return;
        var savedPage=selectedPage;var events=app.Events;
        foreach(var pageName in new[]{"overview","sessions"})
        {
            ShowPage(pageName);
            foreach(var title in new[]{"Title refresh fixture A","Title refresh fixture B"})
            {
                app.ConfigureSessionTitlePreview(title);
                ((FrameworkElement)Content).UpdateLayout();
                if(!ReferenceEquals(events,app.Events)||!CapacityTestDescendants((FrameworkElement)Content).OfType<TextBlock>().Any(text=>text.Text.Contains(title)))
                    throw new InvalidOperationException("Session title-only refresh did not reach "+pageName);
            }
        }
        ShowPage(savedPage);
        Program.Log.Write("INFO","NavigationTest","Session title-only refresh passed: overview and sessions, usage unchanged");
    }
}

using System.Collections;

namespace CaissonWallUi;

internal static class RibbonLayout
{
    public const string TabName = "Caisson Reinforcement";
    public const string PanelName = "Model Creation";

    // The public Revit API creates tabs, but cannot hide tabs already loaded by
    // retired applications. Hide only these explicitly retired ribbon titles;
    // keep the Rebar Modeling application's background bridge loaded.
    public static void HideLegacyTabs()
    {
        var component = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Autodesk.Windows.ComponentManager", false))
            .FirstOrDefault(t => t != null);
        var ribbon = component?.GetProperty("Ribbon")?.GetValue(null);
        if (ribbon?.GetType().GetProperty("Tabs")?.GetValue(ribbon) is not IEnumerable tabs) return;
        foreach (var tab in tabs)
        {
            if (tab == null) continue;
            var type = tab.GetType();
            var title = type.GetProperty("Title")?.GetValue(tab)?.ToString();
            if (title is not ("沉井配筋" or "雨篷配筋" or "Rebar Modeling")) continue;
            var visible = type.GetProperty("IsVisible");
            if (visible?.CanWrite == true && visible.PropertyType == typeof(bool)) visible.SetValue(tab, false);
        }
    }
}

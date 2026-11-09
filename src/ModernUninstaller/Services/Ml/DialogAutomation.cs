using System.Windows;
using System.Windows.Automation;

namespace ModernUninstaller.Services.Ml;

/// <summary>Liest ein Fenster per UI Automation in einen <see cref="DialogSnapshot"/> und führt Aktionen darauf aus.</summary>
public static class DialogAutomation
{
    const int MaxTexts = 40, MaxTextLength = 300;

    public sealed record Captured(DialogSnapshot Snapshot, IReadOnlyList<AutomationElement> Elements);

    public static Captured? Capture(AutomationElement window)
    {
        try
        {
            var title = window.Current.Name ?? "";
            var texts = new List<string>();
            foreach (AutomationElement t in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
            {
                var s = (t.Current.Name ?? "").Trim();
                if (s.Length == 0 || t.Current.IsOffscreen) continue;
                if (s.Length > MaxTextLength) s = s[..MaxTextLength];
                if (!texts.Contains(s)) texts.Add(s);
                if (texts.Count >= MaxTexts) break;
            }

            var found = new List<(Rect r, DialogControl c, AutomationElement e)>();
            var cond = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
            foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, cond))
            {
                var cur = e.Current;
                if (!cur.IsEnabled || cur.IsOffscreen) continue;
                var label = Clean(cur.Name ?? "");
                if (label.Length == 0) continue;
                if (IsTitleBarButton(e)) continue;   // Schließen-/Minimieren-Knopf der Titelleiste ist keine Antwort
                var type = cur.ControlType;
                DialogControl c;
                if (type == ControlType.CheckBox)
                    c = new DialogControl(ControlKind.CheckBox, label, e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp) && ((TogglePattern)tp).Current.ToggleState == ToggleState.On);
                else if (type == ControlType.RadioButton)
                    c = new DialogControl(ControlKind.Radio, label, e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) && ((SelectionItemPattern)sp).Current.IsSelected);
                else c = new DialogControl(ControlKind.Button, label);
                found.Add((cur.BoundingRectangle, c, e));
            }
            // Lesereihenfolge: Zeile für Zeile, links nach rechts (für "erster/letzter Button").
            found = found.OrderBy(x => double.IsInfinity(x.r.Top) ? 0 : Math.Round(x.r.Top / 12)).ThenBy(x => double.IsInfinity(x.r.Left) ? 0 : x.r.Left).ToList();
            return new Captured(new DialogSnapshot(title, texts, found.Select(x => x.c).ToList()), found.Select(x => x.e).ToList());
        }
        catch { return null; }   // Fenster verschwindet gerade
    }

    static string Clean(string s) => s.Replace("&", "").Trim();

    static bool IsTitleBarButton(AutomationElement e)
    {
        try
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(e);
            return parent != null && parent.Current.ControlType == ControlType.TitleBar;
        }
        catch { return false; }
    }

    public static bool Perform(AutomationElement e, ControlKind kind)
    {
        try
        {
            switch (kind)
            {
                case ControlKind.CheckBox when e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp):
                    ((TogglePattern)tp).Toggle(); return true;
                case ControlKind.Radio when e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp):
                    ((SelectionItemPattern)sp).Select(); return true;
            }
            if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return true; }
        }
        catch { }
        return false;
    }
}

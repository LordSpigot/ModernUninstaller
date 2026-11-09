using System.Windows;
using System.Windows.Controls;
using ModernUninstaller.Services;
using ModernUninstaller.Services.Ml;

namespace ModernUninstaller.Dialogs;

/// <summary>Rückfrage des Dialog-Assistenten. Die Antwort wird als Trainingsbeispiel gespeichert.</summary>
public partial class AssistDialog : DialogWindow
{
    public AssistAnswer Answer { get; private set; } = new(AssistChoice.Manual);
    const int NothingIndex = -1;

    public AssistDialog(DialogSnapshot window, AssistDecision decision)
    {
        InitializeComponent();
        ReasonText.Text = decision.Index >= 0
            ? $"Der Assistent würde „{window.Controls[decision.Index].Label}“ wählen ({decision.Confidence:P0}), ist sich aber nicht sicher: {decision.Reason}."
            : $"Der Assistent ist sich nicht sicher: {decision.Reason}.";
        WindowTitle.Text = string.IsNullOrWhiteSpace(window.Title) ? "(ohne Titel)" : window.Title;
        WindowText.Text = string.Join("\n", window.Texts.Where(t => t != window.Title).Take(8));

        var probs = decision.Ranking.ToDictionary(r => r.Index, r => r.P);
        for (int i = 0; i < window.Controls.Count; i++)
        {
            var c = window.Controls[i];
            var kind = c.Kind switch
            {
                ControlKind.CheckBox => c.IsOn ? "Häkchen entfernen: " : "Ankreuzen: ",
                ControlKind.Radio => "Auswählen: ",
                _ => "Klicken: ",
            };
            if (c.Kind == ControlKind.Radio && c.IsOn) continue;   // bereits gewählt
            var p = probs.TryGetValue(i, out var v) ? $"   ({v:P0})" : "";
            Choices.Children.Add(MakeChoice(kind + "„" + c.Label + "“" + p, i, i == decision.Index));
        }
        Choices.Children.Add(MakeChoice("Nichts anklicken – das Fenster erledigt sich selbst", NothingIndex, decision.Index < 0));
    }

    RadioButton MakeChoice(string text, int index, bool selected) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
        Tag = index,
        GroupName = "assist",
        IsChecked = selected,
        Margin = new Thickness(0, 0, 0, 8),
        Foreground = (System.Windows.Media.Brush)FindResource("Text"),
    };

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Choices.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (chosen == null) return;
        var idx = (int)chosen.Tag;
        Answer = idx == NothingIndex ? new AssistAnswer(AssistChoice.NothingToClick) : new AssistAnswer(AssistChoice.Perform, idx);
        CloseDialog(true);
    }

    void Manual_Click(object sender, RoutedEventArgs e) { Answer = new AssistAnswer(AssistChoice.Manual); CloseDialog(false); }
    void Stop_Click(object sender, RoutedEventArgs e) { Answer = new AssistAnswer(AssistChoice.StopAssistant); CloseDialog(false); }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ModernUninstaller;

/// <summary>Rahmenloses, abgerundetes Dialogfenster; das Template steht in Themes/Common.xaml.</summary>
public class DialogWindow : Window
{
    protected DialogWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(ForegroundProperty, "Text");
        Template = (ControlTemplate)Application.Current.FindResource("DialogWindowTemplate");
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Close") is Button close)
            close.Click += (_, _) => CloseDialog();
        if (GetTemplateChild("PART_Drag") is FrameworkElement drag)
            drag.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    protected void CloseDialog(bool? result = false)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }
}


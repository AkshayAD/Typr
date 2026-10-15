using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Ava.Windows;

/// <summary>Explicit, separate-process paste target. Opening this never drives keyboard input.</summary>
internal sealed class PasteTargetWindow : Window
{
    public PasteTargetWindow()
    {
        Title = "Ava paste target · manual validation"; Width = 650; Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "Manual paste target", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = "Start Ava separately, click below, then use its real hotkeys. This window does not simulate keypresses. Check multiline content and the delivery log. Do not run keyboard automation while the computer is in use.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var textbox = new TextBox { AcceptsReturn = true, MinHeight = 160, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(textbox, "Ava external paste validation target");
        var status = new TextBlock { Margin = new Thickness(0, 12, 0, 0) };
        var started = Stopwatch.StartNew();
        textbox.TextChanged += (_, _) => status.Text = $"Changed at {started.Elapsed.TotalSeconds:F3} s · {textbox.Text.Length} characters · {textbox.Text.Count(c => c == '\n') + 1} lines";
        root.Children.Add(textbox); root.Children.Add(status); Content = root;
        Loaded += (_, _) => textbox.Focus();
    }
}

using System.Windows;
// UseWindowsForms puts System.Windows.Forms types in scope project-wide, colliding with these.
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;

namespace EmailClient.UI;

/// <summary>One button in a <see cref="ConfirmDialog"/>, in the order passed to
/// <see cref="ConfirmDialog.Show"/>. The last choice given is always the filled/primary button;
/// earlier ones are plain outline buttons.</summary>
public sealed record ConfirmChoice(string Text, bool Destructive = false);

/// <summary>
/// An app-themed replacement for <c>MessageBox.Show</c> confirmations — rounded, palette-coloured,
/// with the app's own icon language, instead of the plain OS dialog chrome a MessageBox always
/// draws regardless of anything else in the app looking deliberately designed.
/// </summary>
public partial class ConfirmDialog : Window
{
    private string? _result;

    private ConfirmDialog(string title, string message, bool warningIcon, IReadOnlyList<ConfirmChoice> choices)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowCorners.Apply(this);

        TitleText.Text = title;
        MessageText.Text = message;

        // Standard Segoe Fluent Icons/MDL2 glyphs: warning triangle, and the info glyph already
        // used elsewhere in this app (the About page icon) and confirmed to render correctly.
        // Set via ConvertFromUtf32 rather than literal characters in source: these are Private Use
        // Area codepoints that get silently stripped when typed as literal text in some editors.
        IconGlyph.Text = char.ConvertFromUtf32(warningIcon ? 0xE7BA : 0xE946);
        IconGlyph.Foreground = (Brush)FindResource(warningIcon ? "Danger" : "Accent");
        IconBadge.Background = (Brush)FindResource(warningIcon ? "DangerSoft" : "AccentSoft");
        CloseGlyph.Text = char.ConvertFromUtf32(0xE8BB); // ChromeClose ("X")

        var outlineSlots = new[] { Button1, Button2 };
        var outlineChoices = choices.Take(choices.Count - 1).ToList();
        for (var i = 0; i < outlineSlots.Length; i++)
        {
            if (i < outlineChoices.Count)
            {
                outlineSlots[i].Content = outlineChoices[i].Text;
                outlineSlots[i].Tag = outlineChoices[i].Text;
                outlineSlots[i].Visibility = Visibility.Visible;
            }
            else
            {
                outlineSlots[i].Visibility = Visibility.Collapsed;
            }
        }

        var primary = choices[^1];
        Button3.Content = primary.Text;
        Button3.Tag = primary.Text;
        Button3.Background = (Brush)FindResource(primary.Destructive ? "Danger" : "Accent");

        Loaded += (_, _) => Button3.Focus();
    }

    /// <summary>
    /// Shows an app-themed modal confirmation and blocks until it's dismissed. Returns the Text of
    /// whichever choice was clicked, or null if dismissed via Escape or the window's own close
    /// affordance — callers should treat null the same as the first (conventionally "Cancel")
    /// choice. Needs 2 or 3 choices; the last one shown is the filled/primary action.
    /// </summary>
    public static string? Show(Window owner, string title, string message, bool warningIcon, params ConfirmChoice[] choices)
    {
        if (choices.Length is < 2 or > 3)
            throw new ArgumentException("ConfirmDialog needs 2 or 3 choices.", nameof(choices));

        var dialog = new ConfirmDialog(title, message, warningIcon, choices) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Choice_Click(object sender, RoutedEventArgs e)
    {
        _result = (string)((Button)sender).Tag;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        _result = null;
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}

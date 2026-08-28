using System.Windows;
using System.Windows.Input;

namespace EmailClient.UI;

public sealed record ComposeResult(string To, string Cc, string Bcc, string Subject, string Body)
{
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(To) || !string.IsNullOrWhiteSpace(Subject) || !string.IsNullOrWhiteSpace(Body);
}

public partial class ComposeWindow : Window
{
    /// <summary>Set when the user hits Send; null if the window was cancelled.</summary>
    public ComposeResult? Result { get; private set; }

    /// <summary>Whatever was typed when the window closed without sending, for draft-saving.</summary>
    public ComposeResult? Draft { get; private set; }

    public ComposeWindow(string to = "", string subject = "", string body = "", string cc = "", string bcc = "")
    {
        InitializeComponent();
        MaximizeBoundsFix.Apply(this);
        ToBox.Text = to;
        SubjectBox.Text = subject;
        BodyBox.Text = body;
        CcBox.Text = cc;
        BccBox.Text = bcc;

        if (!string.IsNullOrWhiteSpace(cc) || !string.IsNullOrWhiteSpace(bcc))
            ShowCcBcc();

        PreviewKeyDown += ComposeWindow_PreviewKeyDown;

        // Capture whatever was typed on close so MainWindow can keep it as a draft when the
        // window is dismissed without sending.
        Closed += (_, _) =>
        {
            if (Result is null)
                Draft = new ComposeResult(ToBox.Text, CcBox.Text, BccBox.Text, SubjectBox.Text, BodyBox.Text);
        };
    }

    private void CcBccToggle_Click(object sender, MouseButtonEventArgs e) => ShowCcBcc();

    private void ShowCcBcc()
    {
        CcBccPanel.Visibility = Visibility.Visible;
        CcBccToggle.Visibility = Visibility.Collapsed;
    }

    // Ctrl+Enter to send and Esc to cancel are standard across Gmail, Outlook, and Apple Mail.
    private void ComposeWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SendButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ToBox.Text))
        {
            System.Windows.MessageBox.Show(this, "Add at least one recipient.", "Missing recipient",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The actual send is driven by MainWindow, after a short undo-send window — this window's
        // job is just to collect the message and hand it back.
        Result = new ComposeResult(ToBox.Text, CcBox.Text, BccBox.Text, SubjectBox.Text, BodyBox.Text);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}

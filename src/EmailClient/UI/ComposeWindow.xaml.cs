using System.Windows;
using System.Windows.Input;
using EmailClient.Automation;

namespace EmailClient.UI;

public partial class ComposeWindow : Window
{
    private readonly DomBridge _bridge;
    private readonly bool _useMockData;

    public ComposeWindow(DomBridge bridge, bool useMockData = false, string to = "", string subject = "", string body = "")
    {
        InitializeComponent();
        MaximizeBoundsFix.Apply(this);
        _bridge = bridge;
        _useMockData = useMockData;
        ToBox.Text = to;
        SubjectBox.Text = subject;
        BodyBox.Text = body;

        PreviewKeyDown += ComposeWindow_PreviewKeyDown;
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

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        SendButton.IsEnabled = false;
        try
        {
            if (_useMockData)
            {
                // Sample-data mode: no real webmail session to drive yet. Just simulate the
                // round trip so the compose flow can be tried without a completed login.
                await Task.Delay(400);
            }
            else
            {
                await _bridge.StartComposeAsync();
                await _bridge.FillComposeAsync(ToBox.Text, SubjectBox.Text, BodyBox.Text);
                await _bridge.ClickSendAsync();
            }
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Couldn't send: {ex.Message}", "Send failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            SendButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}

using System.Windows;
using EmailClient.Automation;

namespace EmailClient.UI;

public partial class ComposeWindow : Window
{
    private readonly DomBridge _bridge;

    public ComposeWindow(DomBridge bridge)
    {
        InitializeComponent();
        _bridge = bridge;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        SendButton.IsEnabled = false;
        try
        {
            await _bridge.StartComposeAsync();
            await _bridge.FillComposeAsync(ToBox.Text, SubjectBox.Text, BodyBox.Text);
            await _bridge.ClickSendAsync();
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

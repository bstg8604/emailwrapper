using System.Windows;
using EmailClient.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EmailClient.Automation;

/// <summary>
/// Hosts the real webmail.iitb.ac.in page in a WebView2 control. Shown only for the user's
/// real login; hidden (but still running) afterward so the DomBridge can puppet it.
/// </summary>
public sealed class AutomationHost : Window
{
    public const string WebmailUrl = "https://webmail.iitb.ac.in/";

    private readonly WebView2 _webView = new();

    public event EventHandler? LoggedIn;
    public event EventHandler<string>? PageMessageReceived;

    public CoreWebView2 Core => _webView.CoreWebView2;

    public AutomationHost()
    {
        Title = "IITB Webmail — Sign in";
        Width = 480;
        Height = 640;
        Content = _webView;
        // Shown centered for login; hidden entirely once authenticated.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    public async Task InitializeAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: AppSettings.WebView2UserDataFolder);

        await _webView.EnsureCoreWebView2Async(env);

        _webView.CoreWebView2.WebMessageReceived += (_, e) =>
            PageMessageReceived?.Invoke(this, e.WebMessageAsJson);

        _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

        _webView.CoreWebView2.Navigate(WebmailUrl);
    }

    private bool _loggedInFired;

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _loggedInFired)
            return;

        // Heuristic check for a logged-in inbox vs. a login form. Zimbra's exact markup
        // needs live confirmation — see DomBridge.IsLoggedInScript for the selector.
        var result = await _webView.CoreWebView2.ExecuteScriptAsync(DomBridge.IsLoggedInScript);
        if (result.Trim('"') == "true")
        {
            _loggedInFired = true;
            LoggedIn?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Re-check login state after a manual navigation (e.g. user just submitted the login form).</summary>
    public void RecheckLoginState() => _loggedInFired = false;

    public Task<string> ExecuteScriptAsync(string script) => _webView.CoreWebView2.ExecuteScriptAsync(script);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The automation window should only ever be hidden, never actually closed,
        // for as long as the app is running — closing it would tear down the session.
        e.Cancel = true;
        Hide();
    }
}

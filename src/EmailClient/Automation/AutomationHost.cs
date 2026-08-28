using System.Windows;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _loginPoll;
    private bool _loggedInFired;

    public event EventHandler? LoggedIn;
    public event EventHandler<string>? PageMessageReceived;

    public CoreWebView2 Core => _webView.CoreWebView2;

    public AutomationHost()
    {
        Title = "Peacock — Sign in to webmail.iitb.ac.in";
        Width = 480;
        Height = 640;
        Content = _webView;
        // Shown centered for login; hidden entirely once authenticated.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Full-page-reload logins are caught by NavigationCompleted, but Roundcube's login
        // could also complete via an AJAX request with no page reload — a short poll while
        // not yet logged in catches that case too, without depending on any specific flow.
        _loginPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _loginPoll.Tick += async (_, _) => await CheckLoginStateAsync();
    }

    public async Task InitializeAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: AppSettings.WebView2UserDataFolder);

        await _webView.EnsureCoreWebView2Async(env);

        _webView.CoreWebView2.WebMessageReceived += (_, e) =>
            PageMessageReceived?.Invoke(this, e.WebMessageAsJson);

        _webView.CoreWebView2.NavigationCompleted += async (_, e) =>
        {
            if (e.IsSuccess)
                await CheckLoginStateAsync();
        };

        _webView.CoreWebView2.Navigate(WebmailUrl);
        _loginPoll.Start();
    }

    private async Task CheckLoginStateAsync()
    {
        if (_loggedInFired)
            return;

        // Heuristic check for a logged-in inbox vs. a login form (Roundcube's #messagelist /
        // "rcmrow" markup) — see DomBridge.IsLoggedInScript for the selector.
        var result = await _webView.CoreWebView2.ExecuteScriptAsync(DomBridge.IsLoggedInScript);
        if (result.Trim('"') != "true")
            return;

        _loggedInFired = true;
        _loginPoll.Stop();
        LoggedIn?.Invoke(this, EventArgs.Empty);
    }

    public Task<string> ExecuteScriptAsync(string script) => _webView.CoreWebView2.ExecuteScriptAsync(script);

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The automation window should only ever be hidden, never actually closed,
        // for as long as the app is running — closing it would tear down the session.
        e.Cancel = true;
        Hide();
    }
}

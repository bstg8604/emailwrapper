using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Color = System.Windows.Media.Color;
using EmailClient.Settings;
using EmailClient.UI;

namespace EmailClient.Mail;

public enum AccountPage { Profile, Signature, Contacts, Folders, Settings, Help, About }

/// <summary>
/// The account page: sign-in/profile, signature editing, contacts, folder management, and
/// preferences — five pages behind one left sidebar (like a settings window). A real, owned,
/// taskbar-less window (not a Popup) — WPF Popups can't reliably composite rounded-corner
/// transparency around a windowed native control, and this hosts WebView2 (the signature editor);
/// once WebView2 existed anywhere in a Popup's tree the whole popup rendered as an opaque square
/// regardless of any Border clipping.
/// </summary>
public partial class AccountWindow : Window
{
    public AccountSettings? Result { get; private set; }
    public ImapMailBackend? Backend { get; private set; }

    /// <summary>Fired once — sign-in success or the window being closed some other way. Check
    /// <see cref="Result"/>/<see cref="Backend"/> to see whether a sign-in actually happened.</summary>
    public event EventHandler? Completed;

    private readonly AppSettings _settings;
    private readonly ImapMailBackend? _mail;
    private readonly Action? _onFoldersChanged;
    private readonly Action<AccountSettings>? _onSwitchAccount;
    private readonly string? _activeEmail;
    private AccountSettings? _profileAccount;

    private readonly ObservableCollection<SignatureEntry> _signatures;
    private int _defaultSignatureIndex;
    private bool _suppressSignatureEvents;
    private RichHtmlEditor? _sigEditor;
    private bool _sigEditorInitStarted;

    private readonly List<ManualContact> _manualContacts;

    private List<Automation.MailFolder> _currentFolders = [];
    private string? _renamingMailbox;

    public AccountWindow(AccountSettings? existing, AppSettings settings, ImapMailBackend? mail,
        Action? onFoldersChanged, Action<AccountSettings>? onSwitchAccount = null)
    {
        InitializeComponent();
        _settings = settings;
        _mail = mail;
        _onFoldersChanged = onFoldersChanged;
        _onSwitchAccount = onSwitchAccount;
        _activeEmail = existing?.Email;

        // Remembers whatever size it was last resized to, the same way the main window's own
        // bounds persist across sessions.
        Width = Math.Max(MinWidth, settings.AccountWindowWidth);
        Height = Math.Max(MinHeight, settings.AccountWindowHeight);
        // On Closing, not every SizeChanged tick — the same as MainWindow's own bounds, and for
        // the same reason: SizeChanged fires continuously while a resize border is being dragged,
        // and writing settings.json on every one of those would be needless disk churn.
        Closing += (_, _) =>
        {
            _settings.AccountWindowWidth = Width;
            _settings.AccountWindowHeight = Height;
            _settings.Save();

            // Detach ownership before the native window actually closes — WPF's WindowStyle="None"
            // + WindowChrome + Owner combination can otherwise send the owner (MainWindow) a
            // minimize along with this window's own close.
            Owner = null;
        };

        Loaded += (_, _) => UI.WindowCorners.Apply(this);
        this.FadeInOnShow();

        // Every place that used to just raise Completed now also needs to close this window
        // (it's a real Window now, not a Popup MainWindow closed on its behalf) — wiring it once
        // here instead of at each of those call sites.
        Completed += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Completed?.Invoke(this, EventArgs.Empty);
        };

        if (existing is not null)
        {
            EmailBox.Text = existing.Email;
            UsernameBox.Text = existing.Username;
            DisplayNameBox.Text = existing.DisplayName;
            // Without this, reopening Profile for an already-signed-in account (e.g. via
            // "Account settings…") and clicking Sign in again fails validation immediately —
            // the password field looked blank but the account was, in fact, already signed in.
            PasswordBox.Password = existing.Password;
            ImapHostBox.Text = existing.ImapHost;
            ImapPortBox.Text = existing.ImapPort.ToString();
            SmtpHostBox.Text = existing.SmtpHost;
            SmtpPortBox.Text = existing.SmtpPort.ToString();

            // Reopening this page (e.g. "Add account" after one already uses custom servers)
            // used to always start with Advanced collapsed, hiding the very settings that made
            // sign-in work last time.
            if (existing.UsesNonDefaultServers())
            {
                _iitbMode = false;
                UsernameLabel.Text = "Username";
                AdvancedToggle.Text = "Using IIT Bombay?";
                AdvancedPanel.Visibility = Visibility.Visible;
            }

            // Already signed in: show the read-only summary, not the sign-in form all over
            // again — that form is for actually adding a new account, and reusing it here made
            // reopening this page look like signing in was needed again.
            _profileAccount = existing;
            ShowProfileSummary(existing);
        }

        _signatures = new ObservableCollection<SignatureEntry>(
            settings.Signatures.Select(s => new SignatureEntry { Name = s.Name, Body = s.Body }));
        _defaultSignatureIndex = settings.Signatures.Count == 0
            ? -1
            : Math.Clamp(settings.DefaultSignatureIndex, 0, settings.Signatures.Count - 1);
        SignatureCombo.ItemsSource = _signatures;

        _manualContacts = ManualContactsStore.Load();

        SetActiveNav(NavProfile);
    }

    public void NavigateTo(AccountPage page)
    {
        switch (page)
        {
            case AccountPage.Signature: _ = ShowSignaturePageAsync(); break;
            case AccountPage.Contacts: ShowContactsPage(); break;
            case AccountPage.Folders: _ = ShowFoldersPageAsync(); break;
            case AccountPage.Settings: ShowSettingsPage(); break;
            case AccountPage.Help: ShowHelpPage(); break;
            case AccountPage.About: ShowAboutPage(); break;
            default: ShowProfilePage(); break;
        }
    }

    // ---- Sidebar / page switching --------------------------------------------------------------

    private void SetVisiblePage(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { ProfilePage, SignaturePage, ContactsPage, FoldersPage, SettingsPage, HelpPage, AboutPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Active nav row gets its highlight as a local value (fine — it's meant to stay
    /// highlighted regardless of hover); every other row has its local value cleared instead of
    /// stamped to Transparent, so the NavRow style's own hover trigger still fires for them. The
    /// same fix MainWindow's folder sidebar needed for the same reason.</summary>
    private void SetActiveNav(Border active)
    {
        foreach (var row in new[] { NavProfile, NavSignature, NavContacts, NavFolders, NavSettings, NavHelp, NavAbout })
        {
            if (row == active)
                row.Background = (System.Windows.Media.Brush)FindResource("NavActive");
            else
                row.ClearValue(Border.BackgroundProperty);
        }
    }

    private void ShowProfilePage()
    {
        SetVisiblePage(ProfilePage);
        SetActiveNav(NavProfile);
    }

    private async Task ShowSignaturePageAsync()
    {
        SetVisiblePage(SignaturePage);
        SetActiveNav(NavSignature);
        await EnsureSignatureEditorAsync();
    }

    private void ShowContactsPage()
    {
        SetVisiblePage(ContactsPage);
        SetActiveNav(NavContacts);
        CancelEditContact();
        RefreshContactsUI();
    }

    private async Task ShowFoldersPageAsync()
    {
        SetVisiblePage(FoldersPage);
        SetActiveNav(NavFolders);
        await RefreshFoldersUIAsync();
    }

    private void ShowSettingsPage()
    {
        SetVisiblePage(SettingsPage);
        SetActiveNav(NavSettings);
        RefreshSettingsUI();
    }

    private void ShowHelpPage()
    {
        SetVisiblePage(HelpPage);
        SetActiveNav(NavHelp);
    }

    private void ShowAboutPage()
    {
        SetVisiblePage(AboutPage);
        SetActiveNav(NavAbout);
        PopulateAbout();
    }

    /// <summary>
    /// Fills the About page from the assembly rather than from constants, so the version can never
    /// drift from the one that was actually built — the whole point of showing it is that a bug
    /// report can name the exact build.
    /// </summary>
    private void PopulateAbout()
    {
        var assembly = typeof(AccountWindow).Assembly;
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        // The informational version carries a "+<commit sha>" suffix when built from a repo; the
        // sha is noise on an About page, so it's trimmed but the version itself is preferred over
        // the four-part assembly version.
        var version = informational?.Split('+')[0]
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "1.0.0";

        AboutVersionText.Text = $"Version {version}";
        AboutRuntimeText.Text =
            $".NET {Environment.Version}  ·  Windows {Environment.OSVersion.Version}  ·  {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}";
        AboutDataPathText.Text = $"Your mail settings and logs are stored in {Diagnostics.Log.Directory}";

        var copyright = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyCopyrightAttribute), false)
            .OfType<System.Reflection.AssemblyCopyrightAttribute>()
            .FirstOrDefault()?.Copyright;
        AboutCopyrightText.Text = copyright ?? "";
    }

    /// <summary>
    /// Opens a link in the user's browser. WPF's Hyperlink raises this instead of navigating,
    /// because a NavigateUri inside a plain window has nowhere to navigate to; UseShellExecute is
    /// what hands it to the default browser rather than trying to run it as a process.
    /// </summary>
    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn($"Couldn't open the link {e.Uri}", ex);
        }
        e.Handled = true;
    }

    private void NavProfile_Click(object sender, MouseButtonEventArgs e) => ShowProfilePage();
    private async void NavSignature_Click(object sender, MouseButtonEventArgs e) => await ShowSignaturePageAsync();
    private void NavContacts_Click(object sender, MouseButtonEventArgs e) => ShowContactsPage();
    private async void NavFolders_Click(object sender, MouseButtonEventArgs e) => await ShowFoldersPageAsync();
    private void NavSettings_Click(object sender, MouseButtonEventArgs e) => ShowSettingsPage();
    private void NavHelp_Click(object sender, MouseButtonEventArgs e) => ShowHelpPage();
    private void NavAbout_Click(object sender, MouseButtonEventArgs e) => ShowAboutPage();

    // ---- Profile summary (already signed in) -----------------------------------------------------

    private void ShowProfileSummary(AccountSettings account)
    {
        ProfileSummaryPanel.Visibility = Visibility.Visible;
        ProfileSignInForm.Visibility = Visibility.Collapsed;

        ProfileInitial.Text = account.Initial;
        ProfileEmailText.Text = account.Email;
        ProfileDisplayNameBox.Text = account.DisplayName;
        ProfileServerText.Text = $"IMAP {account.ImapHost}:{account.ImapPort} · SMTP {account.SmtpHost}:{account.SmtpPort}";
        ChangePasswordPanel.Visibility = Visibility.Collapsed;
        PasswordChangeStatusText.Text = "";
    }

    private void ShowProfileSignInForm()
    {
        ProfileSummaryPanel.Visibility = Visibility.Collapsed;
        ProfileSignInForm.Visibility = Visibility.Visible;
    }

    private void ProfileDisplayNameBox_LostFocus(object sender, RoutedEventArgs e) => SaveDisplayName();

    private void ProfileDisplayNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SaveDisplayName();
    }

    private void SaveDisplayName()
    {
        if (_profileAccount is null || _profileAccount.DisplayName == ProfileDisplayNameBox.Text)
            return;
        // _profileAccount is the same AccountSettings instance MainWindow holds as its active
        // account (passed straight through as `existing`), so this takes effect immediately —
        // not just in the persisted store, but in the toolbar's account button too.
        _profileAccount.DisplayName = ProfileDisplayNameBox.Text.Trim();
        _profileAccount.Save();
    }

    private void ChangePasswordToggle_Click(object sender, MouseButtonEventArgs e)
    {
        ChangePasswordPanel.Visibility = ChangePasswordPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (ChangePasswordPanel.Visibility == Visibility.Visible)
            NewPasswordBox.Focus();
    }

    private void NewPasswordRevealToggle_Click(object sender, RoutedEventArgs e)
    {
        var revealing = NewPasswordRevealToggle.IsChecked == true;
        if (revealing)
        {
            NewPasswordRevealBox.Text = NewPasswordBox.Password;
            NewPasswordBox.Visibility = Visibility.Collapsed;
            NewPasswordRevealBox.Visibility = Visibility.Visible;
            NewPasswordRevealBox.Focus();
            NewPasswordRevealBox.CaretIndex = NewPasswordRevealBox.Text.Length;
        }
        else
        {
            NewPasswordBox.Password = NewPasswordRevealBox.Text;
            NewPasswordRevealBox.Visibility = Visibility.Collapsed;
            NewPasswordBox.Visibility = Visibility.Visible;
            NewPasswordBox.Focus();
        }
    }

    private void NewPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SavePasswordButton_Click(sender, new RoutedEventArgs());
    }

    /// <summary>Doesn't change anything on the mail server — there's no admin protocol for that
    /// here. Just verifies the new password actually authenticates before overwriting the saved
    /// (now stale) one, so a typo doesn't silently lock the account out of its own saved
    /// credentials.</summary>
    private async void SavePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var newPassword = NewPasswordRevealToggle.IsChecked == true ? NewPasswordRevealBox.Text : NewPasswordBox.Password;
        if (string.IsNullOrEmpty(newPassword) || _profileAccount is null)
            return;

        SavePasswordButton.IsEnabled = false;
        PasswordChangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        PasswordChangeStatusText.Text = "Checking…";

        var probe = new AccountSettings
        {
            Email = _profileAccount.Email,
            Password = newPassword,
            DisplayName = _profileAccount.DisplayName,
            ImapHost = _profileAccount.ImapHost,
            ImapPort = _profileAccount.ImapPort,
            SmtpHost = _profileAccount.SmtpHost,
            SmtpPort = _profileAccount.SmtpPort,
            SmtpSecurity = _profileAccount.SmtpSecurity,
        };
        var backend = new ImapMailBackend(probe);
        try
        {
            await backend.ConnectAsync();
            _profileAccount.Password = newPassword;
            _profileAccount.Save();
            PasswordChangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
            PasswordChangeStatusText.Text = "Password updated";
            NewPasswordBox.Password = "";
            NewPasswordRevealBox.Text = "";
        }
        catch (Exception ex)
        {
            PasswordChangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
            PasswordChangeStatusText.Text = $"Couldn't verify: {MailErrors.Friendly(ex)}";
        }
        finally
        {
            await backend.DisposeAsync();
            SavePasswordButton.IsEnabled = true;
        }
    }

    // ---- Profile (sign-in) ----------------------------------------------------------------------

    // Starts true (IITB terminology/prefills) since that's who this app is built for; "Not from
    // IIT Bombay?" switches the form to generic wording and clears the IITB-specific prefilled
    // values so nothing implies a server that isn't actually being used. Server settings are
    // editable either way — this only changes what's prefilled and what the fields are called.
    private bool _iitbMode = true;

    private void AdvancedToggle_Click(object sender, MouseButtonEventArgs e)
    {
        _iitbMode = !_iitbMode;
        ApplyIitbMode();
        AdvancedPanel.Visibility = _iitbMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyIitbMode()
    {
        if (_iitbMode)
        {
            UsernameLabel.Text = "LDAP ID";
            AdvancedToggle.Text = "Not from IIT Bombay?";
            if (string.IsNullOrWhiteSpace(EmailBox.Text))
                EmailBox.Text = "@iitb.ac.in";
            if (string.IsNullOrWhiteSpace(ImapHostBox.Text))
                ImapHostBox.Text = "imap.iitb.ac.in";
            if (string.IsNullOrWhiteSpace(SmtpHostBox.Text))
                SmtpHostBox.Text = "smtp-auth.iitb.ac.in";
        }
        else
        {
            UsernameLabel.Text = "Username";
            AdvancedToggle.Text = "Using IIT Bombay?";
            if (EmailBox.Text == "@iitb.ac.in")
                EmailBox.Text = "";
            if (ImapHostBox.Text == "imap.iitb.ac.in")
                ImapHostBox.Text = "";
            if (SmtpHostBox.Text == "smtp-auth.iitb.ac.in")
                SmtpHostBox.Text = "";
            // Ports (993/587) are standard IMAPS/submission ports well beyond just IITB, so they
            // stay put either way — only the IITB-specific hostnames and email domain get cleared.
        }
    }

    private void EmailBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SignInButton_Click(sender, new RoutedEventArgs());
    }

    /// <summary>WPF's PasswordBox can't reveal its own text, so this swaps in a plain TextBox
    /// mirroring the same value instead — the two never both hold focus/visibility at once.</summary>
    private void RevealPasswordToggle_Click(object sender, RoutedEventArgs e)
    {
        var revealing = RevealPasswordToggle.IsChecked == true;
        if (revealing)
        {
            PasswordRevealBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordRevealBox.Visibility = Visibility.Visible;
            PasswordRevealBox.Focus();
            PasswordRevealBox.CaretIndex = PasswordRevealBox.Text.Length;
        }
        else
        {
            PasswordBox.Password = PasswordRevealBox.Text;
            PasswordRevealBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordBox.Focus();
        }
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SignInButton_Click(sender, new RoutedEventArgs());
    }

    /// <summary>Builds an AccountSettings from the form, or null (with an error already shown) if
    /// something's missing or malformed — shared by both Sign in and Test connection so the two
    /// can't quietly validate differently.</summary>
    private AccountSettings? ReadFormOrShowError()
    {
        var email = EmailBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var password = RevealPasswordToggle.IsChecked == true ? PasswordRevealBox.Text : PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || string.IsNullOrEmpty(password))
        {
            ShowError("Enter your full email address and password.");
            return null;
        }
        if (!int.TryParse(ImapPortBox.Text, out var imapPort) || !int.TryParse(SmtpPortBox.Text, out var smtpPort))
        {
            ShowError("Ports must be numbers.");
            return null;
        }

        return new AccountSettings
        {
            Email = email,
            Username = username,
            Password = password,
            DisplayName = DisplayNameBox.Text.Trim(),
            ImapHost = ImapHostBox.Text.Trim(),
            ImapPort = imapPort,
            SmtpHost = SmtpHostBox.Text.Trim(),
            SmtpPort = smtpPort,
            SmtpSecurity = SmtpSecurity.StartTls,
        };
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        var account = ReadFormOrShowError();
        if (account is null)
            return;

        SetBusy(true, "Connecting…");
        var backend = new ImapMailBackend(account);
        try
        {
            await backend.ConnectAsync();
        }
        catch (Exception ex)
        {
            await backend.DisposeAsync();
            SetBusy(false);
            ShowError($"Couldn't sign in: {MailErrors.Friendly(ex)}");
            return;
        }

        account.Save();
        Result = account;
        Backend = backend;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Connects and immediately disconnects, without saving or closing — a way to check a
    /// port/host change actually works before committing to it.</summary>
    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        var account = ReadFormOrShowError();
        if (account is null)
            return;

        SetBusy(true, "Testing connection…");
        TestConnectionButton.IsEnabled = false;
        var backend = new ImapMailBackend(account);
        try
        {
            await backend.ConnectAsync();
            SetBusy(false, "Connection successful");
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError($"Couldn't connect: {MailErrors.Friendly(ex)}");
        }
        finally
        {
            await backend.DisposeAsync();
            TestConnectionButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy, string status = "")
    {
        SignInButton.IsEnabled = !busy;
        TestConnectionButton.IsEnabled = !busy;
        StatusText.Text = status;
        if (busy)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            BusySpinner.Visibility = Visibility.Visible;
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever };
            BusySpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }
        else
        {
            BusySpinner.Visibility = Visibility.Collapsed;
            BusySpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Completed?.Invoke(this, EventArgs.Empty);

    // ---- Signatures -----------------------------------------------------------------------------

    private SignatureEntry? Selected => SignatureCombo.SelectedItem as SignatureEntry;

    // A blank starting point means every new user retypes the same formal block by hand for
    // correspondence with faculty/administration — a plain, fill-in-the-blanks IITB template
    // (Name / Roll No. / Department, IIT Bombay) saves that first pass without forcing it on
    // anyone who deletes or edits it.
    private const string DefaultSignatureBody = "Regards,<br>Your Name<br>Roll No. XXXXXXXX<br>Department, IIT Bombay";

    private async Task EnsureSignatureEditorAsync()
    {
        if (_sigEditorInitStarted)
            return;
        _sigEditorInitStarted = true;

        _sigEditor = new RichHtmlEditor(SignatureEditorView);
        if (_signatures.Count > 0)
            SignatureCombo.SelectedIndex = Math.Max(_defaultSignatureIndex, 0);
        else
            RefreshSignatureEditorVisibility();

        // The very first signature's body loads once the editor finishes initialising —
        // SignatureCombo_SelectionChanged already ran above with the editor not ready yet.
        await _sigEditor.InitializeAsync(Selected is { } entry ? entry.BodyHtml : "");
    }

    private async void SignatureCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // The entry that just lost selection (if any) needs its in-progress edits flushed from
        // the editor before it's swapped out — otherwise switching signatures silently discards
        // whatever was typed since the last debounced push.
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is SignatureEntry previous && _sigEditor is { IsReady: true })
            previous.Body = await _sigEditor.GetHtmlAsync();

        RefreshSignatureEditorVisibility();
        if (Selected is not { } entry)
            return;

        _suppressSignatureEvents = true;
        SignatureNameBox.Text = entry.Name;
        SignatureDefaultCheck.IsChecked = SignatureCombo.SelectedIndex == _defaultSignatureIndex;
        _suppressSignatureEvents = false;

        if (_sigEditor is { IsReady: true })
            await _sigEditor.InitializeAsync(entry.BodyHtml);
    }

    private void RefreshSignatureEditorVisibility()
    {
        var hasSelection = Selected is not null;
        SignatureEditorPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SignatureEmptyHint.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SignatureNameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressSignatureEvents || Selected is not { } entry)
            return;
        entry.Name = SignatureNameBox.Text;
        // ComboBox doesn't observe property changes on its items, only collection changes, so nudge it.
        var index = SignatureCombo.SelectedIndex;
        SignatureCombo.Items.Refresh();
        SignatureCombo.SelectedIndex = index;
    }

    private void SignatureDefaultCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSignatureEvents)
            return;
        if (SignatureDefaultCheck.IsChecked == true)
            _defaultSignatureIndex = SignatureCombo.SelectedIndex;
        else if (SignatureCombo.SelectedIndex == _defaultSignatureIndex)
            _defaultSignatureIndex = _signatures.Count > 0 ? 0 : -1;
    }

    private void AddSignatureButton_Click(object sender, RoutedEventArgs e)
    {
        SignatureStatusText.Text = "";
        var entry = new SignatureEntry { Name = $"Signature {_signatures.Count + 1}", Body = DefaultSignatureBody };
        _signatures.Add(entry);
        if (_defaultSignatureIndex < 0)
            _defaultSignatureIndex = 0;
        SignatureCombo.SelectedItem = entry;
        SignatureNameBox.Focus();
        SignatureNameBox.SelectAll();
    }

    private void DeleteSignatureButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry)
            return;
        SignatureStatusText.Text = "";
        var index = _signatures.IndexOf(entry);
        _signatures.RemoveAt(index);

        if (_signatures.Count == 0)
            _defaultSignatureIndex = -1;
        else if (_defaultSignatureIndex >= _signatures.Count)
            _defaultSignatureIndex = _signatures.Count - 1;
        else if (index < _defaultSignatureIndex)
            _defaultSignatureIndex--;

        SignatureCombo.SelectedIndex = _signatures.Count == 0 ? -1 : Math.Min(index, _signatures.Count - 1);
    }

    private async void SaveSignaturesButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry && _sigEditor is { IsReady: true })
            entry.Body = await _sigEditor.GetHtmlAsync();

        _settings.Signatures = _signatures.ToList();
        _settings.DefaultSignatureIndex = Math.Max(_defaultSignatureIndex, 0);
        _settings.Save();
        SignatureStatusText.Text = _signatures.Count == 0 ? "Signatures cleared" : "Saved";
    }

    // ---- Signature formatting toolbar ----------------------------------------------------------

    private async void SigFontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sigEditor is not { IsReady: true } || SigFontCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string family })
            return;
        await _sigEditor.ExecAsync("fontName", family);
        await _sigEditor.CommitFontPreviewAsync();
    }

    private async void SigFontComboItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_sigEditor is not { IsReady: true } || sender is not System.Windows.Controls.ComboBoxItem { Tag: string family })
            return;
        await _sigEditor.PreviewFontNameAsync(family);
    }

    private async void SigFontCombo_DropDownClosed(object sender, EventArgs e) =>
        await (_sigEditor?.CancelFontPreviewAsync() ?? Task.CompletedTask);

    private async void SigSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sigEditor is not { IsReady: true } || SigSizeCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem { Content: string points })
            return;
        await _sigEditor.SetFontSizeAsync(int.Parse(points));
    }

    private async void Bold_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("bold") ?? Task.CompletedTask);
    private async void Italic_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("italic") ?? Task.CompletedTask);
    private async void Underline_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("underline") ?? Task.CompletedTask);
    private async void Strikethrough_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("strikeThrough") ?? Task.CompletedTask);
    private async void BulletList_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("insertUnorderedList") ?? Task.CompletedTask);
    private async void NumberedList_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("insertOrderedList") ?? Task.CompletedTask);
    private async void Indent_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("indent") ?? Task.CompletedTask);
    private async void Outdent_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("outdent") ?? Task.CompletedTask);
    private async void Quote_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("formatBlock", "blockquote") ?? Task.CompletedTask);
    private async void AlignLeft_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("justifyLeft") ?? Task.CompletedTask);
    private async void AlignCenter_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("justifyCenter") ?? Task.CompletedTask);
    private async void AlignRight_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("justifyRight") ?? Task.CompletedTask);
    private async void ClearFormatting_Click(object sender, RoutedEventArgs e) => await (_sigEditor?.ExecAsync("removeFormat") ?? Task.CompletedTask);

    // A fixed palette rather than Compose's full custom hue-picker — plenty for the handful of
    // accents a signature realistically needs, without dragging that picker's ~200 lines of
    // hand-built hue-grid/hex-input UI in here too.
    private static readonly (string Hex, string Name)[] ColourPalette =
    [
        ("#000000", "Black"), ("#5A5A5A", "Grey"), ("#B91C1C", "Red"), ("#C2410C", "Orange"),
        ("#A16207", "Gold"), ("#15803D", "Green"), ("#0E7490", "Teal"), ("#1D4ED8", "Blue"),
        ("#6D28D9", "Purple"), ("#BE185D", "Pink"),
    ];

    private static readonly (string Hex, string Name)[] HighlightPalette =
    [
        ("transparent", "None"), ("#FFF9C4", "Pale yellow"), ("#FFF176", "Yellow"), ("#C8E6C9", "Green"),
        ("#B3E5FC", "Blue"), ("#F8BBD0", "Pink"), ("#E1BEE7", "Purple"), ("#FFCCBC", "Orange"),
    ];

    private System.Windows.Controls.Primitives.Popup BuildColourPopup(
        FrameworkElement anchor, (string Hex, string Name)[] palette, Action<string> onPick)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5, Margin = new Thickness(8) };
        foreach (var (hex, name) in palette)
        {
            var swatch = new System.Windows.Controls.Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(2),
                Background = hex == "transparent"
                    ? System.Windows.Media.Brushes.White
                    : new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = name,
            };
            swatch.Click += (_, _) => onPick(hex);
            grid.Children.Add(swatch);
        }

        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xDD, 0xF0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Opacity = 0.15, BlurRadius = 16, ShadowDepth = 3 },
                Child = grid,
            },
            PlacementTarget = anchor,
            StaysOpen = false,
        };
        popup.KeepOnScreen();
        popup.AnimateOnOpen();
        return popup;
    }

    private System.Windows.Controls.Primitives.Popup? _colourPopup;

    private async void TextColourButton_Click(object sender, RoutedEventArgs e)
    {
        if (_colourPopup is { IsOpen: true })
        {
            _colourPopup.IsOpen = false;
            return;
        }
        _colourPopup = BuildColourPopup(TextColourButton, ColourPalette, hex =>
        {
            _colourPopup!.IsOpen = false;
            TextColourSwatch.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            _ = (_sigEditor?.ExecAsync("foreColor", hex) ?? Task.CompletedTask);
        });
        _colourPopup.IsOpen = true;
        await Task.CompletedTask;
    }

    private async void HighlightColourButton_Click(object sender, RoutedEventArgs e)
    {
        if (_colourPopup is { IsOpen: true })
        {
            _colourPopup.IsOpen = false;
            return;
        }
        _colourPopup = BuildColourPopup(HighlightColourButton, HighlightPalette, hex =>
        {
            _colourPopup!.IsOpen = false;
            HighlightColourSwatch.Background = hex == "transparent"
                ? System.Windows.Media.Brushes.White
                : new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            _ = (_sigEditor?.ExecAsync("hiliteColor", hex) ?? Task.CompletedTask);
        });
        _colourPopup.IsOpen = true;
        await Task.CompletedTask;
    }

    private void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        SignatureLinkBar.Visibility = Visibility.Visible;
        SignatureLinkBox.Text = "https://";
        SignatureLinkBox.Focus();
        SignatureLinkBox.CaretIndex = SignatureLinkBox.Text.Length;
    }

    private async void InsertSignatureLink_Click(object sender, RoutedEventArgs e)
    {
        var url = SignatureLinkBox.Text.Trim();
        SignatureLinkBar.Visibility = Visibility.Collapsed;

        // Only ever produce links the viewer can safely follow — a javascript: or data: href
        // typed in here would otherwise be handed straight to the recipient.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps
                && parsed.Scheme != Uri.UriSchemeMailto))
        {
            ShowError("Enter a http, https or mailto address.");
            return;
        }

        await (_sigEditor?.ExecAsync("createLink", parsed.AbsoluteUri) ?? Task.CompletedTask);
    }

    private void CancelSignatureLink_Click(object sender, RoutedEventArgs e) => SignatureLinkBar.Visibility = Visibility.Collapsed;

    private void SignatureLinkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            InsertSignatureLink_Click(sender, new RoutedEventArgs());
        else if (e.Key == Key.Escape)
            CancelSignatureLink_Click(sender, new RoutedEventArgs());
    }

    // ---- Contacts -------------------------------------------------------------------------------

    /// <summary>Non-null while an existing contact is loaded into the form for editing — the
    /// email it was originally saved under, so a rename that also changes the email address still
    /// replaces the old entry instead of leaving a stale duplicate behind.</summary>
    private string? _editingContactEmail;

    private void CancelEditContact()
    {
        _editingContactEmail = null;
        AddContactButton.Content = "Add";
        ContactNameBox.Text = "";
        ContactEmailBox.Text = "";
        ContactsErrorText.Visibility = Visibility.Collapsed;
    }

    private void RefreshContactsUI()
    {
        // A fresh list each time, not the same _manualContacts reference re-assigned — WPF's
        // ItemsSource setter no-ops when the reference doesn't actually change, so re-pointing it
        // at the very list that was just mutated would silently leave the old rows on screen.
        ManualContactsList.ItemsSource = _manualContacts.ToList();
        ManualContactsEmptyHint.Visibility = _manualContacts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_mail is null)
        {
            LearnedContactsHint.Visibility = Visibility.Visible;
            LearnedContactsList.ItemsSource = null;
        }
        else
        {
            LearnedContactsHint.Visibility = Visibility.Collapsed;
            LearnedContactsList.ItemsSource = _mail.Contacts.ListAll();
        }
    }

    private void AddContactButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ContactNameBox.Text.Trim();
        var email = ContactEmailBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            ContactsErrorText.Text = "Enter a valid email address.";
            ContactsErrorText.Visibility = Visibility.Visible;
            return;
        }
        ContactsErrorText.Visibility = Visibility.Collapsed;

        // Editing a contact whose email address itself changed: drop the old entry it was saved
        // under, not just whatever's newly typed, so it's a rename rather than a stray duplicate.
        if (_editingContactEmail is { } editing)
            _manualContacts.RemoveAll(c => c.Email.Equals(editing, StringComparison.OrdinalIgnoreCase));
        _manualContacts.RemoveAll(c => c.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        _manualContacts.Add(new ManualContact { Name = string.IsNullOrWhiteSpace(name) ? email : name, Email = email });
        _manualContacts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        ManualContactsStore.Save(_manualContacts);
        RefreshContactsUI();

        CancelEditContact();
        ContactNameBox.Focus();
    }

    private void ContactEmailBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            AddContactButton_Click(sender, new RoutedEventArgs());
    }

    private void EditContactButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string email })
            return;
        var contact = _manualContacts.FirstOrDefault(c => c.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        if (contact is null)
            return;

        _editingContactEmail = contact.Email;
        ContactNameBox.Text = contact.Name;
        ContactEmailBox.Text = contact.Email;
        AddContactButton.Content = "Update";
        ContactsErrorText.Visibility = Visibility.Collapsed;
        ContactNameBox.Focus();
        ContactNameBox.SelectAll();
    }

    private void RemoveContactButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string email })
            return;

        var choice = EmailClient.UI.ConfirmDialog.Show(this, "Remove contact", $"Remove {email} from your contacts?",
            warningIcon: true, new EmailClient.UI.ConfirmChoice("Cancel"), new EmailClient.UI.ConfirmChoice("Remove", Destructive: true));
        if (choice != "Remove")
            return;

        _manualContacts.RemoveAll(c => c.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        ManualContactsStore.Save(_manualContacts);
        RefreshContactsUI();

        // The row being edited is the one that just got removed — drop out of edit mode instead
        // of leaving a stale "Update" button pointed at a contact that's no longer there.
        if (_editingContactEmail?.Equals(email, StringComparison.OrdinalIgnoreCase) == true)
            CancelEditContact();
    }

    // ---- Folders --------------------------------------------------------------------------------

    private sealed record FolderRowView(string Mailbox, string Name, Thickness IndentMargin, bool CanModify);

    // The same fixed folders the main window's sidebar shows in sample-data mode (see
    // MainWindow's _folderData) — shown here too, read-only, rather than this page claiming
    // there are no folders at all while the rest of the app is plainly showing some.
    private static readonly string[] MockFolderNames = ["Inbox", "Sent", "Drafts", "Archive", "Trash"];

    private async Task RefreshFoldersUIAsync()
    {
        if (_mail is null)
        {
            FoldersSignInHint.Visibility = Visibility.Visible;
            FoldersEditorPanel.Visibility = Visibility.Visible;
            CreateFolderRow.Visibility = Visibility.Collapsed;
            _currentFolders = [];
            FoldersList.ItemsSource = MockFolderNames
                .Select(name => new FolderRowView(name, name, new Thickness(0), CanModify: false))
                .ToList();
            return;
        }

        FoldersSignInHint.Visibility = Visibility.Collapsed;
        FoldersEditorPanel.Visibility = Visibility.Visible;
        CreateFolderRow.Visibility = Visibility.Visible;

        var special = await _mail.GetSpecialMailboxesAsync();
        var protectedMailboxes = new HashSet<string>(special.Values, StringComparer.OrdinalIgnoreCase);
        // IMAP's root mailbox is canonically named "INBOX" (RFC 3501) — every other special folder's
        // name (Sent, Drafts, ...) is just whatever the server happens to call it, already readable,
        // but INBOX's all-caps spelling would otherwise be the one folder in this list that doesn't
        // look like the rest.
        var inboxMailbox = special.GetValueOrDefault("Inbox");

        _currentFolders = (await _mail.ListFoldersAsync()).ToList();
        FoldersList.ItemsSource = _currentFolders
            .OrderBy(f => f.Mailbox, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FolderRowView(
                f.Mailbox,
                f.Mailbox.Equals(inboxMailbox, StringComparison.OrdinalIgnoreCase) ? "Inbox" : f.Name,
                new Thickness(f.Depth * 14, 0, 0, 0),
                !protectedMailboxes.Contains(f.Mailbox)))
            .ToList();
    }

    private void NewFolderNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CreateFolderButton_Click(sender, new RoutedEventArgs());
    }

    private async void CreateFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NewFolderNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || _mail is null)
            return;

        FoldersErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _mail.CreateFolderAsync(name);
            NewFolderNameBox.Text = "";
            await RefreshFoldersUIAsync();
            _onFoldersChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FoldersErrorText.Text = $"Couldn't create folder: {ex.Message}";
            FoldersErrorText.Visibility = Visibility.Visible;
        }
    }

    private void RenameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string mailbox })
            return;

        var folder = _currentFolders.FirstOrDefault(f => f.Mailbox == mailbox);
        _renamingMailbox = mailbox;
        RenameFolderBox.Text = folder?.Name ?? mailbox;
        RenameFolderBar.Visibility = Visibility.Visible;
        RenameFolderBox.Focus();
        RenameFolderBox.SelectAll();
    }

    private void RenameFolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ConfirmRenameFolderButton_Click(sender, new RoutedEventArgs());
        else if (e.Key == Key.Escape)
            CancelRenameFolderButton_Click(sender, new RoutedEventArgs());
    }

    private async void ConfirmRenameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_renamingMailbox is not { } mailbox || _mail is null)
            return;
        var newName = RenameFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
            return;

        RenameFolderBar.Visibility = Visibility.Collapsed;
        FoldersErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _mail.RenameFolderAsync(mailbox, newName);
            await RefreshFoldersUIAsync();
            _onFoldersChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FoldersErrorText.Text = $"Couldn't rename folder: {ex.Message}";
            FoldersErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _renamingMailbox = null;
        }
    }

    private void CancelRenameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        _renamingMailbox = null;
        RenameFolderBar.Visibility = Visibility.Collapsed;
    }

    private async void DeleteFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string mailbox } || _mail is null)
            return;

        var folder = _currentFolders.FirstOrDefault(f => f.Mailbox == mailbox);
        var choice = EmailClient.UI.ConfirmDialog.Show(this, "Delete folder",
            $"Delete the folder \"{folder?.Name ?? mailbox}\" and everything in it? This can't be undone.",
            warningIcon: true, new EmailClient.UI.ConfirmChoice("Cancel"), new EmailClient.UI.ConfirmChoice("Delete", Destructive: true));
        if (choice != "Delete")
            return;

        FoldersErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _mail.DeleteFolderAsync(mailbox);
            await RefreshFoldersUIAsync();
            _onFoldersChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FoldersErrorText.Text = $"Couldn't delete folder: {ex.Message}";
            FoldersErrorText.Visibility = Visibility.Visible;
        }
    }

    // ---- Settings -------------------------------------------------------------------------------

    private sealed record AccountRowView(string Email, string Identity, Visibility IsActiveVisibility, Visibility SwitchVisibility);

    private void RefreshSettingsUI()
    {
        UndoSendSecondsBox.Text = _settings.ClampedUndoSendSeconds.ToString();

        NotificationsCheck.IsChecked = _settings.NotificationsEnabled;
        CloseToTrayCheck.IsChecked = _settings.CloseToTray;
        // Read from the registry every time this page opens: the entry can be turned off from Task
        // Manager's Startup tab without the app being involved, so a remembered value would lie.
        StartWithWindowsCheck.IsChecked = StartupRegistration.IsEnabled;
        StartWithWindowsCheck.IsEnabled = StartupRegistration.IsAvailable;
        if (!StartupRegistration.IsAvailable)
            StartWithWindowsHint.Text = "Only available when running the installed app, not under `dotnet run`.";

        var accounts = AccountStore.Load().Accounts;
        AccountsList.ItemsSource = accounts.Select(a =>
        {
            var isActive = a.Email.Equals(_activeEmail, StringComparison.OrdinalIgnoreCase);
            return new AccountRowView(a.Email, a.Identity,
                isActive ? Visibility.Visible : Visibility.Collapsed,
                isActive ? Visibility.Collapsed : Visibility.Visible);
        }).ToList();
        NoAccountsHint.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddAccountButton_Click(object sender, RoutedEventArgs e)
    {
        EmailBox.Text = "@iitb.ac.in";
        DisplayNameBox.Text = "";
        PasswordBox.Password = "";
        ImapHostBox.Text = "imap.iitb.ac.in";
        ImapPortBox.Text = "993";
        SmtpHostBox.Text = "smtp-auth.iitb.ac.in";
        SmtpPortBox.Text = "587";
        AdvancedPanel.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        ShowProfilePage();
        ShowProfileSignInForm();
        EmailBox.Focus();
    }

    private void SwitchAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string email })
            return;
        var account = AccountStore.Load().Accounts.FirstOrDefault(a => a.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        if (account is null || _onSwitchAccount is null)
            return;

        _onSwitchAccount(account);
        Close();
    }

    private void RemoveAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string email })
            return;

        // One click, silently gone, with no way back short of re-entering the password and
        // server settings from scratch — worth the extra click to confirm, the same way Delete
        // Folder already does.
        var choice = EmailClient.UI.ConfirmDialog.Show(this, "Remove account",
            $"Remove {email} from this app? You'll need to sign in again to add it back.",
            warningIcon: true, new EmailClient.UI.ConfirmChoice("Cancel"), new EmailClient.UI.ConfirmChoice("Remove", Destructive: true));
        if (choice != "Remove")
            return;

        var account = AccountStore.Load().Accounts.FirstOrDefault(a => a.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        account?.Remove();
        RefreshSettingsUI();
    }

    private void SaveUndoSendSeconds()
    {
        if (int.TryParse(UndoSendSecondsBox.Text, out var seconds))
        {
            _settings.UndoSendSeconds = Math.Clamp(seconds, 1, 60);
            _settings.Save();
        }
        UndoSendSecondsBox.Text = _settings.ClampedUndoSendSeconds.ToString();
    }

    private void UndoSendSecondsBox_LostFocus(object sender, RoutedEventArgs e) => SaveUndoSendSeconds();

    private void UndoSendSecondsBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SaveUndoSendSeconds();
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e) => App.OpenLog();

    private void NotificationsCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.NotificationsEnabled = NotificationsCheck.IsChecked == true;
        _settings.Save();
    }

    private void CloseToTrayCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _settings.Save();
    }

    private const string StartWithWindowsHintText =
        "Starts minimised to the tray, so new mail is announced from sign-in onwards.";

    private void StartWithWindowsCheck_Click(object sender, RoutedEventArgs e)
    {
        // The registry is the source of truth, and writing it can fail (a policy-managed Run key),
        // so the checkbox re-reads what the registry actually says rather than trusting the click.
        var wanted = StartWithWindowsCheck.IsChecked == true;
        var actual = StartupRegistration.Set(wanted);

        StartWithWindowsCheck.IsChecked = actual;
        StartWithWindowsHint.Text = actual == wanted
            ? StartWithWindowsHintText
            : "Windows wouldn't accept the change — startup entries can be blocked by policy or turned off under Task Manager's Startup apps.";
    }

}

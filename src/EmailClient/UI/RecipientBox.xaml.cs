using System.Windows;
using System.Windows.Media;
// UseWindowsForms puts System.Windows.Forms types in scope project-wide, colliding with these.
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using WrapPanel = System.Windows.Controls.WrapPanel;
using Border = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Cursors = System.Windows.Input.Cursors;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using AutomationProperties = System.Windows.Automation.AutomationProperties;

namespace EmailClient.UI;

/// <summary>
/// A recipient chip field — see the XAML file's own comment for the "why" and the overall shape.
/// <see cref="Text"/> is a drop-in replacement for a plain TextBox's own Text property (same
/// "Name &lt;address&gt;, address2" wire format in and out), so nothing outside this control needs
/// to know chips exist at all — the compose window's own validation, autosave and send logic all
/// keep reading/writing <c>ToBox.Text</c> exactly as before.
/// </summary>
public partial class RecipientBox : UserControl
{
    private readonly record struct Recipient(string DisplayName, string Address)
    {
        public bool HasName =>
            !string.IsNullOrWhiteSpace(DisplayName) && !DisplayName.Equals(Address, StringComparison.OrdinalIgnoreCase);
    }

    private readonly List<Recipient> _recipients = [];
    private readonly TextBox _input;
    private bool _updatingInput;

    public RecipientBox()
    {
        InitializeComponent();

        _input = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            MinWidth = 60,
            Margin = new Thickness(2, 3, 2, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.IBeam,
        };
        _input.PreviewKeyDown += Input_PreviewKeyDown;
        _input.TextChanged += (_, _) =>
        {
            if (!_updatingInput)
                TextChanged?.Invoke(this, EventArgs.Empty);
        };
        _input.GotFocus += (s, e) => GotFocus?.Invoke(this, e);
        _input.LostFocus += (s, e) =>
        {
            CommitTyped();
            LostFocus?.Invoke(this, e);
        };
        Chips.Items.Add(_input);
    }

    /// <summary>The live-typing field — what the recipient autocomplete popup anchors itself to,
    /// and where focus actually needs to go on click/GotFocus, since the control itself isn't
    /// focusable (nothing to put a caret in at the UserControl level).</summary>
    public TextBox InputTextBox => _input;

    /// <summary>Set by the owning window while its own suggestion popup is open for this box, so
    /// Enter/Tab/comma here defer to that popup (pick the highlighted suggestion) instead of this
    /// control also trying to commit the raw typed text as its own chip at the same time.</summary>
    public bool SuppressAutoCommit { get; set; }

    /// <summary>Fires on any change a caller might care about: a chip added or removed, or the
    /// in-progress text changing — the same occasions a plain TextBox's own TextChanged would
    /// have covered, since this is what drives the recipient autocomplete's search-as-you-type.</summary>
    public event EventHandler? TextChanged;

    public new event RoutedEventHandler? GotFocus;
    public new event RoutedEventHandler? LostFocus;

    /// <summary>
    /// The wire-format text — "Name &lt;address&gt;, address2, ..." — committed chips plus
    /// whatever's still being typed. Setting it replaces every chip, parsed via MimeKit rather
    /// than a hand-rolled comma-splitter so a quoted display name containing its own comma (rare,
    /// but valid RFC 5322) doesn't get cut in half.
    /// </summary>
    public string Text
    {
        get
        {
            var parts = _recipients.Select(FormatForWire).ToList();
            if (!string.IsNullOrWhiteSpace(_input.Text))
                parts.Add(_input.Text.Trim());
            return string.Join(", ", parts);
        }
        set
        {
            _recipients.Clear();
            foreach (var (name, address) in ParseAddresses(value ?? ""))
                _recipients.Add(new Recipient(name, address));
            _updatingInput = true;
            try { _input.Text = ""; }
            finally { _updatingInput = false; }
            Rebuild();
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Clear() => Text = "";

    /// <summary>Commits one recipient as a chip directly — used by the autocomplete dropdown and
    /// the address-book picker, both of which already have a real (name, address) pair in hand
    /// rather than text that needs parsing.</summary>
    public void AddRecipient(string displayName, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;
        if (_recipients.Any(r => r.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
        {
            // Already there — still clear whatever was typed toward it, matching what committing
            // normally does, rather than leaving a stale partial token sitting in the input.
            ClearInput();
            return;
        }

        _recipients.Add(new Recipient(displayName, address));
        ClearInput();
        Rebuild();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearInput()
    {
        _updatingInput = true;
        try { _input.Text = ""; }
        finally { _updatingInput = false; }
    }

    private static string FormatForWire(Recipient r) =>
        r.HasName ? $"{r.DisplayName} <{r.Address}>" : r.Address;

    private static IEnumerable<(string Name, string Address)> ParseAddresses(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !MimeKit.InternetAddressList.TryParse(text, out var list))
            yield break;

        foreach (var mailbox in list.Mailboxes)
            yield return (mailbox.Name ?? "", mailbox.Address);
    }

    /// <summary>Turns whatever's currently typed into one or more chips (a pasted "a@x, b@x" all
    /// commits at once, via the same parser). Text that doesn't parse as an address at all — a
    /// stray word, an incomplete address — is left in the input rather than silently dropped, so
    /// the user notices it's still there instead of it vanishing.</summary>
    private void CommitTyped()
    {
        var typed = _input.Text.Trim().TrimEnd(',').Trim();
        if (string.IsNullOrEmpty(typed))
            return;

        var parsed = ParseAddresses(typed).ToList();
        if (parsed.Count == 0)
            return;

        foreach (var (name, address) in parsed)
        {
            if (!_recipients.Any(r => r.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
                _recipients.Add(new Recipient(name, address));
        }
        ClearInput();
        Rebuild();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SuppressAutoCommit)
            return; // the owning window's suggestion popup gets this keystroke instead

        if ((e.Key is Key.Enter or Key.Tab or Key.OemComma) && _input.Text.Trim().Length > 0)
        {
            var wasTab = e.Key == Key.Tab;
            CommitTyped();
            // Tab still needs to actually move focus on to the next field; Enter/comma are fully
            // ours, since letting Enter through would try to "activate" whatever's the dialog's
            // default button instead.
            e.Handled = !wasTab;
            return;
        }

        if (e.Key == Key.Back && _input.Text.Length == 0 && _recipients.Count > 0)
        {
            _recipients.RemoveAt(_recipients.Count - 1);
            Rebuild();
            TextChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking anywhere in the field's empty space (not a specific chip's own remove button)
        // should focus the input, same as clicking the padding of a plain TextBox would.
        if (e.OriginalSource == Root || e.OriginalSource is WrapPanel)
        {
            _input.Focus();
            _input.CaretIndex = _input.Text.Length;
        }
    }

    private void Rebuild()
    {
        var caretWasAt = _input.CaretIndex;
        Chips.Items.Clear();
        foreach (var r in _recipients)
            Chips.Items.Add(BuildChip(r));
        Chips.Items.Add(_input);
        _input.CaretIndex = Math.Min(caretWasAt, _input.Text.Length);
    }

    private Border BuildChip(Recipient r)
    {
        var label = new TextBlock
        {
            Text = r.HasName ? r.DisplayName : r.Address,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
        };

        var remove = new TextBlock
        {
            Text = "×",
            FontSize = 13,
            Margin = new Thickness(7, 0, 0, 0),
            Foreground = (Brush)FindResource("TextMuted"),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.SetValue(AutomationProperties.NameProperty, $"Remove {(r.HasName ? r.DisplayName : r.Address)}");
        remove.MouseLeftButtonUp += (_, e) =>
        {
            _recipients.Remove(r);
            Rebuild();
            TextChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true; // don't also let Root_MouseLeftButtonDown steal focus back to input
        };

        return new Border
        {
            Background = (Brush)FindResource("SurfaceHover"),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(9, 4, 8, 4),
            Margin = new Thickness(0, 2, 5, 2),
            ToolTip = r.HasName ? r.Address : null,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, remove } },
        };
    }
}

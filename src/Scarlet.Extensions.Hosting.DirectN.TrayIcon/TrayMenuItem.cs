namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon;

/// <summary>
/// An item of a <see cref="TrayMenu"/>: a command, a submenu (when <see cref="Items"/> isn't empty) or a separator.
/// </summary>
public class TrayMenuItem
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TrayMenuItem"/> class.
    /// </summary>
    /// <param name="text">The text. An <c>&amp;</c> marks the mnemonic (e.g. <c>"E&amp;xit"</c>).</param>
    /// <param name="click">An optional <see cref="Click"/> handler.</param>
    public TrayMenuItem(string text, EventHandler? click = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        if (click is not null)
        {
            Click += click;
        }
    }

    private TrayMenuItem()
    {
        Text = string.Empty;
        IsSeparator = true;
    }

    /// <summary>
    /// Raised on the UI thread when the item is chosen, after the menu has closed.
    /// </summary>
    public event EventHandler? Click;

    /// <summary>
    /// Gets or sets the text. An <c>&amp;</c> marks the mnemonic; use <c>&amp;&amp;</c> for a literal ampersand.
    /// </summary>
    public string Text { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item can be chosen. Default: <see langword="true"/>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the item shows a check mark.
    /// </summary>
    public bool Checked { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item is the menu's default item (shown in bold).
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets an arbitrary object associated with the item.
    /// </summary>
    public object? Tag { get; set; }

    /// <summary>
    /// Gets a value indicating whether the item is a separator (<see cref="Separator"/>).
    /// </summary>
    public bool IsSeparator { get; }

    /// <summary>
    /// Gets the submenu items. When not empty, the item opens a submenu instead of raising <see cref="Click"/>.
    /// </summary>
    public IList<TrayMenuItem> Items { get; } = [];

    /// <summary>
    /// Creates a separator line.
    /// </summary>
    /// <returns>A separator item.</returns>
    public static TrayMenuItem Separator() => new();

    /// <summary>
    /// Raises <see cref="Click"/>.
    /// </summary>
    public void PerformClick() => OnClick(EventArgs.Empty);

    /// <summary>
    /// Raises <see cref="Click"/>.
    /// </summary>
    /// <param name="e">The event data.</param>
    protected virtual void OnClick(EventArgs e) => Click?.Invoke(this, e);
}

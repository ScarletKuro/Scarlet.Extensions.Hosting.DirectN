namespace Scarlet.Extensions.Hosting.DirectN;

/// <summary>
/// Options for <see cref="DirectNLifetime"/>.
/// </summary>
public sealed class DirectNLifetimeOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether host lifetime status messages (started, stopping, ...) are suppressed.
    /// </summary>
    public bool SuppressStatusMessages { get; set; }
}

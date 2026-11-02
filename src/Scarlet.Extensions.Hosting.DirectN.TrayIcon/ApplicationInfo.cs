using System.Diagnostics;
using System.Reflection;

namespace Scarlet.Extensions.Hosting.DirectN.TrayIcon;

/// <summary>
/// Native AOT compatible replacement for WinForms' <c>Application.ProductVersion</c> and <c>Application.ProductName</c>.
/// </summary>
public static class ApplicationInfo
{
    private static readonly Lazy<string> s_productVersion = new(GetProductVersion);
    private static readonly Lazy<string> s_productName = new(GetProductName);

    /// <summary>
    /// Gets the product version: the entry assembly's <see cref="AssemblyInformationalVersionAttribute"/> (as WinForms does, so it
    /// may carry a <c>+commit</c> suffix unless <c>IncludeSourceRevisionInInformationalVersion</c> is off), else the file version of
    /// the process executable, else the entry assembly version.
    /// </summary>
    public static string ProductVersion => s_productVersion.Value;

    /// <summary>
    /// Gets the product name: the entry assembly's <see cref="AssemblyProductAttribute"/>, else the executable's file name.
    /// </summary>
    public static string ProductName => s_productName.Value;

    private static string GetProductVersion()
    {
        var entry = Assembly.GetEntryAssembly();
        var informational = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            return informational;
        }

        if (Environment.ProcessPath is { } path && FileVersionInfo.GetVersionInfo(path).ProductVersion is { Length: > 0 } fileVersion)
        {
            return fileVersion;
        }

        return entry?.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    private static string GetProductName()
    {
        var product = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        if (!string.IsNullOrEmpty(product))
        {
            return product;
        }

        return Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
    }
}

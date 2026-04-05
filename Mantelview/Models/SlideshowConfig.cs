using System;
using System.Collections.Generic;
using System.IO;
using Mantelview.Services;

namespace Mantelview.Models;

/// <summary>
/// Defines the slideshow playback order.
/// </summary>
public enum PlaybackMode
{
    InOrder,
    Random,
}

/// <summary>
/// Stores launcher-selected slideshow settings.
/// </summary>
public sealed class SlideshowConfig
{
    public const double MinImageDurationSec = 1.0;
    public const double MaxImageDurationSec = 300.0;
    public const double MinEinkImageDurationSec = 3.0;
    public const double MinTransitionDurationSec = 0.5;
    public const double MaxTransitionDurationSec = 5.0;
    public const string BlackBackgroundColor = "black";
    public const string WhiteBackgroundColor = "white";

    private static readonly string[] LinuxBlockedPrefixes =
    [
        "/proc",
        "/sys",
        "/dev",
        "/etc",
    ];

    private static readonly string[] WindowsBlockedPrefixes =
    [
        @"C:\Windows",
        @"C:\Program Files",
    ];

    private static readonly HashSet<string> AllowedUnsafeFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "bmp",
        "gif",
    };

    /// <summary>
    /// Folder to scan for slideshow images. Defaults to an empty string.
    /// </summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Playback order. Defaults to <see cref="Mantelview.Models.PlaybackMode.InOrder"/>.
    /// </summary>
    public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.InOrder;

    /// <summary>
    /// Seconds each image remains on screen. Defaults to 5.0 seconds.
    /// </summary>
    public double ImageDurationSec { get; set; } = 5.0;

    /// <summary>
    /// Seconds reserved for transitions. Defaults to 1.0 second.
    /// </summary>
    public double TransitionDurationSec { get; set; } = 1.0;

    /// <summary>
    /// Enables e-ink friendly behavior. Defaults to false.
    /// </summary>
    public bool IsEinkMode { get; set; }

    /// <summary>
    /// Canonical transition class names eligible for random selection. Defaults to Crossfade.
    /// </summary>
    public string[] TransitionEffects { get; set; } = [TransitionRegistry.CrossfadeName];

    /// <summary>
    /// Optional opt-in list of additional image formats permitted for discovery.
    /// </summary>
    public string[]? UnsafeFormats { get; set; }

    /// <summary>
    /// Keeps the slideshow window above other windows. Defaults to false.
    /// </summary>
    public bool Topmost { get; set; }

    /// <summary>
    /// Optional replacement allowlist for keys that should not interrupt the slideshow.
    /// </summary>
    public string[]? IgnoredKeys { get; set; }

    /// <summary>
    /// Optional IPC secret required to prefix Linux socket commands.
    /// </summary>
    public string? IpcSecret { get; set; }

    /// <summary>
    /// Optional advanced cap on the number of ordered images loaded into the catalog.
    /// </summary>
    public int? MaxCatalogImages { get; set; }

    /// <summary>
    /// Optional background color shown behind letterboxed or pillarboxed images.
    /// </summary>
    public string? BackgroundColor { get; set; }

    /// <summary>
    /// Returns the effective image duration after applying runtime constraints.
    /// </summary>
    public double GetEffectiveImageDurationSec()
    {
        return NormalizeImageDuration(ImageDurationSec, IsEinkMode);
    }

    /// <summary>
    /// Resolves a folder path to its absolute form. Returns false for null, empty, or invalid paths.
    /// </summary>
    public static bool TryResolveFolderPath(string? path, out string? resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            resolvedPath = null;
            return false;
        }

        try
        {
            resolvedPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            resolvedPath = null;
            return false;
        }
    }

    /// <summary>
    /// Returns true when the supplied folder path resolves to an OS-protected location.
    /// </summary>
    public static bool IsBlockedPath(string path)
    {
        if (!TryResolveFolderPath(path, out var resolvedPath) || resolvedPath is null)
        {
            return false;
        }

        return OperatingSystem.IsWindows()
            ? MatchesBlockedPrefix(resolvedPath, WindowsBlockedPrefixes, StringComparison.OrdinalIgnoreCase)
            : MatchesBlockedPrefix(resolvedPath, LinuxBlockedPrefixes, StringComparison.Ordinal);
    }

    public static double NormalizeImageDuration(double value, bool isEinkMode)
    {
        var clampedValue = Math.Clamp(value, MinImageDurationSec, MaxImageDurationSec);
        return isEinkMode
            ? Math.Max(clampedValue, MinEinkImageDurationSec)
            : clampedValue;
    }

    public static string[]? NormalizeUnsafeFormats(IEnumerable<string>? formats)
    {
        if (formats is null)
        {
            return null;
        }

        var normalizedFormats = new List<string>();
        var seenFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var format in formats)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return null;
            }

            var normalizedFormat = format.Trim().ToLowerInvariant();
            if (!AllowedUnsafeFormats.Contains(normalizedFormat))
            {
                return null;
            }

            if (seenFormats.Add(normalizedFormat))
            {
                normalizedFormats.Add(normalizedFormat);
            }
        }

        return normalizedFormats.Count > 0
            ? [.. normalizedFormats]
            : null;
    }

    public static int? NormalizeMaxCatalogImages(int? value)
    {
        return value is > 0
            ? value
            : null;
    }

    public static string? NormalizeBackgroundColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            BlackBackgroundColor => BlackBackgroundColor,
            WhiteBackgroundColor => WhiteBackgroundColor,
            _ => null,
        };
    }

    public static string ResolveBackgroundColor(string? value)
    {
        return NormalizeBackgroundColor(value) ?? BlackBackgroundColor;
    }

    private static bool MatchesBlockedPrefix(string resolvedPath, string[] blockedPrefixes, StringComparison comparison)
    {
        foreach (var blockedPrefix in blockedPrefixes)
        {
            if (resolvedPath.Equals(blockedPrefix, comparison))
            {
                return true;
            }

            if (resolvedPath.StartsWith(blockedPrefix + Path.DirectorySeparatorChar, comparison))
            {
                return true;
            }
        }

        return false;
    }
}

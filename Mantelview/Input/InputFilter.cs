using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace Mantelview.Input;

public sealed class InputFilter
{
    private static readonly HashSet<Key> DefaultIgnoredKeys =
    [
        Key.F1,
        Key.F2,
        Key.F3,
        Key.F4,
        Key.F5,
        Key.F6,
        Key.F7,
        Key.F8,
        Key.F9,
        Key.F10,
        Key.F11,
        Key.F12,
        Key.F13,
        Key.F14,
        Key.F15,
        Key.F16,
        Key.F17,
        Key.F18,
        Key.F19,
        Key.F20,
        Key.F21,
        Key.F22,
        Key.F23,
        Key.F24,
        Key.NumLock,
        Key.CapsLock,
        Key.Scroll,
        Key.LeftShift,
        Key.RightShift,
        Key.LeftCtrl,
        Key.RightCtrl,
        Key.LeftAlt,
        Key.RightAlt,
        Key.LWin,
        Key.RWin,
        Key.MediaPlayPause,
        Key.MediaStop,
        Key.MediaNextTrack,
        Key.MediaPreviousTrack,
        Key.VolumeMute,
        Key.VolumeUp,
        Key.VolumeDown,
        Key.PrintScreen,
        Key.Pause,
        Key.Insert,
    ];

    private readonly HashSet<Key> _ignoredKeys;

    public InputFilter(IReadOnlyList<string>? configuredKeys)
    {
        _ignoredKeys = ResolveIgnoredKeys(configuredKeys);
    }

    public bool ShouldInterrupt(KeyEventArgs e)
    {
        return !_ignoredKeys.Contains(e.Key);
    }

    private static HashSet<Key> ResolveIgnoredKeys(IReadOnlyList<string>? configuredKeys)
    {
        if (configuredKeys is null)
        {
            return [.. DefaultIgnoredKeys];
        }

        var parsedKeys = new HashSet<Key>();
        foreach (var configuredKey in configuredKeys)
        {
            if (Enum.TryParse<Key>(configuredKey, ignoreCase: true, out var parsedKey))
            {
                parsedKeys.Add(parsedKey);
            }
        }

        return parsedKeys.Count > 0 ? parsedKeys : [.. DefaultIgnoredKeys];
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Input;
using Mantelview.Models;

namespace Mantelview.Services;

public static class ConfigService
{
    private const string ConfigFileName = "MantelviewConfig.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static PersistedConfigState CreateDefaultState()
    {
        return new PersistedConfigState(new SlideshowConfig(), null);
    }

    public static PersistedConfigState Load()
    {
        var config = new SlideshowConfig();
        var configPath = GetConfigPath();

        if (!File.Exists(configPath))
        {
            return new PersistedConfigState(config, null);
        }

        try
        {
            using var stream = File.OpenRead(configPath);
            var rootNode = JsonNode.Parse(stream);

            if (rootNode is not JsonObject rootObject)
            {
                return new PersistedConfigState(config, null);
            }

            ApplyValidatedConfig(rootObject, config);

            var preservedRoot = (JsonObject)rootObject.DeepClone();
            SanitizeOpaqueFields(preservedRoot);

            return new PersistedConfigState(config, preservedRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new PersistedConfigState(config, null);
        }
    }

    public static void Save(SlideshowConfig config, PersistedConfigState state)
    {
        var root = state.PreservedRoot is null
            ? new JsonObject()
            : (JsonObject)state.PreservedRoot.DeepClone();

        root[nameof(SlideshowConfig.FolderPath)] = NormalizeFolderPath(config.FolderPath);
        root[nameof(SlideshowConfig.PlaybackMode)] = NormalizePlaybackMode(config.PlaybackMode).ToString();
        root[nameof(SlideshowConfig.ImageDurationSec)] = SlideshowConfig.NormalizeImageDuration(config.ImageDurationSec, config.IsEinkMode);
        root[nameof(SlideshowConfig.TransitionDurationSec)] = NormalizeTransitionDuration(config.TransitionDurationSec);
        root[nameof(SlideshowConfig.IsEinkMode)] = config.IsEinkMode;
        root[nameof(SlideshowConfig.Topmost)] = config.Topmost;
        WriteUnsafeFormats(root, config.UnsafeFormats);

        try
        {
            File.WriteAllText(GetConfigPath(), root.ToJsonString(SerializerOptions));
            state.PreservedRoot = root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ApplyValidatedConfig(JsonObject root, SlideshowConfig config)
    {
        if (TryGetValidatedFolderPath(root, nameof(SlideshowConfig.FolderPath), out var folderPath))
        {
            config.FolderPath = folderPath;
        }

        if (TryGetPlaybackMode(root, nameof(SlideshowConfig.PlaybackMode), out var playbackMode))
        {
            config.PlaybackMode = playbackMode;
        }

        if (TryGetDoubleInRange(root, nameof(SlideshowConfig.ImageDurationSec), min: 1.0, max: 300.0, out var imageDuration))
        {
            config.ImageDurationSec = imageDuration;
        }

        if (TryGetDoubleInRange(root, nameof(SlideshowConfig.TransitionDurationSec), min: 0.5, max: 5.0, out var transitionDuration))
        {
            config.TransitionDurationSec = transitionDuration;
        }

        if (TryGetBool(root, nameof(SlideshowConfig.IsEinkMode), out var isEinkMode))
        {
            config.IsEinkMode = isEinkMode;
        }

        config.ImageDurationSec = SlideshowConfig.NormalizeImageDuration(config.ImageDurationSec, config.IsEinkMode);

        if (TryGetBool(root, nameof(SlideshowConfig.Topmost), out var topmost))
        {
            config.Topmost = topmost;
        }

        if (TryGetValidatedUnsafeFormats(root, nameof(SlideshowConfig.UnsafeFormats), out var unsafeFormats))
        {
            config.UnsafeFormats = unsafeFormats;
        }

        if (TryGetValidatedIgnoredKeys(root, nameof(SlideshowConfig.IgnoredKeys), out var ignoredKeys))
        {
            config.IgnoredKeys = ignoredKeys;
        }

        if (TryGetString(root, "IpcSecret", out var ipcSecret) && !string.IsNullOrWhiteSpace(ipcSecret))
        {
            config.IpcSecret = ipcSecret;
        }
    }

    private static void SanitizeOpaqueFields(JsonObject root)
    {
        if (root.ContainsKey("UnsafeFormats") && !HasValidUnsafeFormats(root["UnsafeFormats"]))
        {
            root.Remove("UnsafeFormats");
        }

        if (root.ContainsKey("IpcSecret") && !TryGetString(root, "IpcSecret", out _))
        {
            root.Remove("IpcSecret");
        }
    }

    private static bool TryGetValidatedFolderPath(JsonObject root, string propertyName, out string folderPath)
    {
        folderPath = string.Empty;

        if (!TryGetString(root, propertyName, out var rawPath))
        {
            return false;
        }

        if (!SlideshowConfig.TryResolveFolderPath(rawPath, out var resolvedPath) ||
            string.IsNullOrWhiteSpace(resolvedPath) ||
            SlideshowConfig.IsBlockedPath(resolvedPath))
        {
            return false;
        }

        folderPath = resolvedPath;
        return true;
    }

    private static bool TryGetPlaybackMode(JsonObject root, string propertyName, out PlaybackMode playbackMode)
    {
        playbackMode = default;

        if (!root.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var stringValue) &&
                Enum.TryParse(stringValue, ignoreCase: true, out PlaybackMode parsedMode) &&
                Enum.IsDefined(parsedMode))
            {
                playbackMode = parsedMode;
                return true;
            }

            if (value.TryGetValue<int>(out var numericValue) &&
                Enum.IsDefined(typeof(PlaybackMode), numericValue))
            {
                playbackMode = (PlaybackMode)numericValue;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetDoubleInRange(JsonObject root, string propertyName, double min, double max, out double value)
    {
        value = default;

        if (!root.TryGetPropertyValue(propertyName, out var node) ||
            node is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<double>(out var parsedValue) ||
            double.IsNaN(parsedValue) ||
            double.IsInfinity(parsedValue) ||
            parsedValue < min ||
            parsedValue > max)
        {
            return false;
        }

        value = parsedValue;
        return true;
    }

    private static bool TryGetBool(JsonObject root, string propertyName, out bool value)
    {
        value = default;

        return root.TryGetPropertyValue(propertyName, out var node) &&
            node is JsonValue jsonValue &&
            jsonValue.TryGetValue<bool>(out value);
    }

    private static bool TryGetString(JsonObject root, string propertyName, out string value)
    {
        value = string.Empty;

        if (!root.TryGetPropertyValue(propertyName, out var node) ||
            node is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<string>(out var parsedValue) ||
            parsedValue is null)
        {
            return false;
        }

        value = parsedValue;
        return true;
    }

    private static bool TryGetValidatedIgnoredKeys(JsonObject root, string propertyName, out string[] ignoredKeys)
    {
        ignoredKeys = [];

        if (!root.TryGetPropertyValue(propertyName, out var node) || node is not JsonArray array)
        {
            return false;
        }

        var validatedKeys = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var configuredKey) ||
                configuredKey is null ||
                !Enum.TryParse<Key>(configuredKey, ignoreCase: true, out var parsedKey) ||
                !Enum.IsDefined(parsedKey))
            {
                continue;
            }

            validatedKeys.Add(parsedKey.ToString());
        }

        ignoredKeys = [.. validatedKeys];
        return true;
    }

    private static bool TryGetValidatedUnsafeFormats(JsonObject root, string propertyName, out string[] unsafeFormats)
    {
        unsafeFormats = [];

        if (!root.TryGetPropertyValue(propertyName, out var node) || node is not JsonArray array)
        {
            return false;
        }

        var configuredFormats = new string[array.Count];
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonValue value ||
                !value.TryGetValue<string>(out var configuredFormat) ||
                configuredFormat is null)
            {
                return false;
            }

            configuredFormats[index] = configuredFormat;
        }

        var normalizedFormats = SlideshowConfig.NormalizeUnsafeFormats(configuredFormats);
        if (normalizedFormats is null)
        {
            return false;
        }

        unsafeFormats = normalizedFormats;
        return true;
    }

    private static bool HasValidUnsafeFormats(JsonNode? node)
    {
        return node is JsonArray array
            && TryNormalizeUnsafeFormats(array, out _);
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        return folderPath ?? string.Empty;
    }

    private static PlaybackMode NormalizePlaybackMode(PlaybackMode value)
    {
        return Enum.IsDefined(value)
            ? value
            : PlaybackMode.InOrder;
    }

    private static double NormalizeImageDuration(double value)
    {
        return value is >= SlideshowConfig.MinImageDurationSec and <= SlideshowConfig.MaxImageDurationSec ? value : 5.0;
    }

    private static double NormalizeTransitionDuration(double value)
    {
        return value is >= SlideshowConfig.MinTransitionDurationSec and <= SlideshowConfig.MaxTransitionDurationSec ? value : 1.0;
    }

    private static string GetConfigPath()
    {
        return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        return options;
    }

    private static bool TryNormalizeUnsafeFormats(JsonArray array, out string[] normalizedFormats)
    {
        normalizedFormats = [];

        var configuredFormats = new string[array.Count];
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonValue value ||
                !value.TryGetValue<string>(out var configuredFormat) ||
                configuredFormat is null)
            {
                return false;
            }

            configuredFormats[index] = configuredFormat;
        }

        var validatedFormats = SlideshowConfig.NormalizeUnsafeFormats(configuredFormats);
        if (validatedFormats is null)
        {
            return false;
        }

        normalizedFormats = validatedFormats;
        return true;
    }

    private static void WriteUnsafeFormats(JsonObject root, string[]? unsafeFormats)
    {
        var normalizedFormats = SlideshowConfig.NormalizeUnsafeFormats(unsafeFormats);
        if (normalizedFormats is null)
        {
            root.Remove(nameof(SlideshowConfig.UnsafeFormats));
            return;
        }

        var array = new JsonArray();
        foreach (var format in normalizedFormats)
        {
            array.Add(format);
        }

        root[nameof(SlideshowConfig.UnsafeFormats)] = array;
    }
}

public sealed class PersistedConfigState
{
    internal PersistedConfigState(SlideshowConfig config, JsonObject? preservedRoot)
    {
        Config = config;
        PreservedRoot = preservedRoot;
    }

    public SlideshowConfig Config { get; }

    internal JsonObject? PreservedRoot { get; set; }
}

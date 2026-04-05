using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Input;
using Mantelview.Models;

namespace Mantelview.Services;

public static class ConfigService
{
    private const string ConfigFileName = "MantelviewConfig.json";
    private const string BackupFileName = ConfigFileName + ".bak";
    private const int MaxConfigFileBytes = 64 * 1024;
    private const string RecoveryWarningMessage = "Config recovered: Verify IgnoredKeys and IpcSecret after exit.";
    private const string OversizedConfigWarningMessage = "Oversized config file cannot be loaded.";

    private static readonly string[] RecoverableProperties =
    [
        nameof(SlideshowConfig.FolderPath),
        nameof(SlideshowConfig.PlaybackMode),
        nameof(SlideshowConfig.ImageDurationSec),
        nameof(SlideshowConfig.TransitionDurationSec),
        nameof(SlideshowConfig.IsEinkMode),
        nameof(SlideshowConfig.TransitionEffects),
        nameof(SlideshowConfig.UnsafeFormats),
        nameof(SlideshowConfig.Topmost),
        nameof(SlideshowConfig.IgnoredKeys),
        nameof(SlideshowConfig.IpcSecret),
        nameof(SlideshowConfig.MaxCatalogImages),
        nameof(SlideshowConfig.BackgroundColor),
    ];

    private static readonly string[] RecoverableStringArrayProperties =
    [
        nameof(SlideshowConfig.TransitionEffects),
        nameof(SlideshowConfig.UnsafeFormats),
        nameof(SlideshowConfig.IgnoredKeys),
    ];

    private static readonly Regex MissingPropertyCommaAfterStringRegex = CreateRecoveryRegex(
        "(\"(?:[^\"\\\\]|\\\\.)*\")(\\s*)(?=\"[A-Za-z_][A-Za-z0-9_]*\"\\s*:)");
    private static readonly Regex MissingPropertyCommaAfterLiteralRegex = CreateRecoveryRegex(
        "(\\btrue\\b|\\bfalse\\b|\\bnull\\b|-?(?:0|[1-9]\\d*)(?:\\.\\d+)?(?:[eE][+\\-]?\\d+)?)(\\s*)(?=\"[A-Za-z_][A-Za-z0-9_]*\"\\s*:)");
    private static readonly Regex MissingPropertyCommaAfterArrayRegex = CreateRecoveryRegex(
        "(\\])(\\s*)(?=\"[A-Za-z_][A-Za-z0-9_]*\"\\s*:)");

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
            var configFileInfo = new FileInfo(configPath);
            if (configFileInfo.Length > MaxConfigFileBytes)
            {
                return new PersistedConfigState(config, null, OversizedConfigWarningMessage, backupPending: true);
            }

            var diagnostics = new ConfigLoadDiagnostics();
            var configText = File.ReadAllText(configPath);
            if (!TryParseConfigRoot(configText, diagnostics, out var rootObject) || rootObject is null)
            {
                diagnostics.RequestBackup();
                diagnostics.ShowWarning();
                return new PersistedConfigState(config, null, diagnostics.CreateWarningMessage(), diagnostics.BackupPending);
            }

            ApplyValidatedConfig(rootObject, config, diagnostics);

            var preservedRoot = (JsonObject)rootObject.DeepClone();
            SanitizeOpaqueFields(preservedRoot);

            return new PersistedConfigState(config, preservedRoot, diagnostics.CreateWarningMessage(), diagnostics.BackupPending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new PersistedConfigState(config, null);
        }
    }

    public static bool Save(SlideshowConfig config, PersistedConfigState state)
    {
        var root = state.PreservedRoot is null
            ? new JsonObject()
            : (JsonObject)state.PreservedRoot.DeepClone();
        var configPath = GetConfigPath();
        var backupSucceeded = false;
        var backupAttempted = false;

        root[nameof(SlideshowConfig.FolderPath)] = NormalizeFolderPath(config.FolderPath);
        root[nameof(SlideshowConfig.PlaybackMode)] = NormalizePlaybackMode(config.PlaybackMode).ToString();
        root[nameof(SlideshowConfig.ImageDurationSec)] = SlideshowConfig.NormalizeImageDuration(config.ImageDurationSec, config.IsEinkMode);
        root[nameof(SlideshowConfig.TransitionDurationSec)] = NormalizeTransitionDuration(config.TransitionDurationSec);
        root[nameof(SlideshowConfig.IsEinkMode)] = config.IsEinkMode;
        WriteTransitionEffects(root, config.TransitionEffects);
        root[nameof(SlideshowConfig.Topmost)] = config.Topmost;
        WriteUnsafeFormats(root, config.UnsafeFormats);
        WriteIgnoredKeys(root, config.IgnoredKeys);
        WriteIpcSecret(root, config.IpcSecret);
        WriteMaxCatalogImages(root, config.MaxCatalogImages);
        WriteBackgroundColor(root, config.BackgroundColor);

        if (state.BackupPending)
        {
            backupAttempted = true;
            backupSucceeded = TryWriteBackup(configPath);
        }

        try
        {
            File.WriteAllText(configPath, root.ToJsonString(SerializerOptions));
            state.PreservedRoot = root;
            state.WarningMessage = null;
            state.BackupPending = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (backupAttempted && backupSucceeded)
            {
                state.BackupPending = false;
            }
        }

        return false;
    }

    private static void ApplyValidatedConfig(JsonObject root, SlideshowConfig config, ConfigLoadDiagnostics diagnostics)
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
        if (TryGetValidatedTransitionEffects(root, nameof(SlideshowConfig.TransitionEffects), out var transitionEffects))
        {
            config.TransitionEffects = transitionEffects;
        }
        else
        {
            config.TransitionEffects = TransitionRegistry.NormalizeConfiguredEffects(null);
        }

        if (TryGetBool(root, nameof(SlideshowConfig.Topmost), out var topmost))
        {
            config.Topmost = topmost;
        }

        if (TryGetValidatedUnsafeFormats(root, nameof(SlideshowConfig.UnsafeFormats), out var unsafeFormats))
        {
            config.UnsafeFormats = unsafeFormats;
        }
        else if (root.ContainsKey(nameof(SlideshowConfig.UnsafeFormats)))
        {
            diagnostics.ShowWarning();
            diagnostics.RequestBackup();
        }

        if (TryGetValidatedPositiveInt(root, nameof(SlideshowConfig.MaxCatalogImages), out var maxCatalogImages))
        {
            config.MaxCatalogImages = maxCatalogImages;
        }

        if (TryGetValidatedBackgroundColor(root, nameof(SlideshowConfig.BackgroundColor), out var backgroundColor))
        {
            config.BackgroundColor = backgroundColor;
        }

        if (TryGetValidatedIgnoredKeys(root, nameof(SlideshowConfig.IgnoredKeys), out var ignoredKeys))
        {
            config.IgnoredKeys = ignoredKeys;
        }
        else if (root.ContainsKey(nameof(SlideshowConfig.IgnoredKeys)))
        {
            diagnostics.ShowWarning();
            diagnostics.RequestBackup();
        }

        var ipcSecretState = ReadIpcSecret(root, nameof(SlideshowConfig.IpcSecret));
        if (ipcSecretState.HasWarning)
        {
            diagnostics.ShowWarning();
        }

        if (ipcSecretState.RequestsBackup)
        {
            diagnostics.RequestBackup();
        }

        if (!string.IsNullOrWhiteSpace(ipcSecretState.Value))
        {
            config.IpcSecret = ipcSecretState.Value;
        }
    }

    private static void SanitizeOpaqueFields(JsonObject root)
    {
        if (root.ContainsKey("UnsafeFormats") && !HasValidUnsafeFormats(root["UnsafeFormats"]))
        {
            root.Remove("UnsafeFormats");
        }

        if (root.ContainsKey(nameof(SlideshowConfig.IgnoredKeys)) &&
            !TryGetValidatedIgnoredKeys(root, nameof(SlideshowConfig.IgnoredKeys), out _))
        {
            root.Remove(nameof(SlideshowConfig.IgnoredKeys));
        }

        if (root.ContainsKey("IpcSecret"))
        {
            var ipcSecretState = ReadIpcSecret(root, "IpcSecret");
            if (ipcSecretState.RequestsBackup || ipcSecretState.HasWarning)
            {
                root.Remove("IpcSecret");
            }
        }

        if (root.ContainsKey(nameof(SlideshowConfig.MaxCatalogImages)) &&
            !TryGetValidatedPositiveInt(root, nameof(SlideshowConfig.MaxCatalogImages), out _))
        {
            root.Remove(nameof(SlideshowConfig.MaxCatalogImages));
        }

        if (root.ContainsKey(nameof(SlideshowConfig.TransitionEffects)) &&
            root[nameof(SlideshowConfig.TransitionEffects)] is not JsonArray)
        {
            root.Remove(nameof(SlideshowConfig.TransitionEffects));
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

    private static bool TryGetValidatedPositiveInt(JsonObject root, string propertyName, out int value)
    {
        value = default;

        if (!root.TryGetPropertyValue(propertyName, out var node) || node is not JsonValue jsonValue)
        {
            return false;
        }

        if (!jsonValue.TryGetValue<int>(out var parsedValue) || SlideshowConfig.NormalizeMaxCatalogImages(parsedValue) is not int normalizedValue)
        {
            return false;
        }

        value = normalizedValue;
        return true;
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
                ignoredKeys = [];
                return false;
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

    private static bool TryGetValidatedTransitionEffects(JsonObject root, string propertyName, out string[] transitionEffects)
    {
        transitionEffects = TransitionRegistry.NormalizeConfiguredEffects(null);

        if (!root.TryGetPropertyValue(propertyName, out var node) || node is not JsonArray array)
        {
            return false;
        }

        var configuredEffects = new string[array.Count];
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonValue value ||
                !value.TryGetValue<string>(out var configuredEffect) ||
                configuredEffect is null)
            {
                return false;
            }

            configuredEffects[index] = configuredEffect;
        }

        transitionEffects = TransitionRegistry.NormalizeConfiguredEffects(configuredEffects);
        return true;
    }

    private static bool TryGetValidatedBackgroundColor(JsonObject root, string propertyName, out string backgroundColor)
    {
        backgroundColor = string.Empty;

        if (!TryGetString(root, propertyName, out var configuredValue) ||
            SlideshowConfig.NormalizeBackgroundColor(configuredValue) is not string normalizedValue)
        {
            return false;
        }

        backgroundColor = normalizedValue;
        return true;
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

    private static bool TryParseConfigRoot(string configText, ConfigLoadDiagnostics diagnostics, out JsonObject? rootObject)
    {
        rootObject = null;

        if (TryParseJsonNode(configText, out var rootNode, out _))
        {
            rootObject = rootNode as JsonObject;
            return rootObject is not null;
        }

        if (TryRecoverKnownConfigFields(configText, out var recoveredConfigText, out var recoveryResult) &&
            TryParseJsonNode(recoveredConfigText, out rootNode, out _) &&
            rootNode is JsonObject recoveredRootObject)
        {
            if (recoveryResult.UsedRecovery)
            {
                diagnostics.ShowWarning();
            }

            if (recoveryResult.ResetAnyField)
            {
                diagnostics.RequestBackup();
            }

            rootObject = recoveredRootObject;
            return true;
        }

        return false;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        return options;
    }

    private static Regex CreateRecoveryRegex(string pattern)
    {
        return new Regex(
            pattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
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

    private static bool TryParseJsonNode(string configText, out JsonNode? rootNode, out JsonException? exception)
    {
        try
        {
            rootNode = JsonNode.Parse(configText);
            exception = null;
            return true;
        }
        catch (JsonException ex)
        {
            rootNode = null;
            exception = ex;
            return false;
        }
    }

    private static bool TryRecoverKnownConfigFields(string configText, out string recoveredConfigText, out ConfigTextRecoveryResult recoveryResult)
    {
        var usedRecovery = false;
        var resetAnyField = false;
        var repairedConfigText = RepairKnownStringArraySeparators(configText);
        usedRecovery |= !string.Equals(configText, repairedConfigText, StringComparison.Ordinal);
        repairedConfigText = RepairMissingTopLevelPropertyCommas(repairedConfigText);
        usedRecovery |= !string.Equals(configText, repairedConfigText, StringComparison.Ordinal);

        if (TryParseJsonNode(repairedConfigText, out _, out _))
        {
            return CompleteRecovery(repairedConfigText, success: true, usedRecovery, resetAnyField, out recoveredConfigText, out recoveryResult);
        }

        var currentConfigText = repairedConfigText;

        for (var attempt = 0; attempt < RecoverableProperties.Length; attempt++)
        {
            if (TryParseJsonNode(currentConfigText, out _, out var parseException))
            {
                return CompleteRecovery(currentConfigText, success: true, usedRecovery, resetAnyField, out recoveredConfigText, out recoveryResult);
            }

            if (parseException is null ||
                !TryDropNearestRecoverableProperty(currentConfigText, parseException, out var updatedConfigText))
            {
                break;
            }

            currentConfigText = updatedConfigText;
            usedRecovery = true;
            resetAnyField = true;
        }

        return CompleteRecovery(
            currentConfigText,
            TryParseJsonNode(currentConfigText, out _, out _),
            usedRecovery,
            resetAnyField,
            out recoveredConfigText,
            out recoveryResult);

        static bool CompleteRecovery(
            string finalConfigText,
            bool success,
            bool usedRecovery,
            bool resetAnyField,
            out string recoveredConfigText,
            out ConfigTextRecoveryResult recoveryResult)
        {
            recoveredConfigText = finalConfigText;
            recoveryResult = new ConfigTextRecoveryResult(usedRecovery, resetAnyField);
            return success;
        }
    }

    private static string RepairKnownStringArraySeparators(string configText)
    {
        var repairedConfigText = configText;

        foreach (var propertyName in RecoverableStringArrayProperties)
        {
            if (TryRepairStringArraySeparators(repairedConfigText, propertyName, out var updatedConfigText))
            {
                repairedConfigText = updatedConfigText;
            }
        }

        return repairedConfigText;
    }

    private static bool TryRepairStringArraySeparators(string configText, string propertyName, out string repairedConfigText)
    {
        repairedConfigText = string.Empty;

        if (!TryFindPropertyValueBounds(configText, propertyName, out var arrayStartIndex, out var arrayEndIndex) ||
            arrayStartIndex >= configText.Length ||
            configText[arrayStartIndex] != '[' ||
            arrayEndIndex <= arrayStartIndex)
        {
            return false;
        }

        var arrayContents = configText[(arrayStartIndex + 1)..arrayEndIndex];
        var repairedArrayContents = RepairMissingStringSeparators(arrayContents);

        if (string.Equals(arrayContents, repairedArrayContents, StringComparison.Ordinal))
        {
            return false;
        }

        repairedConfigText = string.Concat(
            configText.AsSpan(0, arrayStartIndex + 1),
            repairedArrayContents,
            configText.AsSpan(arrayEndIndex));

        return true;
    }

    private static string RepairMissingTopLevelPropertyCommas(string configText)
    {
        var repairedConfigText = MissingPropertyCommaAfterStringRegex.Replace(configText, "$1, ");
        repairedConfigText = MissingPropertyCommaAfterLiteralRegex.Replace(repairedConfigText, "$1, ");
        repairedConfigText = MissingPropertyCommaAfterArrayRegex.Replace(repairedConfigText, "$1, ");
        return repairedConfigText;
    }

    private static bool TryDropNearestRecoverableProperty(string configText, JsonException parseException, out string updatedConfigText)
    {
        updatedConfigText = string.Empty;

        if (!TryLocateNearestRecoverableProperty(configText, parseException, out var propertyName))
        {
            return false;
        }

        if (!TryFindPropertyValueBounds(configText, propertyName, out var valueStartIndex, out var valueEndIndex))
        {
            return false;
        }

        updatedConfigText = string.Concat(
            configText.AsSpan(0, valueStartIndex),
            "null",
            configText.AsSpan(valueEndIndex));

        return true;
    }

    private static bool TryLocateNearestRecoverableProperty(string configText, JsonException parseException, out string propertyName)
    {
        propertyName = string.Empty;

        var searchOrigin = GetParseErrorSearchIndex(configText, parseException);
        var closestPropertyIndex = -1;

        foreach (var candidateProperty in RecoverableProperties)
        {
            var candidateIndex = FindLastPropertyKeyIndex(configText, candidateProperty, searchOrigin);
            if (candidateIndex <= closestPropertyIndex)
            {
                continue;
            }

            closestPropertyIndex = candidateIndex;
            propertyName = candidateProperty;
        }

        return closestPropertyIndex >= 0;
    }

    private static int FindLastPropertyKeyIndex(string configText, string propertyName, int searchOrigin)
    {
        var token = $"\"{propertyName}\"";
        var searchLength = Math.Min(searchOrigin + 1, configText.Length);
        var searchIndex = searchOrigin;

        while (searchLength > 0 && searchIndex >= 0)
        {
            var candidateIndex = configText.LastIndexOf(token, searchIndex, searchLength, StringComparison.Ordinal);
            if (candidateIndex < 0)
            {
                return -1;
            }

            if (LooksLikePropertyName(configText, candidateIndex))
            {
                return candidateIndex;
            }

            searchLength = candidateIndex;
            searchIndex = candidateIndex - 1;
        }

        return -1;
    }

    private static int GetParseErrorSearchIndex(string configText, JsonException parseException)
    {
        if (parseException.LineNumber is null || parseException.BytePositionInLine is null)
        {
            return configText.Length - 1;
        }

        var targetLine = (int)parseException.LineNumber.Value;
        var targetColumn = (int)parseException.BytePositionInLine.Value;
        var currentLine = 0;
        var currentColumn = 0;

        for (var index = 0; index < configText.Length; index++)
        {
            if (currentLine == targetLine && currentColumn >= targetColumn)
            {
                return index;
            }

            if (configText[index] == '\n')
            {
                currentLine++;
                currentColumn = 0;
            }
            else
            {
                currentColumn++;
            }
        }

        return configText.Length - 1;
    }

    private static bool TryFindPropertyValueBounds(string configText, string propertyName, out int valueStartIndex, out int valueEndIndex)
    {
        valueStartIndex = -1;
        valueEndIndex = -1;

        var propertyIndex = FindFirstPropertyKeyIndex(configText, propertyName);
        if (propertyIndex < 0)
        {
            return false;
        }

        var colonIndex = configText.IndexOf(':', propertyIndex);
        if (colonIndex < 0)
        {
            return false;
        }

        valueStartIndex = colonIndex + 1;
        while (valueStartIndex < configText.Length && char.IsWhiteSpace(configText[valueStartIndex]))
        {
            valueStartIndex++;
        }

        if (valueStartIndex >= configText.Length)
        {
            return false;
        }

        var inString = false;
        var escaping = false;
        var arrayDepth = 0;
        var objectDepth = 0;

        for (var index = valueStartIndex; index < configText.Length; index++)
        {
            var current = configText[index];

            if (inString)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                if (arrayDepth == 0 && objectDepth == 0 && index > valueStartIndex && LooksLikePropertyName(configText, index))
                {
                    valueEndIndex = index;
                    return true;
                }

                inString = true;
                continue;
            }

            if (current == '[')
            {
                arrayDepth++;
                continue;
            }

            if (current == '{')
            {
                objectDepth++;
                continue;
            }

            if (current == ']')
            {
                if (arrayDepth > 0)
                {
                    arrayDepth--;
                }

                continue;
            }

            if (current == '}')
            {
                if (objectDepth > 0)
                {
                    objectDepth--;
                    continue;
                }
            }

            if (arrayDepth == 0 && objectDepth == 0 && current == ',')
            {
                valueEndIndex = index;
                return true;
            }

            if (arrayDepth == 0 && objectDepth == 0 && current == '}')
            {
                valueEndIndex = index;
                return true;
            }
        }

        return false;
    }

    private static int FindFirstPropertyKeyIndex(string configText, string propertyName)
    {
        var token = $"\"{propertyName}\"";
        var searchIndex = 0;

        while (searchIndex < configText.Length)
        {
            var candidateIndex = configText.IndexOf(token, searchIndex, StringComparison.Ordinal);
            if (candidateIndex < 0)
            {
                return -1;
            }

            if (LooksLikePropertyName(configText, candidateIndex))
            {
                return candidateIndex;
            }

            searchIndex = candidateIndex + token.Length;
        }

        return -1;
    }

    private static bool LooksLikePropertyName(string configText, int quoteIndex)
    {
        var closingQuoteIndex = quoteIndex + 1;
        var escaping = false;

        while (closingQuoteIndex < configText.Length)
        {
            var current = configText[closingQuoteIndex];
            if (escaping)
            {
                escaping = false;
                closingQuoteIndex++;
                continue;
            }

            if (current == '\\')
            {
                escaping = true;
                closingQuoteIndex++;
                continue;
            }

            if (current == '"')
            {
                break;
            }

            closingQuoteIndex++;
        }

        if (closingQuoteIndex >= configText.Length)
        {
            return false;
        }

        var nextIndex = closingQuoteIndex + 1;
        while (nextIndex < configText.Length && char.IsWhiteSpace(configText[nextIndex]))
        {
            nextIndex++;
        }

        return nextIndex < configText.Length && configText[nextIndex] == ':';
    }

    private static int FindArrayEndIndex(string text, int arrayStartIndex)
    {
        var inString = false;
        var escaping = false;
        var depth = 0;

        for (var index = arrayStartIndex; index < text.Length; index++)
        {
            var current = text[index];

            if (inString)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '[')
            {
                depth++;
                continue;
            }

            if (current == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static string RepairMissingStringSeparators(string arrayContents)
    {
        var repaired = new System.Text.StringBuilder(arrayContents.Length + 8);
        var inString = false;
        var escaping = false;
        var previousTokenWasString = false;

        foreach (var current in arrayContents)
        {
            if (inString)
            {
                repaired.Append(current);

                if (escaping)
                {
                    escaping = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                    previousTokenWasString = true;
                }

                continue;
            }

            if (current == '"')
            {
                if (previousTokenWasString)
                {
                    repaired.Append(", ");
                }

                repaired.Append(current);
                inString = true;
                continue;
            }

            repaired.Append(current);

            if (char.IsWhiteSpace(current))
            {
                continue;
            }

            previousTokenWasString = false;
        }

        return repaired.ToString();
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
            array.Add((JsonNode?)JsonValue.Create(format));
        }

        root[nameof(SlideshowConfig.UnsafeFormats)] = array;
    }

    private static void WriteIgnoredKeys(JsonObject root, string[]? ignoredKeys)
    {
        if (ignoredKeys is null)
        {
            root.Remove(nameof(SlideshowConfig.IgnoredKeys));
            return;
        }

        var array = new JsonArray();
        foreach (var ignoredKey in ignoredKeys)
        {
            array.Add((JsonNode?)JsonValue.Create(ignoredKey));
        }

        root[nameof(SlideshowConfig.IgnoredKeys)] = array;
    }

    private static void WriteIpcSecret(JsonObject root, string? ipcSecret)
    {
        if (string.IsNullOrWhiteSpace(ipcSecret))
        {
            root.Remove(nameof(SlideshowConfig.IpcSecret));
            return;
        }

        root[nameof(SlideshowConfig.IpcSecret)] = ipcSecret;
    }

    private static void WriteTransitionEffects(JsonObject root, string[]? transitionEffects)
    {
        var normalizedEffects = TransitionRegistry.NormalizeConfiguredEffects(transitionEffects);
        var array = new JsonArray();

        foreach (var effect in normalizedEffects)
        {
            array.Add((JsonNode?)JsonValue.Create(effect));
        }

        root[nameof(SlideshowConfig.TransitionEffects)] = array;
    }

    private static void WriteMaxCatalogImages(JsonObject root, int? maxCatalogImages)
    {
        var normalizedValue = SlideshowConfig.NormalizeMaxCatalogImages(maxCatalogImages);
        if (normalizedValue is null)
        {
            root.Remove(nameof(SlideshowConfig.MaxCatalogImages));
            return;
        }

        root[nameof(SlideshowConfig.MaxCatalogImages)] = normalizedValue.Value;
    }

    private static void WriteBackgroundColor(JsonObject root, string? backgroundColor)
    {
        var normalizedValue = SlideshowConfig.NormalizeBackgroundColor(backgroundColor);
        if (normalizedValue is null)
        {
            root.Remove(nameof(SlideshowConfig.BackgroundColor));
            return;
        }

        root[nameof(SlideshowConfig.BackgroundColor)] = normalizedValue;
    }

    private static bool TryWriteBackup(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return false;
            }

            var originalText = File.ReadAllText(configPath);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory, BackupFileName), originalText);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IpcSecretReadResult ReadIpcSecret(JsonObject root, string propertyName)
    {
        if (!root.TryGetPropertyValue(propertyName, out var node))
        {
            return IpcSecretReadResult.None;
        }

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var value) || value is null)
        {
            return new IpcSecretReadResult(null, HasWarning: true, RequestsBackup: true);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return new IpcSecretReadResult(null, HasWarning: true, RequestsBackup: false);
        }

        return new IpcSecretReadResult(value, HasWarning: false, RequestsBackup: false);
    }
}

public sealed class PersistedConfigState
{
    internal PersistedConfigState(SlideshowConfig config, JsonObject? preservedRoot, string? warningMessage = null, bool backupPending = false)
    {
        Config = config;
        PreservedRoot = preservedRoot;
        WarningMessage = warningMessage;
        BackupPending = backupPending;
    }

    public SlideshowConfig Config { get; }

    public bool HasRecoveryWarning => !string.IsNullOrWhiteSpace(WarningMessage);

    public string? WarningMessage { get; internal set; }

    internal JsonObject? PreservedRoot { get; set; }

    internal bool BackupPending { get; set; }
}

internal sealed class ConfigLoadDiagnostics
{
    public bool BackupPending { get; private set; }

    public bool HasWarning { get; private set; }

    public void RequestBackup()
    {
        BackupPending = true;
    }

    public void ShowWarning()
    {
        HasWarning = true;
    }

    public string? CreateWarningMessage()
    {
        return HasWarning ? "Config recovered: Verify IgnoredKeys and IpcSecret after exit." : null;
    }
}

internal readonly record struct ConfigTextRecoveryResult(bool UsedRecovery, bool ResetAnyField);

internal readonly record struct IpcSecretReadResult(string? Value, bool HasWarning, bool RequestsBackup)
{
    public static IpcSecretReadResult None => new(null, false, false);
}

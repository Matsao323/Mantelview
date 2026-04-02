using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Mantelview.Models;
using Mantelview.Services;

namespace Mantelview;

public partial class MainWindow : Window
{
    private const string DefaultFolderMessage = "Select a folder that contains .jpg, .jpeg, .png, or .webp files.";
    private const string DefaultSelectFolderButtonText = "Select Folder";
    private const int FolderButtonTextMaxLength = 56;

    private static readonly SolidColorBrush MutedStatusBrush = new(Color.Parse("#4B5563"));
    private static readonly SolidColorBrush ErrorStatusBrush = new(Color.Parse("#B91C1C"));

    private readonly SlideshowConfig _config = new();
    private PersistedConfigState _persistedConfigState = ConfigService.CreateDefaultState();
    private string? _selectedFolderPath;
    private SlideshowWindow? _slideshowWindow;
    private double _displayTimeSeconds = 5.0;
    private double _transitionTimeSeconds = 1.0;
    private bool _isFolderPickerOpen;
    private bool _isLaunchingSlideshow;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;

        _persistedConfigState = ConfigService.Load();
        InitializeConfigBindings();
        ApplyLoadedConfig(_persistedConfigState.Config);
        RefreshFolderState();
    }

    private async void SelectFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isFolderPickerOpen)
        {
            return;
        }

        if (StorageProvider is null)
        {
            SetFolderState(folderPath: _selectedFolderPath, isValid: false, message: "Folder picker is unavailable on this platform.", isError: true);
            return;
        }

        _isFolderPickerOpen = true;
        SelectFolderButton.IsEnabled = false;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Select image folder",
            });

            var selectedFolder = folders.FirstOrDefault();

            if (selectedFolder is null)
            {
                return;
            }

            var folderPath = selectedFolder.TryGetLocalPath();

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                _selectedFolderPath = null;
                SetFolderState(folderPath: null, isValid: false, message: "The selected folder is not available as a local path.", isError: true);
                return;
            }

            if (!SlideshowConfig.TryResolveFolderPath(folderPath, out var resolvedFolderPath) || string.IsNullOrWhiteSpace(resolvedFolderPath))
            {
                _selectedFolderPath = null;
                SetFolderState(folderPath: null, isValid: false, message: "The selected folder is not available as a local path.", isError: true);
                return;
            }

            _selectedFolderPath = resolvedFolderPath;
            RefreshFolderState();
        }
        finally
        {
            _isFolderPickerOpen = false;
            SelectFolderButton.IsEnabled = true;
        }
    }

    private async void PlayButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_slideshowWindow is not null || _isLaunchingSlideshow || string.IsNullOrWhiteSpace(_selectedFolderPath))
        {
            return;
        }

        RefreshFolderState();

        if (!PlayButton.IsEnabled)
        {
            return;
        }

        SaveCurrentConfig();

        if (!ImageCatalog.TryLoad(_selectedFolderPath, _config.PlaybackMode, out var catalog, _config.UnsafeFormats))
        {
            RefreshFolderState();
            return;
        }

        var slideshowWindow = new SlideshowWindow(CreateLaunchConfigSnapshot(), catalog);
        _isLaunchingSlideshow = true;
        PlayButton.IsEnabled = false;

        try
        {
            if (!await slideshowWindow.InitializeAsync().ConfigureAwait(true))
            {
                var evaluation = FolderSelectionEvaluator.Evaluate(_config);
                SetFolderState(_selectedFolderPath, isValid: false, message: evaluation.Message, isError: true);
                return;
            }

            slideshowWindow.Closed += SlideshowWindow_Closed;
            _slideshowWindow = slideshowWindow;
            Hide();
            slideshowWindow.Show();
        }
        catch
        {
            slideshowWindow.Closed -= SlideshowWindow_Closed;
            await slideshowWindow.ShutdownAsync().ConfigureAwait(true);
            _slideshowWindow = null;
            RefreshAfterSlideshowClose();
            throw;
        }
        finally
        {
            _isLaunchingSlideshow = false;
        }
    }

    private void SetFolderState(string? folderPath, bool isValid, string message, bool isError = false)
    {
        _config.FolderPath = folderPath ?? string.Empty;
        SelectFolderButtonText.Text = FormatFolderButtonText(folderPath);
        ToolTip.SetTip(SelectFolderButton, string.IsNullOrWhiteSpace(folderPath) ? null : folderPath);

        StatusText.Text = message;
        StatusText.Foreground = isError ? ErrorStatusBrush : MutedStatusBrush;
        PlayButton.IsEnabled = isValid && _slideshowWindow is null && !_isLaunchingSlideshow;
    }

    private void RefreshFolderState()
    {
        if (string.IsNullOrWhiteSpace(_selectedFolderPath))
        {
            SetFolderState(folderPath: null, isValid: false, message: DefaultFolderMessage);
            return;
        }

        _config.FolderPath = _selectedFolderPath;
        var evaluation = FolderSelectionEvaluator.Evaluate(_config);
        SetFolderState(_selectedFolderPath, evaluation.IsValid, evaluation.Message, evaluation.IsError);
    }

    private void SlideshowWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is SlideshowWindow slideshowWindow)
        {
            slideshowWindow.Closed -= SlideshowWindow_Closed;
        }

        _slideshowWindow = null;
        RefreshAfterSlideshowClose();
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        SaveCurrentConfig();
    }

    private void RefreshAfterSlideshowClose()
    {
        _isLaunchingSlideshow = false;
        Show();
        Activate();
        RefreshFolderState();
    }

    private void InitializeConfigBindings()
    {
        ShuffleToggle.PropertyChanged += ShuffleToggle_PropertyChanged;
        EinkModeToggle.PropertyChanged += EinkModeToggle_PropertyChanged;

        SyncConfigFromControls();
    }

    private void ApplyLoadedConfig(SlideshowConfig config)
    {
        _selectedFolderPath = string.IsNullOrWhiteSpace(config.FolderPath)
            ? null
            : config.FolderPath;

        ShuffleToggle.IsChecked = config.PlaybackMode == PlaybackMode.Random;
        _config.IsEinkMode = config.IsEinkMode;
        SetDisplayTimeSeconds(config.ImageDurationSec);
        SetTransitionTimeSeconds(config.TransitionDurationSec);
        EinkModeToggle.IsChecked = config.IsEinkMode;

        _config.FolderPath = config.FolderPath;
        _config.Topmost = config.Topmost;
        _config.UnsafeFormats = config.UnsafeFormats?.ToArray();
        _config.IgnoredKeys = config.IgnoredKeys?.ToArray();
        _config.IpcSecret = config.IpcSecret;
    }

    private void ShuffleToggle_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ToggleButton.IsCheckedProperty)
        {
            _config.PlaybackMode = ShuffleToggle.IsChecked == true
                ? PlaybackMode.Random
                : PlaybackMode.InOrder;
        }
    }

    private void EinkModeToggle_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ToggleButton.IsCheckedProperty)
        {
            UpdateEinkMode();
        }
    }

    private void DisplayTimeDecreaseButton_Click(object? sender, RoutedEventArgs e)
    {
        SetDisplayTimeSeconds(_displayTimeSeconds - 1.0);
    }

    private void DisplayTimeIncreaseButton_Click(object? sender, RoutedEventArgs e)
    {
        SetDisplayTimeSeconds(_displayTimeSeconds + 1.0);
    }

    private void TransitionTimeDecreaseButton_Click(object? sender, RoutedEventArgs e)
    {
        SetTransitionTimeSeconds(_transitionTimeSeconds - 0.5);
    }

    private static string FormatFolderButtonText(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return DefaultSelectFolderButtonText;
        }

        if (folderPath.Length <= FolderButtonTextMaxLength)
        {
            return folderPath;
        }

        const string ellipsis = "...";
        var suffixLength = (FolderButtonTextMaxLength - ellipsis.Length) / 2;
        var prefixLength = FolderButtonTextMaxLength - ellipsis.Length - suffixLength;

        return string.Concat(
            folderPath.AsSpan(0, prefixLength),
            ellipsis,
            folderPath.AsSpan(folderPath.Length - suffixLength));
    }

    private void TransitionTimeIncreaseButton_Click(object? sender, RoutedEventArgs e)
    {
        SetTransitionTimeSeconds(_transitionTimeSeconds + 0.5);
    }

    private void SyncConfigFromControls()
    {
        _config.PlaybackMode = ShuffleToggle.IsChecked == true
            ? PlaybackMode.Random
            : PlaybackMode.InOrder;
        SetDisplayTimeSeconds(_displayTimeSeconds);
        SetTransitionTimeSeconds(_transitionTimeSeconds);
        UpdateEinkMode();
    }

    private void SetDisplayTimeSeconds(double value)
    {
        _displayTimeSeconds = SlideshowConfig.NormalizeImageDuration(value, _config.IsEinkMode);
        DisplayTimeValueText.Text = $"{_displayTimeSeconds:F0}";
        _config.ImageDurationSec = _displayTimeSeconds;
    }

    private void SetTransitionTimeSeconds(double value)
    {
        var steppedValue = Math.Round(value * 2.0, MidpointRounding.AwayFromZero) / 2.0;
        _transitionTimeSeconds = Math.Clamp(steppedValue, SlideshowConfig.MinTransitionDurationSec, SlideshowConfig.MaxTransitionDurationSec);
        TransitionTimeValueText.Text = $"{_transitionTimeSeconds:F1}";
        _config.TransitionDurationSec = _transitionTimeSeconds;
    }

    private void UpdateEinkMode()
    {
        var isEinkMode = EinkModeToggle.IsChecked == true;
        _config.IsEinkMode = isEinkMode;
        SetDisplayTimeSeconds(_displayTimeSeconds);
        TransitionTimeSection.IsEnabled = !isEinkMode;
        TransitionTimeSection.Opacity = isEinkMode ? 0.55 : 1.0;
    }

    private void SaveCurrentConfig()
    {
        ConfigService.Save(_config, _persistedConfigState);
    }

    private SlideshowConfig CreateLaunchConfigSnapshot()
    {
        return new SlideshowConfig
        {
            FolderPath = _config.FolderPath,
            PlaybackMode = _config.PlaybackMode,
            ImageDurationSec = _config.GetEffectiveImageDurationSec(),
            TransitionDurationSec = _config.TransitionDurationSec,
            IsEinkMode = _config.IsEinkMode,
            Topmost = _config.Topmost,
            UnsafeFormats = _config.UnsafeFormats?.ToArray(),
            IgnoredKeys = _config.IgnoredKeys?.ToArray(),
            IpcSecret = _config.IpcSecret,
        };
    }
}

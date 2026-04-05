using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Mantelview.Input;
using Mantelview.Models;
using Mantelview.Services;

namespace Mantelview;

public partial class SlideshowWindow : Window
{
    private static readonly TimeSpan EscapeHoldThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CatalogLimitNoticeDuration = TimeSpan.FromSeconds(6);
    private const int BytesPerPixel = 4;
    private readonly SlideshowConfig _config;
    private readonly ImageCatalog? _catalog;
    private readonly Stopwatch _escapeStopwatch = new();
    private readonly DispatcherTimer _escapeTimer;
    private readonly DispatcherTimer _catalogLimitNoticeTimer;
    private readonly InputFilter _inputFilter;
    private bool _escapeHeld;
    private bool _pipelineDisposed;
    private ImagePreloader? _preloader;
    private SlideshowEngine? _engine;
    private CommandListener? _commandListener;

    public SlideshowWindow()
        : this(new SlideshowConfig(), null)
    {
    }

    public SlideshowWindow(SlideshowConfig config, ImageCatalog? catalog)
    {
        _config = config;
        _catalog = catalog;
        _inputFilter = new InputFilter(_config.IgnoredKeys);
        _escapeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _catalogLimitNoticeTimer = new DispatcherTimer
        {
            Interval = CatalogLimitNoticeDuration,
        };

        InitializeComponent();
        ApplyConfiguredBackgroundColor();
        ResetFrameSurfaceOrder();
        Topmost = _config.Topmost;

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        _escapeTimer.Tick += EscapeTimer_Tick;
        _catalogLimitNoticeTimer.Tick += CatalogLimitNoticeTimer_Tick;
        Opened += SlideshowWindow_Opened;
        Closed += SlideshowWindow_Closed;
    }

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is null)
        {
            return false;
        }

        var preloader = new ImagePreloader(_catalog, GetScreenPixelSize);
        var transitionRegistry = new TransitionRegistry();
        var engine = new SlideshowEngine(_config, preloader, transitionRegistry, PresentFrameAsync);
        var commandListener = new CommandListener(_config.IpcSecret, HandleExternalCommand);

        try
        {
            if (!await engine.StartAsync(cancellationToken).ConfigureAwait(true))
            {
                await engine.StopAsync().ConfigureAwait(true);
                engine.Dispose();
                preloader.Dispose();
                commandListener.Dispose();
                return false;
            }

            commandListener.Start();
            engine.StopRequested = OnStopRequested;
            _preloader = preloader;
            _engine = engine;
            _commandListener = commandListener;
            _pipelineDisposed = false;
            return true;
        }
        catch
        {
            engine.StopRequested = null;
            await engine.StopAsync().ConfigureAwait(true);
            engine.Dispose();
            preloader.Dispose();
            commandListener.Dispose();
            throw;
        }
    }

    public Task ShutdownAsync()
    {
        return DisposePipelineAsync();
    }

    public event EventHandler? FramePresented;

    private void ApplyConfiguredBackgroundColor()
    {
        var backgroundBrush = ResolveBackgroundBrush(_config.BackgroundColor);
        Background = backgroundBrush;
        FrameHost.Background = backgroundBrush;
    }

    internal static Bitmap LoadBitmapForScreen(string imagePath, PixelSize screenSize)
    {
        var metadata = TryReadImageMetadata(imagePath, out var parsedMetadata)
            ? parsedMetadata
            : ProbeImageMetadata(imagePath);
        using var decodeStream = File.OpenRead(imagePath);
        var decodedBitmap = DecodeBitmapForScreen(decodeStream, metadata, screenSize);

        return ApplyExifOrientation(decodedBitmap, metadata.Orientation);
    }

    private PixelSize GetScreenPixelSize()
    {
        var screen = Screens?.ScreenFromWindow(this);

        if (screen is not null)
        {
            return screen.Bounds.Size;
        }

        var primaryScreen = Screens?.Primary;

        if (primaryScreen is not null)
        {
            return primaryScreen.Bounds.Size;
        }

        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * RenderScaling));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * RenderScaling));

        return new PixelSize(width, height);
    }

    private static bool ShouldDecodeToWidth(PixelSize imageSize, PixelSize screenSize)
    {
        return (long)screenSize.Width * imageSize.Height <= (long)screenSize.Height * imageSize.Width;
    }

    private static IBrush ResolveBackgroundBrush(string? configuredBackgroundColor)
    {
        return SlideshowConfig.ResolveBackgroundColor(configuredBackgroundColor) == SlideshowConfig.WhiteBackgroundColor
            ? Brushes.White
            : Brushes.Black;
    }

    private static WriteableBitmap DecodeBitmapForScreen(Stream decodeStream, ImageDecodeMetadata metadata, PixelSize screenSize)
    {
        var targetWidth = Math.Max(1, screenSize.Width);
        var targetHeight = Math.Max(1, screenSize.Height);
        var decodeAgainstDisplayWidth = ShouldDecodeToWidth(metadata.DisplayPixelSize, screenSize);

        if (OrientationSwapsAxes(metadata.Orientation))
        {
            return decodeAgainstDisplayWidth
                ? WriteableBitmap.DecodeToHeight(decodeStream, targetWidth, BitmapInterpolationMode.HighQuality)
                : WriteableBitmap.DecodeToWidth(decodeStream, targetHeight, BitmapInterpolationMode.HighQuality);
        }

        return decodeAgainstDisplayWidth
            ? WriteableBitmap.DecodeToWidth(decodeStream, targetWidth, BitmapInterpolationMode.HighQuality)
            : WriteableBitmap.DecodeToHeight(decodeStream, targetHeight, BitmapInterpolationMode.HighQuality);
    }

    private static ImageDecodeMetadata ProbeImageMetadata(string imagePath)
    {
        using var probeStream = File.OpenRead(imagePath);
        using var probeBitmap = new Bitmap(probeStream);
        return new ImageDecodeMetadata(probeBitmap.PixelSize, JpegExifOrientation.Normal);
    }

    private static bool TryReadImageMetadata(string imagePath, out ImageDecodeMetadata metadata)
    {
        var extension = Path.GetExtension(imagePath);

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && TryReadPngPixelSize(imagePath, out var pngPixelSize))
        {
            metadata = new ImageDecodeMetadata(pngPixelSize, JpegExifOrientation.Normal);
            return true;
        }

        if ((extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            && TryReadJpegMetadata(imagePath, out metadata))
        {
            return true;
        }

        metadata = default;
        return false;
    }

    private static bool TryReadPngPixelSize(string imagePath, out PixelSize pixelSize)
    {
        pixelSize = default;

        var buffer = new byte[24];
        using var stream = File.OpenRead(imagePath);
        if (!TryReadExactly(stream, buffer))
        {
            return false;
        }

        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!buffer.AsSpan(0, 8).SequenceEqual(signature))
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(20, 4));
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        pixelSize = new PixelSize(width, height);
        return true;
    }

    private static bool TryReadJpegMetadata(string imagePath, out ImageDecodeMetadata metadata)
    {
        metadata = default;

        using var stream = File.OpenRead(imagePath);
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
        {
            return false;
        }

        var orientation = JpegExifOrientation.Normal;
        PixelSize? encodedPixelSize = null;

        while (true)
        {
            var markerPrefix = stream.ReadByte();
            if (markerPrefix < 0)
            {
                break;
            }

            if (markerPrefix != 0xFF)
            {
                continue;
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
                if (marker < 0)
                {
                    break;
                }
            }
            while (marker == 0xFF);

            if (marker < 0)
            {
                break;
            }

            if (marker is 0xD9 or 0xDA)
            {
                break;
            }

            var lengthHigh = stream.ReadByte();
            var lengthLow = stream.ReadByte();
            if (lengthHigh < 0 || lengthLow < 0)
            {
                return false;
            }

            var segmentLength = (lengthHigh << 8) + lengthLow;
            if (segmentLength < 2)
            {
                return false;
            }

            var payloadLength = segmentLength - 2;

            if (marker == 0xE1)
            {
                if (TryReadExifOrientation(stream, payloadLength, out var parsedOrientation))
                {
                    orientation = parsedOrientation;
                }

                continue;
            }

            if (IsStartOfFrameMarker(marker))
            {
                if (!TryReadJpegStartOfFrame(stream, payloadLength, out var parsedPixelSize))
                {
                    return false;
                }

                encodedPixelSize = parsedPixelSize;
                continue;
            }

            stream.Seek(payloadLength, SeekOrigin.Current);
        }

        if (encodedPixelSize is null)
        {
            return false;
        }

        metadata = new ImageDecodeMetadata(ApplyOrientation(encodedPixelSize.Value, orientation), orientation);
        return true;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = stream.Read(buffer[totalRead..]);
            if (bytesRead == 0)
            {
                return false;
            }

            totalRead += bytesRead;
        }

        return true;
    }

    private static bool IsStartOfFrameMarker(int marker)
    {
        return marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
    }

    private static bool TryReadJpegStartOfFrame(Stream stream, int payloadLength, out PixelSize pixelSize)
    {
        pixelSize = default;
        if (payloadLength < 5)
        {
            return false;
        }

        var frameHeader = new byte[5];
        if (!TryReadExactly(stream, frameHeader))
        {
            return false;
        }

        var height = BinaryPrimitives.ReadUInt16BigEndian(frameHeader.AsSpan(1, 2));
        var width = BinaryPrimitives.ReadUInt16BigEndian(frameHeader.AsSpan(3, 2));
        if (width == 0 || height == 0)
        {
            return false;
        }

        if (payloadLength > frameHeader.Length)
        {
            stream.Seek(payloadLength - frameHeader.Length, SeekOrigin.Current);
        }

        pixelSize = new PixelSize(width, height);
        return true;
    }

    private static bool TryReadExifOrientation(Stream stream, int payloadLength, out JpegExifOrientation orientation)
    {
        orientation = JpegExifOrientation.Normal;
        if (payloadLength <= 0)
        {
            return false;
        }

        var payload = new byte[payloadLength];
        if (!TryReadExactly(stream, payload))
        {
            return false;
        }

        ReadOnlySpan<byte> exifHeader = "Exif\0\0"u8;
        if (!payload.AsSpan().StartsWith(exifHeader))
        {
            return false;
        }

        return TryParseExifOrientation(payload.AsSpan(exifHeader.Length), out orientation);
    }

    private static bool TryParseExifOrientation(ReadOnlySpan<byte> tiffData, out JpegExifOrientation orientation)
    {
        orientation = JpegExifOrientation.Normal;
        if (tiffData.Length < 8)
        {
            return false;
        }

        var byteOrder = tiffData[..2];
        var isLittleEndian = byteOrder.SequenceEqual("II"u8);
        if (!isLittleEndian && !byteOrder.SequenceEqual("MM"u8))
        {
            return false;
        }

        var ifdOffset = ReadUInt32(tiffData.Slice(4, 4), isLittleEndian);
        if (ifdOffset > int.MaxValue || ifdOffset + 2 > (uint)tiffData.Length)
        {
            return false;
        }

        var entryCountOffset = (int)ifdOffset;
        var entryCount = ReadUInt16(tiffData.Slice(entryCountOffset, 2), isLittleEndian);
        var entriesOffset = entryCountOffset + 2;

        for (var entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            var entryOffset = entriesOffset + (entryIndex * 12);
            if (entryOffset + 12 > tiffData.Length)
            {
                return false;
            }

            var entry = tiffData.Slice(entryOffset, 12);
            var tag = ReadUInt16(entry.Slice(0, 2), isLittleEndian);
            if (tag != 0x0112)
            {
                continue;
            }

            var type = ReadUInt16(entry.Slice(2, 2), isLittleEndian);
            var count = ReadUInt32(entry.Slice(4, 4), isLittleEndian);
            if (type != 3 || count != 1)
            {
                return false;
            }

            var orientationValue = ReadUInt16(entry.Slice(8, 2), isLittleEndian);
            if (!Enum.IsDefined(typeof(JpegExifOrientation), (int)orientationValue))
            {
                return false;
            }

            orientation = (JpegExifOrientation)orientationValue;
            return true;
        }

        return false;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> buffer, bool isLittleEndian)
    {
        return isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(buffer)
            : BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, bool isLittleEndian)
    {
        return isLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(buffer)
            : BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static Bitmap ApplyExifOrientation(WriteableBitmap decodedBitmap, JpegExifOrientation orientation)
    {
        if (orientation == JpegExifOrientation.Normal)
        {
            return decodedBitmap;
        }

        using var normalizedSource = new WriteableBitmap(decodedBitmap.PixelSize, decodedBitmap.Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var normalizedLock = normalizedSource.Lock())
        {
            decodedBitmap.CopyPixels(normalizedLock, AlphaFormat.Unpremul);
        }

        var destinationBitmap = new WriteableBitmap(
            ApplyOrientation(decodedBitmap.PixelSize, orientation),
            decodedBitmap.Dpi,
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        try
        {
            using var sourceLock = normalizedSource.Lock();
            using var destinationLock = destinationBitmap.Lock();
            CopyOrientedPixels(sourceLock, destinationLock, orientation);
            decodedBitmap.Dispose();
            return destinationBitmap;
        }
        catch
        {
            destinationBitmap.Dispose();
            decodedBitmap.Dispose();
            throw;
        }
    }

    private static void CopyOrientedPixels(
        ILockedFramebuffer source,
        ILockedFramebuffer destination,
        JpegExifOrientation orientation)
    {
        if (source.Format != PixelFormat.Bgra8888 || destination.Format != PixelFormat.Bgra8888)
        {
            throw new NotSupportedException("EXIF orientation correction requires BGRA8888 framebuffers.");
        }

        var sourceBytes = new byte[source.RowBytes * source.Size.Height];
        var destinationBytes = new byte[destination.RowBytes * destination.Size.Height];
        Marshal.Copy(source.Address, sourceBytes, 0, sourceBytes.Length);

        for (var y = 0; y < source.Size.Height; y++)
        {
            for (var x = 0; x < source.Size.Width; x++)
            {
                var destinationPoint = MapOrientedPixel(x, y, source.Size, orientation);
                var sourceOffset = (y * source.RowBytes) + (x * BytesPerPixel);
                var destinationOffset = (destinationPoint.Y * destination.RowBytes) + (destinationPoint.X * BytesPerPixel);
                Buffer.BlockCopy(sourceBytes, sourceOffset, destinationBytes, destinationOffset, BytesPerPixel);
            }
        }

        Marshal.Copy(destinationBytes, 0, destination.Address, destinationBytes.Length);
    }

    private static PixelPoint MapOrientedPixel(int x, int y, PixelSize sourceSize, JpegExifOrientation orientation)
    {
        return orientation switch
        {
            JpegExifOrientation.Normal => new PixelPoint(x, y),
            JpegExifOrientation.MirrorHorizontal => new PixelPoint((sourceSize.Width - 1) - x, y),
            JpegExifOrientation.Rotate180 => new PixelPoint((sourceSize.Width - 1) - x, (sourceSize.Height - 1) - y),
            JpegExifOrientation.MirrorVertical => new PixelPoint(x, (sourceSize.Height - 1) - y),
            JpegExifOrientation.Transpose => new PixelPoint(y, x),
            JpegExifOrientation.Rotate90Clockwise => new PixelPoint((sourceSize.Height - 1) - y, x),
            JpegExifOrientation.Transverse => new PixelPoint((sourceSize.Height - 1) - y, (sourceSize.Width - 1) - x),
            JpegExifOrientation.Rotate270Clockwise => new PixelPoint(y, (sourceSize.Width - 1) - x),
            _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Unsupported EXIF orientation."),
        };
    }

    private static PixelSize ApplyOrientation(PixelSize pixelSize, JpegExifOrientation orientation)
    {
        return OrientationSwapsAxes(orientation)
            ? new PixelSize(pixelSize.Height, pixelSize.Width)
            : pixelSize;
    }

    private static bool OrientationSwapsAxes(JpegExifOrientation orientation)
    {
        return orientation is JpegExifOrientation.Transpose
            or JpegExifOrientation.Rotate90Clockwise
            or JpegExifOrientation.Transverse
            or JpegExifOrientation.Rotate270Clockwise;
    }

    private readonly record struct ImageDecodeMetadata(PixelSize DisplayPixelSize, JpegExifOrientation Orientation);

    private enum JpegExifOrientation
    {
        Normal = 1,
        MirrorHorizontal = 2,
        Rotate180 = 3,
        MirrorVertical = 4,
        Transpose = 5,
        Rotate90Clockwise = 6,
        Transverse = 7,
        Rotate270Clockwise = 8,
    }

    private void SlideshowWindow_Opened(object? sender, EventArgs e)
    {
        _escapeTimer.Start();
        ShowCatalogLimitNoticeIfNeeded();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _escapeHeld = true;
            if (!_escapeStopwatch.IsRunning)
            {
                _escapeStopwatch.Start();
            }

            e.Handled = true;
            return;
        }

        if (StopPromptOverlay.IsVisible)
        {
            return;
        }

        if (_engine?.State == SlideshowEngineState.Paused)
        {
            return;
        }

        if (_engine is not null
            && _engine.State is SlideshowEngineState.Displaying or SlideshowEngineState.Transitioning
            && _inputFilter.ShouldInterrupt(e))
        {
            ShowStopPrompt();
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        _escapeHeld = false;
        _escapeStopwatch.Reset();
    }

    private void EscapeTimer_Tick(object? sender, EventArgs e)
    {
        if (_escapeHeld && _escapeStopwatch.Elapsed > EscapeHoldThreshold)
        {
            Close();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (StopPromptOverlay.IsVisible)
        {
            return;
        }

        if (_engine is not null && _engine.State is SlideshowEngineState.Displaying or SlideshowEngineState.Transitioning)
        {
            ShowStopPrompt();
            e.Handled = true;
        }
    }

    private async void SlideshowWindow_Closed(object? sender, EventArgs e)
    {
        Opened -= SlideshowWindow_Opened;
        Closed -= SlideshowWindow_Closed;
        _escapeTimer.Tick -= EscapeTimer_Tick;
        _escapeTimer.Stop();
        _catalogLimitNoticeTimer.Tick -= CatalogLimitNoticeTimer_Tick;
        _catalogLimitNoticeTimer.Stop();
        _escapeStopwatch.Reset();
        _escapeHeld = false;
        CatalogLimitNotice.IsVisible = false;

        try
        {
            await DisposePipelineAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsRecoverableShutdownFailure(ex))
        {
        }
    }

    private async Task DisposePipelineAsync()
    {
        if (_pipelineDisposed)
        {
            return;
        }

        _pipelineDisposed = true;

        _commandListener?.Dispose();
        _commandListener = null;

        if (_engine is not null)
        {
            _engine.StopRequested = null;
            await _engine.StopAsync().ConfigureAwait(true);
            _engine.Dispose();
            _engine = null;
        }

        await ReleaseImageSourcesAsync().ConfigureAwait(true);

        _preloader?.Dispose();
        _preloader = null;

        Content = null;
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);

        await ReclaimBitmapMemoryAsync().ConfigureAwait(true);
    }

    private async Task PresentFrameAsync(
        Bitmap bitmap,
        bool showPrimarySurface,
        ITransitionEffect effect,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var incoming = showPrimarySurface ? PrimaryImage : SecondaryImage;
        var outgoing = showPrimarySurface ? SecondaryImage : PrimaryImage;

        incoming.Source = bitmap;
        ResetFrameSurface(incoming, preserveSource: true);
        ResetFrameSurface(outgoing, preserveSource: true, opacity: outgoing.Source is null ? 0 : outgoing.Opacity);
        ApplyTransitionSurfaceOrder(effect, incoming, outgoing);

        try
        {
            await effect.ApplyAsync(
                outgoing,
                incoming,
                Math.Max(1, Bounds.Width),
                Math.Max(1, Bounds.Height),
                duration,
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestorePreviousFrame(incoming, outgoing);
            throw;
        }
        catch (Exception ex) when (IsRecoverableFrameFailure(ex))
        {
            RestorePreviousFrame(incoming, outgoing);
            throw;
        }
        finally
        {
            ResetFrameSurfaceOrder();
        }

        outgoing.Source = null;
        ResetFrameSurface(outgoing);
        ResetFrameSurface(incoming, preserveSource: true, opacity: 1);
        FramePresented?.Invoke(this, EventArgs.Empty);
    }

    private static void RestorePreviousFrame(Image incoming, Image outgoing)
    {
        ResetFrameSurface(incoming);
        ResetFrameSurface(outgoing, preserveSource: true, opacity: outgoing.Source is null ? 0 : 1);
    }

    private static void ResetFrameSurface(Image surface, bool preserveSource = false, double opacity = 0)
    {
        if (!preserveSource)
        {
            surface.Source = null;
        }

        surface.Opacity = opacity;
        surface.RenderTransform = null;
    }

    private void ApplyTransitionSurfaceOrder(ITransitionEffect effect, Image incoming, Image outgoing)
    {
        if (effect is CoverTransition)
        {
            incoming.ZIndex = 1;
            outgoing.ZIndex = 0;
            return;
        }

        if (effect is UncoverTransition)
        {
            outgoing.ZIndex = 1;
            incoming.ZIndex = 0;
            return;
        }

        if (effect is SmartSlideTransition)
        {
            outgoing.ZIndex = 1;
            incoming.ZIndex = 0;
            return;
        }

        ResetFrameSurfaceOrder();
    }

    private void ResetFrameSurfaceOrder()
    {
        PrimaryImage.ZIndex = 1;
        SecondaryImage.ZIndex = 0;
    }

    private static bool IsRecoverableFrameFailure(Exception exception)
    {
        exception = UnwrapRecoverableException(exception);

        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NullReferenceException
            or NotSupportedException;
    }

    private static bool IsRecoverableShutdownFailure(Exception exception)
    {
        return exception is ObjectDisposedException || IsRecoverableFrameFailure(exception);
    }

    private static Exception UnwrapRecoverableException(Exception exception)
    {
        while (true)
        {
            switch (exception)
            {
                case TargetInvocationException { InnerException: not null } targetInvocationException:
                    exception = targetInvocationException.InnerException!;
                    continue;
                case AggregateException { InnerExceptions.Count: 1 } aggregateException when aggregateException.InnerException is not null:
                    exception = aggregateException.InnerException!;
                    continue;
                default:
                    return exception;
            }
        }
    }

    private void ShowStopPrompt()
    {
        if (_engine is null
            || StopPromptOverlay.IsVisible
            || _engine.State is not (SlideshowEngineState.Displaying or SlideshowEngineState.Transitioning))
        {
            return;
        }

        StopPromptOverlay.IsVisible = true;
        _engine.Pause();
        StopNoButton.Focus();
    }

    private void HideStopPrompt()
    {
        StopPromptOverlay.IsVisible = false;
        _engine?.Resume();
    }

    private void StopYesButton_Click(object? sender, RoutedEventArgs e)
    {
        _engine?.RequestStop();
    }

    private void StopNoButton_Click(object? sender, RoutedEventArgs e)
    {
        HideStopPrompt();
    }

    private void HandleExternalCommand(SlideshowCommand command)
    {
        switch (command)
        {
            case SlideshowCommand.Pause:
                ShowStopPrompt();
                break;
            case SlideshowCommand.Resume:
                if (StopPromptOverlay.IsVisible)
                {
                    HideStopPrompt();
                }
                else
                {
                    _engine?.Resume();
                }

                break;
            case SlideshowCommand.Stop:
                _engine?.RequestStop();
                break;
            case SlideshowCommand.Next:
                _engine?.SkipToNext();
                break;
        }
    }

    private async Task ReleaseImageSourcesAsync()
    {
        CatalogLimitNotice.IsVisible = false;
        StopPromptOverlay.IsVisible = false;
        PrimaryImage.Source = null;
        SecondaryImage.Source = null;
        PrimaryImage.Opacity = 0;
        SecondaryImage.Opacity = 0;

        // Let Avalonia render the cleared surfaces before reclaiming the bitmaps
        // that backed the last presented frame.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
    }

    private static async Task ReclaimBitmapMemoryAsync()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        TryTrimAllocator();

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static void TryTrimAllocator()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            _ = MallocTrim(0);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    private void ShowCatalogLimitNoticeIfNeeded()
    {
        if (_catalog is null || !_catalog.IsTruncated)
        {
            return;
        }

        if (_catalog.CatalogLimit is not int catalogLimit)
        {
            return;
        }

        CatalogLimitNoticeText.Text = ImageCatalog.FormatCatalogLimitMessage(catalogLimit);
        CatalogLimitNotice.IsVisible = true;
        _catalogLimitNoticeTimer.Stop();
        _catalogLimitNoticeTimer.Start();
    }

    private void CatalogLimitNoticeTimer_Tick(object? sender, EventArgs e)
    {
        _catalogLimitNoticeTimer.Stop();
        CatalogLimitNotice.IsVisible = false;
    }

    private void OnStopRequested()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Close();
            return;
        }

        Dispatcher.UIThread.Post(Close);
    }
}

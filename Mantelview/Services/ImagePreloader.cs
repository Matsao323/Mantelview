using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Mantelview.Models;

namespace Mantelview.Services;

public sealed class ImagePreloader : IDisposable
{
    private readonly ImageCatalog _catalog;
    private readonly Func<PixelSize> _screenSizeProvider;
    private readonly Action<PixelSize>? _bitmapLoadObserver;
    private readonly CancellationTokenSource _disposeCancellationSource = new();
    private Task<PreloadedBitmap?>? _nextBitmapTask;
    private bool _isDisposed;
    private string? _nextPath;

    public ImagePreloader(ImageCatalog catalog, Func<PixelSize> screenSizeProvider, Action<PixelSize>? bitmapLoadObserver = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _screenSizeProvider = screenSizeProvider ?? throw new ArgumentNullException(nameof(screenSizeProvider));
        _bitmapLoadObserver = bitmapLoadObserver;
    }

    public Bitmap? Current { get; private set; }

    public Bitmap? Next { get; private set; }

    public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var current = LoadNextEligibleBitmap(cancellationToken);
        Current = current?.Bitmap;

        if (Current is null)
        {
            return Task.FromResult(false);
        }

        _nextBitmapTask = StartPreloadTask();
        return Task.FromResult(true);
    }

    public async Task<Bitmap?> PreloadNextAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (Next is not null)
        {
            return Next;
        }

        var preloadTask = _nextBitmapTask ??= StartPreloadTask();
        var preloadedBitmap = await AwaitPreloadAsync(preloadTask, cancellationToken).ConfigureAwait(false);
        Next = preloadedBitmap?.Bitmap;
        _nextPath = preloadedBitmap?.Path;

        if (ReferenceEquals(_nextBitmapTask, preloadTask))
        {
            _nextBitmapTask = null;
        }

        return Next;
    }

    public void CommitTransition()
    {
        ThrowIfDisposed();

        if (Next is null || string.IsNullOrWhiteSpace(_nextPath))
        {
            throw new InvalidOperationException("Cannot commit a transition without a preloaded bitmap.");
        }

        var previousCurrent = Current;
        Current = Next;
        Next = null;
        _nextPath = null;
        previousCurrent?.Dispose();
        _nextBitmapTask = StartPreloadTask();
    }

    public void RetirePendingPath()
    {
        ThrowIfDisposed();

        if (Next is null || string.IsNullOrWhiteSpace(_nextPath))
        {
            return;
        }

        var retiredPath = _nextPath;
        Next.Dispose();
        Next = null;
        _nextPath = null;
        _catalog.TryRetirePath(retiredPath);
        _nextBitmapTask = StartPreloadTask();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _disposeCancellationSource.Cancel();
        _disposeCancellationSource.Dispose();

        Current?.Dispose();
        Next?.Dispose();
        Current = null;
        Next = null;
        _nextPath = null;
        _nextBitmapTask = null;
    }

    private static async Task<PreloadedBitmap?> AwaitPreloadAsync(Task<PreloadedBitmap?> preloadTask, CancellationToken cancellationToken)
    {
        return cancellationToken.CanBeCanceled
            ? await preloadTask.WaitAsync(cancellationToken).ConfigureAwait(false)
            : await preloadTask.ConfigureAwait(false);
    }

    private PreloadedBitmap? LoadNextEligibleBitmap(CancellationToken cancellationToken)
    {
        string? firstAttemptedPath = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_catalog.TryGetNextPath(out var imagePath))
            {
                return null;
            }

            if (firstAttemptedPath is null)
            {
                firstAttemptedPath = imagePath;
            }
            else if (string.Equals(imagePath, firstAttemptedPath, StringComparison.Ordinal))
            {
                return null;
            }

            if (!ImageMagicByteValidator.HasValidMagicBytes(imagePath))
            {
                _catalog.TryRetirePath(imagePath);
                continue;
            }

            try
            {
                var screenSize = NormalizeScreenSize(_screenSizeProvider());
                _bitmapLoadObserver?.Invoke(screenSize);
                return new PreloadedBitmap(imagePath, SlideshowWindow.LoadBitmapForScreen(imagePath, screenSize));
            }
            catch (Exception ex) when (IsRecoverableImageFailure(ex))
            {
                _catalog.TryRetirePath(imagePath);
                continue;
            }
        }
    }

    private static PixelSize NormalizeScreenSize(PixelSize screenSize)
    {
        return new PixelSize(
            Math.Max(1, screenSize.Width),
            Math.Max(1, screenSize.Height));
    }

    private Task<PreloadedBitmap?> StartPreloadTask()
    {
        return Task.Run(() => LoadNextEligibleBitmap(_disposeCancellationSource.Token), _disposeCancellationSource.Token);
    }

    private static bool IsRecoverableImageFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or NullReferenceException
            or NotSupportedException;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private sealed record PreloadedBitmap(string Path, Bitmap Bitmap);
}

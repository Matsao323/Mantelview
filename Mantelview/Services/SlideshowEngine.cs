using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mantelview.Models;

namespace Mantelview.Services;

public delegate Task SlideshowFramePresenter(
    Bitmap bitmap,
    bool showPrimarySurface,
    ITransitionEffect effect,
    TimeSpan duration,
    CancellationToken cancellationToken);

public sealed class SlideshowEngine : IDisposable
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);

    private readonly SlideshowConfig _config;
    private readonly ImagePreloader _preloader;
    private readonly TransitionRegistry _transitionRegistry;
    private readonly SlideshowFramePresenter _framePresenter;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _watchdogTimer;
    private readonly CancellationTokenSource _stopCancellationSource = new();
    private CancellationTokenSource? _advanceCancellationSource;
    private Task _advanceTask = Task.CompletedTask;
    private bool _isDisposed;
    private bool _isStopping;
    private bool _showPrimarySurface = true;

    public SlideshowEngine(
        SlideshowConfig config,
        ImagePreloader preloader,
        TransitionRegistry transitionRegistry,
        SlideshowFramePresenter framePresenter)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _preloader = preloader ?? throw new ArgumentNullException(nameof(preloader));
        _transitionRegistry = transitionRegistry ?? throw new ArgumentNullException(nameof(transitionRegistry));
        _framePresenter = framePresenter ?? throw new ArgumentNullException(nameof(framePresenter));

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_config.GetEffectiveImageDurationSec()),
        };

        _watchdogTimer = new DispatcherTimer
        {
            Interval = WatchdogInterval,
        };

        _timer.Tick += Timer_Tick;
        _watchdogTimer.Tick += WatchdogTimer_Tick;
    }

    public SlideshowEngineState State { get; private set; }

    public Stopwatch StateStopwatch { get; } = new();

    public Action? StopRequested { get; set; }

#if DEBUG
    public static bool DebugStallEngine { get; set; }
#endif

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (State != SlideshowEngineState.Idle)
        {
            throw new InvalidOperationException("The slideshow engine can only be started from the idle state.");
        }

        if (_preloader.Current is null && !await _preloader.InitializeAsync(cancellationToken).ConfigureAwait(true))
        {
            return false;
        }

        if (_preloader.Current is null)
        {
            return false;
        }

        _showPrimarySurface = true;
        await _framePresenter(
            _preloader.Current,
            _showPrimarySurface,
            _transitionRegistry.CutEffect,
            TimeSpan.Zero,
            cancellationToken).ConfigureAwait(true);

        TransitionTo(SlideshowEngineState.Displaying);
        _timer.Start();
        _watchdogTimer.Start();
        return true;
    }

    public async Task StopAsync()
    {
        if (_isStopping)
        {
            await AwaitAdvanceTaskDuringStopAsync().ConfigureAwait(true);
            return;
        }

        _isStopping = true;
        _timer.Stop();
        _watchdogTimer.Stop();
        _stopCancellationSource.Cancel();
        _advanceCancellationSource?.Cancel();

        try
        {
            await AwaitAdvanceTaskDuringStopAsync().ConfigureAwait(true);
        }
        finally
        {
            if (State != SlideshowEngineState.Idle)
            {
                TransitionTo(SlideshowEngineState.Idle);
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Tick -= Timer_Tick;
        _watchdogTimer.Tick -= WatchdogTimer_Tick;
        _timer.Stop();
        _watchdogTimer.Stop();
        _advanceCancellationSource?.Cancel();
        _advanceCancellationSource?.Dispose();
        _advanceCancellationSource = null;
        _stopCancellationSource.Cancel();
        _stopCancellationSource.Dispose();
    }

    public void Pause()
    {
        ThrowIfDisposed();

        if (State is SlideshowEngineState.Idle or SlideshowEngineState.Paused)
        {
            return;
        }

        _timer.Stop();
        _watchdogTimer.Stop();

        if (State == SlideshowEngineState.Transitioning)
        {
            _advanceCancellationSource?.Cancel();
        }

        TransitionTo(SlideshowEngineState.Paused);
    }

    public void Resume()
    {
        ThrowIfDisposed();

        if (State != SlideshowEngineState.Paused)
        {
            return;
        }

        TransitionTo(SlideshowEngineState.Displaying);
        _timer.Start();
        _watchdogTimer.Start();
    }

    public void RequestStop()
    {
        ThrowIfDisposed();

        _timer.Stop();
        _watchdogTimer.Stop();
        _advanceCancellationSource?.Cancel();

        if (State != SlideshowEngineState.Idle)
        {
            TransitionTo(SlideshowEngineState.Idle);
        }

        StopRequested?.Invoke();
    }

    public void SkipToNext()
    {
        ThrowIfDisposed();

        if (State != SlideshowEngineState.Displaying || _isStopping || !_advanceTask.IsCompleted)
        {
            return;
        }

        BeginAdvance();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_isStopping || !_advanceTask.IsCompleted || State != SlideshowEngineState.Displaying)
        {
            return;
        }

        BeginAdvance();
    }

    private void WatchdogTimer_Tick(object? sender, EventArgs e)
    {
        if (State is not (SlideshowEngineState.Displaying or SlideshowEngineState.Transitioning))
        {
            return;
        }

        var threshold = TimeSpan.FromSeconds((_config.GetEffectiveImageDurationSec() * 3) + 30);
        if (StateStopwatch.Elapsed <= threshold)
        {
            return;
        }

        _timer.Stop();
        _watchdogTimer.Stop();
        _advanceCancellationSource?.Cancel();

        if (State != SlideshowEngineState.Idle)
        {
            TransitionTo(SlideshowEngineState.Idle);
        }

        StopRequested?.Invoke();
    }

    private void BeginAdvance()
    {
        _timer.Stop();
        _advanceCancellationSource?.Dispose();
        _advanceCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(_stopCancellationSource.Token);
        _advanceTask = AdvanceAsync(_advanceCancellationSource.Token);
    }

    private async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (State != SlideshowEngineState.Displaying)
            {
                return;
            }

            TransitionTo(SlideshowEngineState.Transitioning);

            var nextBitmap = await _preloader.PreloadNextAsync(cancellationToken).ConfigureAwait(true);

            if (nextBitmap is null)
            {
                TransitionTo(SlideshowEngineState.Displaying);
                return;
            }

            _showPrimarySurface = !_showPrimarySurface;
            var effect = _transitionRegistry.GetEffect(_config.IsEinkMode);
            await _framePresenter(
                nextBitmap,
                _showPrimarySurface,
                effect,
                GetTransitionDuration(effect),
                cancellationToken).ConfigureAwait(true);

            _preloader.CommitTransition();
            TransitionTo(SlideshowEngineState.Displaying);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsRecoverableFrameFailure(ex))
        {
            _preloader.RetirePendingPath();
            TransitionTo(SlideshowEngineState.Displaying);
        }
        finally
        {
            if (!_isStopping && State == SlideshowEngineState.Displaying)
            {
#if DEBUG
                if (!DebugStallEngine)
                {
                    _timer.Start();
                }
#else
                _timer.Start();
#endif

                _watchdogTimer.Start();
            }
        }
    }

    private TimeSpan GetTransitionDuration(ITransitionEffect effect)
    {
        return effect is CutTransition
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(_config.TransitionDurationSec);
    }

    private async Task AwaitAdvanceTaskDuringStopAsync()
    {
        try
        {
            await _advanceTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_stopCancellationSource.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (_stopCancellationSource.IsCancellationRequested && IsRecoverableShutdownFailure(ex))
        {
            // Closing from a corrupt-image runtime failure can race with an in-flight
            // preload/presentation task. During shutdown, treat those media faults as
            // already-aborted work so the launcher can return cleanly.
        }
    }

    private static bool IsRecoverableFrameFailure(Exception exception)
    {
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }

    private void TransitionTo(SlideshowEngineState state)
    {
        State = state;
        StateStopwatch.Restart();
    }
}

public enum SlideshowEngineState
{
    Idle,
    Displaying,
    Transitioning,
    Paused,
}

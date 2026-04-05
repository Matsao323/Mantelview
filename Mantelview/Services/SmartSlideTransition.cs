using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Mantelview.Services;

public sealed class SmartSlideTransition : TransitionAnimationEffectBase
{
    private const double FlushEdgeTolerance = 0.5;
    private readonly TransitionDirection _direction;

    public SmartSlideTransition(TransitionDirection direction)
    {
        _direction = direction;
    }

    public override string Name => TransitionRegistry.SmartSlideName;

    public override async Task ApplyAsync(
        Control outgoing,
        Control incoming,
        double width,
        double height,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (duration <= TimeSpan.Zero)
        {
            ClearRenderTransform(outgoing);
            ClearRenderTransform(incoming);
            outgoing.Opacity = 0;
            incoming.Opacity = 1;
            return;
        }

        var viewport = new Size(width, height);
        var incomingRect = GetContentRect(incoming, viewport);
        var outgoingRect = GetContentRect(outgoing, viewport);
        var isHorizontal = _direction is TransitionDirection.Left or TransitionDirection.Right;
        var viewportAxis = Math.Max(1, isHorizontal ? viewport.Width : viewport.Height);
        var incomingMargin = Math.Max(0, (viewportAxis - GetAxisSize(incomingRect, isHorizontal)) / 2);
        var outgoingMargin = Math.Max(0, (viewportAxis - GetAxisSize(outgoingRect, isHorizontal)) / 2);
        var approachCue = outgoingMargin / viewportAxis;
        var pushEndCue = 1 - (incomingMargin / viewportAxis);
        var (unitX, unitY) = GetDirectionUnitVector(_direction);
        var incomingStartDistance = viewportAxis - incomingMargin;
        var outgoingEndDistance = viewportAxis - outgoingMargin;
        var incomingStartX = -unitX * incomingStartDistance;
        var incomingStartY = -unitY * incomingStartDistance;

        InitializeTranslateTransform(outgoing, 0, 0);
        InitializeTranslateTransform(incoming, incomingStartX, incomingStartY);

        outgoing.Opacity = 1;
        if (outgoingMargin <= FlushEdgeTolerance)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        }

        incoming.Opacity = 1;

        await Task.WhenAll(
            RunAsync(
                outgoing,
                CreateTranslateAnimation(
                    0,
                    0,
                    0,
                    approachCue,
                    0,
                    0,
                    1,
                    unitX * outgoingEndDistance,
                    unitY * outgoingEndDistance,
                    duration),
                cancellationToken),
            RunAsync(
                incoming,
                CreateTranslateAnimation(
                    0,
                    incomingStartX,
                    incomingStartY,
                    pushEndCue,
                    0,
                    0,
                    1,
                    0,
                    0,
                    duration),
                cancellationToken)).ConfigureAwait(true);

        ClearRenderTransform(outgoing);
        ClearRenderTransform(incoming);
        outgoing.Opacity = 0;
        incoming.Opacity = 1;
    }

    private static Rect GetContentRect(Control control, Size viewport)
    {
        if (control is Image { Source: Avalonia.Media.Imaging.Bitmap bitmap })
        {
            return ComputeFittedRect(
                new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                viewport);
        }

        return new Rect(0, 0, viewport.Width, viewport.Height);
    }

    private static double GetAxisSize(Rect rect, bool isHorizontal)
    {
        return isHorizontal ? rect.Width : rect.Height;
    }
}

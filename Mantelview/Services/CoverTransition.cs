using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Mantelview.Services;

public sealed class CoverTransition : TransitionAnimationEffectBase
{
    private readonly TransitionDirection _direction;

    public CoverTransition(TransitionDirection direction)
    {
        _direction = direction;
    }

    public override string Name => TransitionRegistry.CoverName;

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

        outgoing.Opacity = 1;
        incoming.Opacity = 1;

        var (offsetX, offsetY) = GetMovementOffset(_direction, width, height);
        ClearRenderTransform(outgoing);
        InitializeTranslateTransform(incoming, -offsetX, -offsetY);

        await Task.WhenAll(
            RunAsync(
                outgoing,
                CreateOpacityAnimation(1, 0, duration),
                cancellationToken),
            RunAsync(
                incoming,
                CreateTranslateAnimation(-offsetX, 0, -offsetY, 0, duration),
                cancellationToken)).ConfigureAwait(true);

        ClearRenderTransform(outgoing);
        ClearRenderTransform(incoming);
        outgoing.Opacity = 0;
        incoming.Opacity = 1;
    }
}

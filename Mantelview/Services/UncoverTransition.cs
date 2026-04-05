using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Mantelview.Services;

public sealed class UncoverTransition : TransitionAnimationEffectBase
{
    private readonly TransitionDirection _direction;

    public UncoverTransition(TransitionDirection direction)
    {
        _direction = direction;
    }

    public override string Name => TransitionRegistry.UncoverName;

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
        incoming.Opacity = 0;

        var (offsetX, offsetY) = GetMovementOffset(_direction, width, height);
        InitializeTranslateTransform(outgoing, 0, 0);
        ClearRenderTransform(incoming);

        await Task.WhenAll(
            RunAsync(
                outgoing,
                CreateTranslateAnimation(0, offsetX, 0, offsetY, duration),
                cancellationToken),
            RunAsync(
                incoming,
                CreateOpacityAnimation(0, 1, duration),
                cancellationToken)).ConfigureAwait(true);

        ClearRenderTransform(outgoing);
        outgoing.Opacity = 0;
        incoming.Opacity = 1;
    }
}

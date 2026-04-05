using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Mantelview.Services;

public sealed class CrossfadeTransition : TransitionAnimationEffectBase
{
    public override string Name => TransitionRegistry.CrossfadeName;

    public override async Task ApplyAsync(
        Control outgoing,
        Control incoming,
        double width,
        double height,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ClearRenderTransform(outgoing);
        ClearRenderTransform(incoming);

        if (duration <= TimeSpan.Zero)
        {
            outgoing.Opacity = 0;
            incoming.Opacity = 1;
            return;
        }

        outgoing.Opacity = 1;
        incoming.Opacity = 0;

        await Task.WhenAll(
            RunAsync(outgoing, CreateOpacityAnimation(1, 0, duration), cancellationToken),
            RunAsync(incoming, CreateOpacityAnimation(0, 1, duration), cancellationToken)).ConfigureAwait(true);

        outgoing.Opacity = 0;
        incoming.Opacity = 1;
    }
}

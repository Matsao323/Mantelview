using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Mantelview.Services;

public sealed class FadeTransition : ITransitionEffect
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    public string Name => "Fade";

    public async Task ApplyAsync(Control outgoing, Control incoming, double width, double height, TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (duration <= TimeSpan.Zero)
        {
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
            return;
        }

        outgoing.Opacity = 1;
        incoming.Opacity = 0;

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            incoming.Opacity = progress;
            outgoing.Opacity = 1 - progress;

            if (progress >= 1)
            {
                break;
            }

            await Task.Delay(FrameInterval, cancellationToken).ConfigureAwait(true);
        }

        incoming.Opacity = 1;
        outgoing.Opacity = 0;
    }
}

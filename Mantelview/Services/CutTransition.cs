using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Mantelview.Services;

public sealed class CutTransition : ITransitionEffect
{
    public string Name => "Cut";

    public Task ApplyAsync(Control outgoing, Control incoming, double width, double height, TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        outgoing.Opacity = 0;
        incoming.Opacity = 1;
        return Task.CompletedTask;
    }
}

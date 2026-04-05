using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Mantelview.Services;

public enum TransitionDirection
{
    None,
    Left,
    Right,
    Up,
    Down,
}

public interface ITransitionEffect
{
    string Name { get; }

    Task ApplyAsync(Control outgoing, Control incoming, double width, double height, TimeSpan duration, CancellationToken cancellationToken);
}

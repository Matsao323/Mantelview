using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Mantelview.Services;

public abstract class TransitionAnimationEffectBase : ITransitionEffect
{
    private static readonly LinearEasing LinearEasing = new();

    public abstract string Name { get; }

    public abstract Task ApplyAsync(Control outgoing, Control incoming, double width, double height, TimeSpan duration, CancellationToken cancellationToken);

    protected static TranslateTransform InitializeTranslateTransform(Control control, double x, double y)
    {
        var transform = new TranslateTransform(x, y);
        control.RenderTransform = transform;
        return transform;
    }

    protected static void ClearRenderTransform(Control control)
    {
        control.RenderTransform = null;
    }

    protected static async Task RunAsync(Control control, Animation animation, CancellationToken cancellationToken)
    {
        await animation.RunAsync(control, cancellationToken).ConfigureAwait(true);
    }

    protected static Animation CreateOpacityAnimation(double fromOpacity, double toOpacity, TimeSpan duration)
    {
        return CreateAnimation(
            duration,
            CreateKeyFrame(0, new Setter(Visual.OpacityProperty, fromOpacity)),
            CreateKeyFrame(1, new Setter(Visual.OpacityProperty, toOpacity)));
    }

    protected static Animation CreateTranslateAnimation(double fromX, double toX, double fromY, double toY, TimeSpan duration)
    {
        return CreateAnimation(
            duration,
            CreateKeyFrame(
                0,
                new Setter(TranslateTransform.XProperty, fromX),
                new Setter(TranslateTransform.YProperty, fromY)),
            CreateKeyFrame(
                1,
                new Setter(TranslateTransform.XProperty, toX),
                new Setter(TranslateTransform.YProperty, toY)));
    }

    protected static Animation CreateTranslateAnimation(
        double cue1,
        double x1,
        double y1,
        double cue2,
        double x2,
        double y2,
        double cue3,
        double x3,
        double y3,
        TimeSpan duration)
    {
        return CreateAnimation(
            duration,
            CreateKeyFrame(
                cue1,
                new Setter(TranslateTransform.XProperty, x1),
                new Setter(TranslateTransform.YProperty, y1)),
            CreateKeyFrame(
                cue2,
                new Setter(TranslateTransform.XProperty, x2),
                new Setter(TranslateTransform.YProperty, y2)),
            CreateKeyFrame(
                cue3,
                new Setter(TranslateTransform.XProperty, x3),
                new Setter(TranslateTransform.YProperty, y3)));
    }

    protected static (double X, double Y) GetMovementOffset(TransitionDirection direction, double width, double height)
    {
        return direction switch
        {
            TransitionDirection.Left => (-width, 0),
            TransitionDirection.Right => (width, 0),
            TransitionDirection.Up => (0, -height),
            TransitionDirection.Down => (0, height),
            _ => (0, 0),
        };
    }

    protected static (double X, double Y) GetDirectionUnitVector(TransitionDirection direction)
    {
        return direction switch
        {
            TransitionDirection.Left => (-1, 0),
            TransitionDirection.Right => (1, 0),
            TransitionDirection.Up => (0, -1),
            TransitionDirection.Down => (0, 1),
            _ => (0, 0),
        };
    }

    protected static Rect ComputeFittedRect(Size image, Size viewport)
    {
        if (image.Width <= 0 || image.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return new Rect(0, 0, Math.Max(0, viewport.Width), Math.Max(0, viewport.Height));
        }

        var scale = Math.Min(viewport.Width / image.Width, viewport.Height / image.Height);
        var width = image.Width * scale;
        var height = image.Height * scale;
        return new Rect((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
    }

    private static Animation CreateAnimation(TimeSpan duration, params KeyFrame[] frames)
    {
        var animation = new Animation
        {
            Duration = duration,
            FillMode = FillMode.Forward,
            Easing = LinearEasing,
        };

        foreach (var frame in frames)
        {
            animation.Children.Add(frame);
        }

        return animation;
    }

    private static KeyFrame CreateKeyFrame(double cue, params Setter[] setters)
    {
        var frame = new KeyFrame
        {
            Cue = new Cue(cue),
        };

        foreach (var setter in setters)
        {
            frame.Setters.Add(setter);
        }

        return frame;
    }
}

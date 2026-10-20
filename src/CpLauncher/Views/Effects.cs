using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace CpLauncher.Views;

/// <summary>
/// The launcher's few animations: short, one-shot, started by an event (never looping, so the window does not
/// keep redrawing next to the game) and skipped when Windows has animation effects switched off. Transform
/// properties are animated on the control that owns the RenderTransform (Avalonia's transform animator).
/// </summary>
internal static class Effects
{
    /// <summary>Fades a picture in while it slides into place from <paramref name="fromX"/>, <paramref name="fromY"/>.</summary>
    public static void Entrance(Visual target, double fromX, double fromY, TimeSpan duration, TimeSpan delay)
    {
        if (!Motion.IsEnabled)
        {
            return;
        }

        if (target.RenderTransform is not TranslateTransform)
        {
            target.RenderTransform = new TranslateTransform();
        }

        var fade = new Animation
        {
            Duration = duration,
            Delay = delay,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Both,
            Children =
            {
                Frame(0, Visual.OpacityProperty, 0d),
                Frame(1, Visual.OpacityProperty, 1d),
            },
        };
        var slide = new Animation
        {
            Duration = duration,
            Delay = delay,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Both,
            Children =
            {
                Frame(0, TranslateTransform.XProperty, fromX, TranslateTransform.YProperty, fromY),
                Frame(1, TranslateTransform.XProperty, 0d, TranslateTransform.YProperty, 0d),
            },
        };
        Run(fade, target);
        Run(slide, target);
    }

    /// <summary>
    /// The state gem grows and glows once (0.5 s) when the state of Icon.utx changes. The gem's RenderTransform
    /// must be a <see cref="ScaleTransform"/>.
    /// </summary>
    public static void Flash(Visual gem, Visual glow)
    {
        if (!Motion.IsEnabled || gem.RenderTransform is not ScaleTransform)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(500);
        var grow = new Animation
        {
            Duration = duration,
            Easing = new CubicEaseOut(),
            Children =
            {
                Frame(0, ScaleTransform.ScaleXProperty, 1d, ScaleTransform.ScaleYProperty, 1d),
                Frame(0.35, ScaleTransform.ScaleXProperty, 1.6d, ScaleTransform.ScaleYProperty, 1.6d),
                Frame(1, ScaleTransform.ScaleXProperty, 1d, ScaleTransform.ScaleYProperty, 1d),
            },
        };
        Run(grow, gem);
        Pulse(glow, TimeSpan.Zero, duration);
    }

    /// <summary>Shows a hidden overlay for a moment: opacity 0 → 1 → 0, starting after <paramref name="delay"/>.</summary>
    public static void Pulse(Visual target, TimeSpan delay, TimeSpan duration)
    {
        if (!Motion.IsEnabled)
        {
            return;
        }

        var pulse = new Animation
        {
            Duration = duration,
            Delay = delay,
            Easing = new CubicEaseOut(),
            Children =
            {
                Frame(0, Visual.OpacityProperty, 0d),
                Frame(0.35, Visual.OpacityProperty, 1d),
                Frame(1, Visual.OpacityProperty, 0d),
            },
        };
        Run(pulse, target);
    }

    /// <summary>
    /// Starts an animation and ignores its failure: an effect is decoration, and a crash at startup would even make
    /// a freshly updated launcher roll itself back.
    /// </summary>
    private static void Run(Animation animation, Animatable target)
    {
        try
        {
            animation.RunAsync(target).ContinueWith(task =>
            {
                _ = task.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception)
        {
            // Decoration only; the page works the same without the animation.
        }
    }

    private static KeyFrame Frame(double cue, AvaloniaProperty property, object value)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };
        frame.Setters.Add(new Setter(property, value));
        return frame;
    }

    private static KeyFrame Frame(double cue, AvaloniaProperty first, object firstValue, AvaloniaProperty second, object secondValue)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };
        frame.Setters.Add(new Setter(first, firstValue));
        frame.Setters.Add(new Setter(second, secondValue));
        return frame;
    }
}

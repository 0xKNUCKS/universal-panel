// Timing and easing follow EarTrumpet (MIT), EarTrumpet/UI/Helpers/WindowAnimationLibrary.cs
// (QuinticEase ease-out entrance, activate + SetForegroundWindow on completion).
// https://github.com/File-New-Project/EarTrumpet @ f492b8fa6459d9c5b442ad07ea28d70e9db7f493
// Deviation: EarTrumpet animates Window.Left, which moves the HWND with SetWindowPos on every
// frame; with a DWM acrylic backdrop that stutters visibly. The window is now placed once at its
// final position and only the content is animated (TranslateTransform + opacity), which WPF
// composes on the GPU without touching the window. Skipped when the user turned animations off
// (SystemParameters.ClientAreaAnimation, SPI_GETCLIENTAREAANIMATION).

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UniversalPanel.Native;

namespace UniversalPanel.Shell;

internal static class PanelAnimator
{
    private const double SlideDistance = 56;
    private static readonly Duration EntranceDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(160));
    // The fade outlasts the slide so the content settles into view rather than snapping in.
    private static readonly Duration EntranceFadeDuration = new(TimeSpan.FromMilliseconds(450));
    private static readonly Duration ExitFadeDuration = new(TimeSpan.FromMilliseconds(240));

    public static void BeginEntrance(Window window, UIElement content, Action completed)
    {
        void OnCompleted()
        {
            window.Topmost = true;
            window.Focus();
            window.SetForegroundWindow();
            completed();
        }

        window.Activate();
        var slide = SlideOf(content);

        if (!SystemParameters.ClientAreaAnimation)
        {
            Stop(content, slide);
            window.Cloak(false);
            OnCompleted();
            return;
        }

        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var move = new DoubleAnimation(SlideDistance, 0, EntranceDuration) { EasingFunction = ease };
        var fade = new DoubleAnimation(0, 1, EntranceFadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fade.Completed += (_, _) => OnCompleted(); // the longer of the two

        // Start from the off-position before the window is uncloaked, so the first frame is right.
        slide.X = SlideDistance;
        content.Opacity = 0;
        window.Cloak(false);

        // SnapshotAndReplace (the default) takes over smoothly from a running exit animation.
        slide.BeginAnimation(TranslateTransform.XProperty, move);
        content.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public static void BeginExit(Window window, UIElement content, Action completed)
    {
        void OnCompleted()
        {
            window.Cloak();
            completed();
        }

        window.Topmost = false;
        var slide = SlideOf(content);

        if (!SystemParameters.ClientAreaAnimation)
        {
            OnCompleted();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(SlideDistance, ExitDuration) { EasingFunction = ease });

        var fade = new DoubleAnimation(0, ExitFadeDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fade.Completed += (_, _) => OnCompleted(); // the longer of the two
        content.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static TranslateTransform SlideOf(UIElement content)
    {
        if (content.RenderTransform is TranslateTransform existing)
        {
            return existing;
        }

        var slide = new TranslateTransform();
        content.RenderTransform = slide;
        return slide;
    }

    private static void Stop(UIElement content, TranslateTransform slide)
    {
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        content.BeginAnimation(UIElement.OpacityProperty, null);
        slide.X = 0;
        content.Opacity = 1;
    }
}

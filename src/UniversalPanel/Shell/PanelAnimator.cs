// Mirrors EarTrumpet (MIT), EarTrumpet/UI/Helpers/WindowAnimationLibrary.cs
// (BeginFlyoutEntranceAnimation / BeginFlyoutExitanimation)
// https://github.com/File-New-Project/EarTrumpet @ f492b8fa6459d9c5b442ad07ea28d70e9db7f493
// Changes: horizontal slide only (panel is always on the right edge). The opacity fade targets the
// window *content*, because Window.Opacity only works with AllowsTransparency=True, which we avoid
// (see docs/PITFALLS.md). Animations are skipped when the user turned them off, via WPF's
// SystemParameters.ClientAreaAnimation (SPI_GETCLIENTAREAANIMATION) instead of WinRT UISettings.

using System.Windows;
using System.Windows.Media.Animation;
using UniversalPanel.Native;

namespace UniversalPanel.Shell;

internal static class PanelAnimator
{
    private const double AnimationOffset = 40;
    private static readonly Duration EntranceDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(150));

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

        if (!SystemParameters.ClientAreaAnimation)
        {
            window.Cloak(false);
            OnCompleted();
            return;
        }

        double moveTo = window.Left;
        window.Left += AnimationOffset;

        var move = new DoubleAnimation
        {
            From = window.Left,
            To = moveTo,
            Duration = EntranceDuration,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(move, window);
        Storyboard.SetTargetProperty(move, new PropertyPath(Window.LeftProperty));

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = EntranceDuration,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, content);
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));

        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        storyboard.Children.Add(move);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) =>
        {
            // FillBehavior.Stop reverts to the base value; commit the final position.
            window.Left = moveTo;
            content.Opacity = 1;
            OnCompleted();
        };

        window.Cloak(false);
        storyboard.Begin(window);
    }

    public static void BeginExit(Window window, UIElement content, Action completed)
    {
        void OnCompleted()
        {
            window.Cloak();
            content.Opacity = 1;
            completed();
        }

        window.Topmost = false;

        if (!SystemParameters.ClientAreaAnimation)
        {
            OnCompleted();
            return;
        }

        var move = new DoubleAnimation
        {
            From = window.Left,
            To = window.Left + AnimationOffset,
            Duration = ExitDuration,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(move, window);
        Storyboard.SetTargetProperty(move, new PropertyPath(Window.LeftProperty));

        var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = ExitDuration,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };
        fade.Completed += (_, _) => content.Opacity = 0;
        Storyboard.SetTarget(fade, content);
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));

        var storyboard = new Storyboard { FillBehavior = FillBehavior.Stop };
        storyboard.Children.Add(move);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => OnCompleted();
        storyboard.Begin(window);
    }
}

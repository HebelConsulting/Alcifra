using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The eye of a secret (ADR 0968, the core's <c>SecretReveal</c>): the secret shows ONLY WHILE HELD — the mouse button
/// down on the eye, or Space/Enter held while it has focus — and hides again on release, when the pointer leaves, when
/// the capture is lost and when the eye loses focus. Attached to the eye control, bound to what it reveals.
/// </summary>
public static class HoldToReveal
{
    public static readonly AttachedProperty<IHoldToReveal?> TargetProperty =
        AvaloniaProperty.RegisterAttached<Control, IHoldToReveal?>("Target", typeof(HoldToReveal));

    public static void SetTarget(Control element, IHoldToReveal? value) => element.SetValue(TargetProperty, value);

    public static IHoldToReveal? GetTarget(Control element) => element.GetValue(TargetProperty);

    static HoldToReveal()
    {
        TargetProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.OldValue is null && e.NewValue is not null)
            {
                // Handled events too, and tunnelling: a Button marks its own press handled before a bubbling handler sees it.
                control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                control.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                control.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
                control.AddHandler(InputElement.PointerExitedEvent, OnExited, RoutingStrategies.Bubble, handledEventsToo: true);
                control.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
                control.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
                control.AddHandler(InputElement.LostFocusEvent, OnLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);
                control.DetachedFromVisualTree += (_, _) => GetTarget(control)?.Release();
            }
            else if (e.OldValue is IHoldToReveal previous)
            {
                previous.Release();   // the eye is re-pointed at another secret: the previous one hides
            }
        });
    }

    private static IHoldToReveal? Of(object? sender) => sender is Control control ? GetTarget(control) : null;

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            Of(sender)?.Press();
        }
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e) => Of(sender)?.Release();

    private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Of(sender)?.Release();

    private static void OnExited(object? sender, PointerEventArgs e) => Of(sender)?.Release();

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            Of(sender)?.Press();
            e.Handled = true;   // held, not clicked: the eye has no click action
        }
    }

    private static void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            Of(sender)?.Release();
            e.Handled = true;
        }
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e) => Of(sender)?.Release();
}

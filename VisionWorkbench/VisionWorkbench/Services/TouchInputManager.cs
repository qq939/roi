using System.Windows;
using System.Windows.Input;

namespace VisionWorkbench.Services;

public static class TouchInputManager
{
    public static void Initialize(Window window)
    {
        if (window == null) return;
        
        window.AddHandler(UIElement.TouchDownEvent, new EventHandler<TouchEventArgs>(OnTouchDown), true);
        window.AddHandler(UIElement.TouchUpEvent, new EventHandler<TouchEventArgs>(OnTouchUp), true);
    }

    private static void OnTouchDown(object sender, TouchEventArgs e)
    {
        var touchPoint = e.GetTouchPoint(null);
        var element = touchPoint.TouchDevice.DirectlyOver as UIElement;
        if (element == null) return;

        var mouseArgs = new MouseButtonEventArgs(
            Mouse.PrimaryDevice, 
            e.Timestamp, 
            MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = e.Source
        };

        element.RaiseEvent(mouseArgs);
    }

    private static void OnTouchUp(object sender, TouchEventArgs e)
    {
        var touchPoint = e.GetTouchPoint(null);
        var element = touchPoint.TouchDevice.DirectlyOver as UIElement;
        if (element == null) return;

        var mouseArgs = new MouseButtonEventArgs(
            Mouse.PrimaryDevice, 
            e.Timestamp, 
            MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            Source = e.Source
        };

        element.RaiseEvent(mouseArgs);
    }
}

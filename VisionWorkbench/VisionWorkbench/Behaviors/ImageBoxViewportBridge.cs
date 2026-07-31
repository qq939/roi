using System.ComponentModel;
using System.Reflection;
using System.Windows;

namespace VisionWorkbench.Behaviors;

public static class ImageBoxViewportBridge
{
    public static readonly DependencyProperty PreserveViewportProperty =
        DependencyProperty.RegisterAttached(
            "PreserveViewport",
            typeof(bool),
            typeof(ImageBoxViewportBridge),
            new PropertyMetadata(false, OnBridgePropertyChanged));

    public static readonly DependencyProperty ViewScaleProperty =
        DependencyProperty.RegisterAttached(
            "ViewScale",
            typeof(double),
            typeof(ImageBoxViewportBridge),
            new FrameworkPropertyMetadata(
                0.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBridgePropertyChanged));

    public static readonly DependencyProperty ViewOffsetXProperty =
        DependencyProperty.RegisterAttached(
            "ViewOffsetX",
            typeof(double),
            typeof(ImageBoxViewportBridge),
            new FrameworkPropertyMetadata(
                0.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBridgePropertyChanged));

    public static readonly DependencyProperty ViewOffsetYProperty =
        DependencyProperty.RegisterAttached(
            "ViewOffsetY",
            typeof(double),
            typeof(ImageBoxViewportBridge),
            new FrameworkPropertyMetadata(
                0.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBridgePropertyChanged));

    private static readonly DependencyProperty BridgeStateProperty =
        DependencyProperty.RegisterAttached(
            "BridgeState",
            typeof(ViewportBridgeState),
            typeof(ImageBoxViewportBridge),
            new PropertyMetadata(null));

    public static bool GetPreserveViewport(DependencyObject obj)
    {
        return (bool)obj.GetValue(PreserveViewportProperty);
    }

    public static void SetPreserveViewport(DependencyObject obj, bool value)
    {
        obj.SetValue(PreserveViewportProperty, value);
    }

    public static double GetViewScale(DependencyObject obj)
    {
        return (double)obj.GetValue(ViewScaleProperty);
    }

    public static void SetViewScale(DependencyObject obj, double value)
    {
        obj.SetValue(ViewScaleProperty, value);
    }

    public static double GetViewOffsetX(DependencyObject obj)
    {
        return (double)obj.GetValue(ViewOffsetXProperty);
    }

    public static void SetViewOffsetX(DependencyObject obj, double value)
    {
        obj.SetValue(ViewOffsetXProperty, value);
    }

    public static double GetViewOffsetY(DependencyObject obj)
    {
        return (double)obj.GetValue(ViewOffsetYProperty);
    }

    public static void SetViewOffsetY(DependencyObject obj, double value)
    {
        obj.SetValue(ViewOffsetYProperty, value);
    }

    private static void OnBridgePropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var state = GetOrCreateState(dependencyObject);
        EnsureTargetSubscriptions(dependencyObject, state);
        if (state.IsUpdatingAttached)
        {
            return;
        }

        ApplyAttachedValuesToTarget(dependencyObject, state);
    }

    private static ViewportBridgeState GetOrCreateState(DependencyObject dependencyObject)
    {
        if (dependencyObject.GetValue(BridgeStateProperty) is ViewportBridgeState state)
        {
            return state;
        }

        state = new ViewportBridgeState();
        dependencyObject.SetValue(BridgeStateProperty, state);
        return state;
    }

    private static void EnsureTargetSubscriptions(DependencyObject dependencyObject, ViewportBridgeState state)
    {
        if (state.TargetType == dependencyObject.GetType())
        {
            return;
        }

        state.TargetType = dependencyObject.GetType();
        state.ScaleDescriptor = DependencyPropertyDescriptor.FromName("ViewScale", state.TargetType, state.TargetType);
        state.OffsetXDescriptor = DependencyPropertyDescriptor.FromName("ViewOffsetX", state.TargetType, state.TargetType);
        state.OffsetYDescriptor = DependencyPropertyDescriptor.FromName("ViewOffsetY", state.TargetType, state.TargetType);

        state.ScaleDescriptor?.AddValueChanged(dependencyObject, (_, _) => CopyTargetValuesToAttached(dependencyObject, state));
        state.OffsetXDescriptor?.AddValueChanged(dependencyObject, (_, _) => CopyTargetValuesToAttached(dependencyObject, state));
        state.OffsetYDescriptor?.AddValueChanged(dependencyObject, (_, _) => CopyTargetValuesToAttached(dependencyObject, state));
    }

    private static void ApplyAttachedValuesToTarget(DependencyObject dependencyObject, ViewportBridgeState state)
    {
        if (state.IsUpdatingTarget)
        {
            return;
        }

        state.IsUpdatingTarget = true;
        try
        {
            TrySetTargetValue(dependencyObject, "PreserveViewport", GetPreserveViewport(dependencyObject));
            TrySetTargetValue(dependencyObject, "ViewScale", GetViewScale(dependencyObject));
            TrySetTargetValue(dependencyObject, "ViewOffsetX", GetViewOffsetX(dependencyObject));
            TrySetTargetValue(dependencyObject, "ViewOffsetY", GetViewOffsetY(dependencyObject));
        }
        finally
        {
            state.IsUpdatingTarget = false;
        }
    }

    private static void CopyTargetValuesToAttached(DependencyObject dependencyObject, ViewportBridgeState state)
    {
        if (state.IsUpdatingTarget || state.IsUpdatingAttached)
        {
            return;
        }

        state.IsUpdatingAttached = true;
        try
        {
            if (TryGetTargetDouble(dependencyObject, "ViewScale", out var scale))
            {
                dependencyObject.SetCurrentValue(ViewScaleProperty, scale);
            }

            if (TryGetTargetDouble(dependencyObject, "ViewOffsetX", out var offsetX))
            {
                dependencyObject.SetCurrentValue(ViewOffsetXProperty, offsetX);
            }

            if (TryGetTargetDouble(dependencyObject, "ViewOffsetY", out var offsetY))
            {
                dependencyObject.SetCurrentValue(ViewOffsetYProperty, offsetY);
            }
        }
        finally
        {
            state.IsUpdatingAttached = false;
        }
    }

    private static bool TrySetTargetValue(DependencyObject dependencyObject, string propertyName, object value)
    {
        var property = dependencyObject.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property == null || !property.CanWrite)
        {
            return false;
        }

        property.SetValue(dependencyObject, value);
        return true;
    }

    private static bool TryGetTargetDouble(DependencyObject dependencyObject, string propertyName, out double value)
    {
        value = 0;
        var property = dependencyObject.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(dependencyObject) is not double propertyValue ||
            !double.IsFinite(propertyValue))
        {
            return false;
        }

        value = propertyValue;
        return true;
    }

    private sealed class ViewportBridgeState
    {
        public Type? TargetType { get; set; }

        public DependencyPropertyDescriptor? ScaleDescriptor { get; set; }

        public DependencyPropertyDescriptor? OffsetXDescriptor { get; set; }

        public DependencyPropertyDescriptor? OffsetYDescriptor { get; set; }

        public bool IsUpdatingTarget { get; set; }

        public bool IsUpdatingAttached { get; set; }
    }
}

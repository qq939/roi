using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ImageBox;

public partial class ImageBoxControl : UserControl
{
    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.Register(
            nameof(ImageSource),
            typeof(ImageSource),
            typeof(ImageBoxControl),
            new PropertyMetadata(null, OnImageSourceChanged));

    public static readonly DependencyProperty OverlayItemsProperty =
        DependencyProperty.Register(
            nameof(OverlayItems),
            typeof(IEnumerable),
            typeof(ImageBoxControl),
            new PropertyMetadata(null, OnOverlayItemsChanged));

    public static readonly DependencyProperty InteractionModeProperty =
        DependencyProperty.Register(
            nameof(InteractionMode),
            typeof(ImageBoxInteractionMode),
            typeof(ImageBoxControl),
            new PropertyMetadata(ImageBoxInteractionMode.Pan, OnInteractionModeChanged));

    public static readonly DependencyProperty ShowInfoOverlayProperty =
        DependencyProperty.Register(
            nameof(ShowInfoOverlay),
            typeof(bool),
            typeof(ImageBoxControl),
            new PropertyMetadata(true, OnShowInfoOverlayChanged));

    private const double MinScale = 0.1;
    private const double MaxScale = 20.0;
    private const double ScaleRate = 1.2;
    private const double FitMargin = 0.98;
    private const double MinRoiEdgeLength = 2.0;
    private const double HandleScreenSize = 10.0;
    private const double RotateHandleScreenOffset = 28.0;

    private static readonly Geometry ResizeVisualCursorGeometry = Geometry.Parse(
        "M -10,0 L 10,0 M -10,0 L -5,-5 M -10,0 L -5,5 M 10,0 L 5,-5 M 10,0 L 5,5");

    private static readonly Geometry MoveVisualCursorGeometry = Geometry.Parse(
        "M -9,0 L 9,0 M -9,0 L -4,-5 M -9,0 L -4,5 M 9,0 L 4,-5 M 9,0 L 4,5 M 0,-9 L 0,9 M 0,-9 L -5,-4 M 0,-9 L 5,-4 M 0,9 L -5,4 M 0,9 L 5,4");

    private readonly List<Point> _drawingPolygonPoints = [];
    private Point _lastMousePosition;
    private Point? _rectangleStartImagePoint;
    private Point? _rotatedRectangleFirstImagePoint;
    private Point? _rotatedRectangleSecondImagePoint;
    private Point? _rotatedRectanglePreviewImagePoint;
    private RotatedRectangleDrawStage _rotatedRectangleDrawStage;
    private bool _isPanning;
    private double _currentScale = 1.0;
    private INotifyCollectionChanged? _overlayItemsNotifier;

    private enum RotatedRectangleDrawStage
    {
        None,
        WaitingSecondPoint,
        WaitingThirdPoint
    }

    private enum RoiEditHandle
    {
        Center,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomRight,
        BottomLeft,
        Rotate
    }

    private enum VisualCursorKind
    {
        Resize,
        Move
    }

    private sealed class EditableRectangleState
    {
        public required string Id { get; init; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public double Angle { get; set; }

        public Point Center => new(X + Width / 2.0, Y + Height / 2.0);

        public EditableRectangleState Snapshot() => new()
        {
            Id = Id,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            Angle = Angle
        };
    }

    private sealed class RoiEditDragState
    {
        public required RoiEditHandle Handle { get; init; }

        public required EditableRectangleState Start { get; init; }

        public required Point StartMouseImagePoint { get; init; }
    }

    public event EventHandler<ImagePointEventArgs>? ImageClicked;

    public event EventHandler<ImagePointEventArgs>? ImageMouseMoved;

    public event EventHandler<RoiDrawCompletedEventArgs>? RoiDrawCompleted;

    public event EventHandler<RoiDrawRejectedEventArgs>? RoiDrawRejected;

    public event EventHandler<OverlayItemSelectedEventArgs>? OverlayItemSelected;

    public event EventHandler<OverlayItemEditCompletedEventArgs>? OverlayItemEditCompleted;

    public double Scale
    {
        get => _currentScale;
        set
        {
            _currentScale = Math.Clamp(value, MinScale, MaxScale);
            UpdateImageScale();
            UpdateScaleInfo();
        }
    }

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public IEnumerable? OverlayItems
    {
        get => (IEnumerable?)GetValue(OverlayItemsProperty);
        set => SetValue(OverlayItemsProperty, value);
    }

    public ImageBoxInteractionMode InteractionMode
    {
        get => (ImageBoxInteractionMode)GetValue(InteractionModeProperty);
        set => SetValue(InteractionModeProperty, value);
    }

    public bool ShowInfoOverlay
    {
        get => (bool)GetValue(ShowInfoOverlayProperty);
        set => SetValue(ShowInfoOverlayProperty, value);
    }

    public bool ShowBrightnessInfo { get; set; } = true;

    public ImageBoxControl()
    {
        InitializeComponent();
        Focusable = true;
        PreviewKeyDown += ImageBoxControl_PreviewKeyDown;
        UpdateInfoOverlayVisibility();

        SizeChanged += (_, _) =>
        {
            if (HasImage())
            {
                FitImageToView();
            }
        };

        Loaded += (_, _) =>
        {
            UpdateOverlayItemsSubscription(null, OverlayItems);
            if (HasImage())
            {
                FitImageToView();
            }
        };
    }

    public void LoadImage(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(filePath);
        bitmap.EndInit();
        bitmap.Freeze();
        ImageSource = bitmap;
    }

    public void LoadImageFromBytes(byte[] imageData)
    {
        if (imageData.Length == 0)
        {
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(imageData);
        bitmap.EndInit();
        bitmap.Freeze();
        ImageSource = bitmap;
    }

    public void ResetView()
    {
        if (HasImage())
        {
            FitImageToView();
        }
    }

    public Point CanvasToImagePoint(Point canvasPoint) => TransformToImageCoordinates(canvasPoint);

    public bool IsImagePointVisible(Point imagePoint) => IsPointInImage(imagePoint);

    public void EnableBrightnessInfo(bool enable)
    {
        ShowBrightnessInfo = enable;
        UpdateInfoOverlayVisibility();
    }

    private void UpdateInfoOverlayVisibility()
    {
        InfoOverlayPanel.Visibility = ShowInfoOverlay ? Visibility.Visible : Visibility.Collapsed;
        BrightnessInfoText.Visibility = ShowInfoOverlay && ShowBrightnessInfo ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnImageSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not ImageBoxControl control)
        {
            return;
        }

        control.DisplayImage.Source = e.NewValue as ImageSource;
        control.ConfigureImageLayer();
    }

    private static void OnOverlayItemsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is ImageBoxControl control)
        {
            control.UpdateOverlayItemsSubscription(e.OldValue, e.NewValue);
            control.RenderOverlayItems();
        }
    }

    private static void OnInteractionModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is ImageBoxControl control)
        {
            control.CancelActiveDrawing();
        }
    }

    private static void OnShowInfoOverlayChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is ImageBoxControl control)
        {
            control.UpdateInfoOverlayVisibility();
        }
    }

    private void ConfigureImageLayer()
    {
        if (ImageSource == null)
        {
            ImageLayer.Width = double.NaN;
            ImageLayer.Height = double.NaN;
            OverlayCanvas.Children.Clear();
            UpdateCoordinateInfo(null);
            return;
        }

        ImageLayer.Width = ImageSource.Width;
        ImageLayer.Height = ImageSource.Height;
        OverlayCanvas.Width = ImageSource.Width;
        OverlayCanvas.Height = ImageSource.Height;
        RenderOverlayItems();
        Dispatcher.BeginInvoke(FitImageToView, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateOverlayItemsSubscription(object? oldValue, object? newValue)
    {
        if (_overlayItemsNotifier != null)
        {
            _overlayItemsNotifier.CollectionChanged -= OnOverlayItemsCollectionChanged;
        }

        _overlayItemsNotifier = newValue as INotifyCollectionChanged;
        if (_overlayItemsNotifier != null)
        {
            _overlayItemsNotifier.CollectionChanged += OnOverlayItemsCollectionChanged;
        }
    }

    private void OnOverlayItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderOverlayItems();
    }

    private bool HasImage() => ImageSource != null && ImageSource.Width > 0 && ImageSource.Height > 0;

    private void FitImageToView()
    {
        if (!HasImage())
        {
            return;
        }

        ResetTransforms();
        Canvas.SetLeft(ImageLayer, 0);
        Canvas.SetTop(ImageLayer, 0);

        var canvasWidth = MainCanvas.ActualWidth;
        var canvasHeight = MainCanvas.ActualHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return;
        }

        _currentScale = Math.Clamp(
            Math.Min(canvasWidth / ImageSource!.Width, canvasHeight / ImageSource.Height) * FitMargin,
            MinScale,
            MaxScale);

        UpdateImageScale();
        CenterImage();
        UpdateScaleInfo();
    }

    private void CenterImage()
    {
        if (!HasImage())
        {
            return;
        }

        var scaledWidth = ImageSource!.Width * _currentScale;
        var scaledHeight = ImageSource.Height * _currentScale;
        Canvas.SetLeft(ImageLayer, (MainCanvas.ActualWidth - scaledWidth) / 2);
        Canvas.SetTop(ImageLayer, (MainCanvas.ActualHeight - scaledHeight) / 2);
    }

    private void ResetTransforms()
    {
        _currentScale = 1.0;
        ImageScaleTransform.ScaleX = 1.0;
        ImageScaleTransform.ScaleY = 1.0;
        ImageTranslateTransform.X = 0;
        ImageTranslateTransform.Y = 0;
    }

    private void UpdateImageScale()
    {
        ImageScaleTransform.ScaleX = _currentScale;
        ImageScaleTransform.ScaleY = _currentScale;
        RenderOverlayItems();
    }

    private void UpdateScaleInfo()
    {
        ScaleInfoText.Text = $"缩放: {_currentScale * 100:0}%";
    }

    private void RenderOverlayItems()
    {
        HideVisualCursor();
        OverlayCanvas.Children.Clear();
        if (ImageSource == null)
        {
            return;
        }

        OverlayCanvas.Width = ImageSource.Width;
        OverlayCanvas.Height = ImageSource.Height;

        if (OverlayItems != null)
        {
            foreach (var item in OverlayItems.OfType<ImageOverlayItem>().Where(item => item.IsVisible))
            {
                var element = CreateOverlayElement(item);
                if (element != null)
                {
                    OverlayCanvas.Children.Add(element);
                }
            }
        }

        RenderDrawingPreview();
    }

    private FrameworkElement? CreateOverlayElement(ImageOverlayItem item)
    {
        if (InteractionMode == ImageBoxInteractionMode.Pan &&
            item.IsEditable &&
            !string.IsNullOrWhiteSpace(item.Id) &&
            item.Kind is ImageOverlayKind.Rectangle or ImageOverlayKind.RotatedRectangle)
        {
            return CreateEditableRectangle(item);
        }

        return item.Kind switch
        {
            ImageOverlayKind.Rectangle => CreateRectangle(item, rotate: false),
            ImageOverlayKind.RotatedRectangle => CreateRectangle(item, rotate: true),
            ImageOverlayKind.Line => CreateLine(item),
            ImageOverlayKind.Polyline => CreatePolyline(item),
            ImageOverlayKind.Polygon => CreatePolygon(item),
            ImageOverlayKind.Circle => CreateCircle(item),
            ImageOverlayKind.Cross => CreateCross(item),
            ImageOverlayKind.Text => CreateText(item),
            _ => null
        };
    }

    private static FrameworkElement CreateRectangle(ImageOverlayItem item, bool rotate)
    {
        var rectangle = new Rectangle
        {
            Width = Math.Max(0, item.Width),
            Height = Math.Max(0, item.Height),
            Stroke = item.Stroke,
            Fill = item.Fill,
            StrokeThickness = item.StrokeThickness,
            RenderTransformOrigin = new Point(0.5, 0.5),
            IsHitTestVisible = false
        };

        if (rotate)
        {
            rectangle.RenderTransform = new RotateTransform(item.Angle);
        }

        Canvas.SetLeft(rectangle, item.X);
        Canvas.SetTop(rectangle, item.Y);
        return rectangle;
    }

    private static FrameworkElement? CreateLine(ImageOverlayItem item)
    {
        if (item.Points.Count < 2)
        {
            return null;
        }

        return new Line
        {
            X1 = item.Points[0].X,
            Y1 = item.Points[0].Y,
            X2 = item.Points[1].X,
            Y2 = item.Points[1].Y,
            Stroke = item.Stroke,
            StrokeThickness = item.StrokeThickness,
            IsHitTestVisible = false
        };
    }

    private static FrameworkElement? CreatePolyline(ImageOverlayItem item)
    {
        if (item.Points.Count == 0)
        {
            return null;
        }

        return new Polyline
        {
            Points = new PointCollection(item.Points),
            Stroke = item.Stroke,
            StrokeThickness = item.StrokeThickness,
            Fill = item.Fill,
            IsHitTestVisible = false
        };
    }

    private static FrameworkElement? CreatePolygon(ImageOverlayItem item)
    {
        if (item.Points.Count == 0)
        {
            return null;
        }

        return new Polygon
        {
            Points = new PointCollection(item.Points),
            Stroke = item.Stroke,
            StrokeThickness = item.StrokeThickness,
            Fill = item.Fill,
            IsHitTestVisible = false
        };
    }

    private static FrameworkElement CreateCircle(ImageOverlayItem item)
    {
        var diameter = Math.Max(0, item.Radius * 2);
        var ellipse = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Stroke = item.Stroke,
            Fill = item.Fill,
            StrokeThickness = item.StrokeThickness,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(ellipse, item.X - item.Radius);
        Canvas.SetTop(ellipse, item.Y - item.Radius);
        return ellipse;
    }

    private static FrameworkElement CreateCross(ImageOverlayItem item)
    {
        var radius = item.Radius <= 0 ? 8 : item.Radius;
        var group = new Canvas
        {
            Width = radius * 2,
            Height = radius * 2,
            IsHitTestVisible = false
        };

        group.Children.Add(new Line
        {
            X1 = 0,
            Y1 = radius,
            X2 = radius * 2,
            Y2 = radius,
            Stroke = item.Stroke,
            StrokeThickness = item.StrokeThickness
        });
        group.Children.Add(new Line
        {
            X1 = radius,
            Y1 = 0,
            X2 = radius,
            Y2 = radius * 2,
            Stroke = item.Stroke,
            StrokeThickness = item.StrokeThickness
        });

        Canvas.SetLeft(group, item.X - radius);
        Canvas.SetTop(group, item.Y - radius);
        return group;
    }

    private static FrameworkElement CreateText(ImageOverlayItem item)
    {
        var text = new TextBlock
        {
            Text = item.Text ?? string.Empty,
            Foreground = item.Foreground,
            FontSize = item.FontSize,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(text, item.X);
        Canvas.SetTop(text, item.Y);
        return text;
    }

    private FrameworkElement CreateEditableRectangle(ImageOverlayItem item)
    {
        var state = new EditableRectangleState
        {
            Id = item.Id!,
            X = item.X,
            Y = item.Y,
            Width = Math.Max(MinRoiEdgeLength, item.Width),
            Height = Math.Max(MinRoiEdgeLength, item.Height),
            Angle = NormalizeAngle(item.Angle)
        };

        var group = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = false,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        var rotateTransform = new RotateTransform(state.Angle);
        group.RenderTransform = rotateTransform;
        Panel.SetZIndex(group, item.IsSelected ? 100 : 0);

        var body = new Rectangle
        {
            Stroke = item.Stroke,
            Fill = item.Fill,
            StrokeThickness = item.StrokeThickness,
            IsHitTestVisible = false
        };
        group.Children.Add(body);

        RoiEditDragState? dragState = null;
        Thumb? centerThumb = null;
        Thumb? leftThumb = null;
        Thumb? rightThumb = null;
        Thumb? topThumb = null;
        Thumb? bottomThumb = null;
        Thumb? topLeftThumb = null;
        Thumb? topRightThumb = null;
        Thumb? bottomRightThumb = null;
        Thumb? bottomLeftThumb = null;
        Thumb? rotateThumb = null;
        Rectangle? leftVisual = null;
        Rectangle? rightVisual = null;
        Rectangle? topVisual = null;
        Rectangle? bottomVisual = null;
        Rectangle? topLeftVisual = null;
        Rectangle? topRightVisual = null;
        Rectangle? bottomRightVisual = null;
        Rectangle? bottomLeftVisual = null;
        Line? rotateLine = null;
        Ellipse? rotateVisual = null;

        void SelectItem()
        {
            OverlayItemSelected?.Invoke(this, new OverlayItemSelectedEventArgs(state.Id));
        }

        void UpdateVisual()
        {
            state.Width = Math.Max(MinRoiEdgeLength, state.Width);
            state.Height = Math.Max(MinRoiEdgeLength, state.Height);
            state.Angle = NormalizeAngle(state.Angle);

            group.Width = state.Width;
            group.Height = state.Height;
            rotateTransform.Angle = state.Angle;
            Canvas.SetLeft(group, state.X);
            Canvas.SetTop(group, state.Y);

            body.Width = state.Width;
            body.Height = state.Height;

            var handle = GetHandleImageSize();
            var edge = Math.Max(handle * 0.45, 1.0 / Math.Max(_currentScale, 0.001));
            var rotateOffset = RotateHandleScreenOffset / Math.Max(_currentScale, 0.001);
            if (centerThumb != null)
            {
                centerThumb.Width = state.Width;
                centerThumb.Height = state.Height;
                Canvas.SetLeft(centerThumb, 0);
                Canvas.SetTop(centerThumb, 0);
            }

            UpdateEdgeVisual(leftVisual, leftThumb, edge, state.Height, -edge / 2.0, 0);
            UpdateEdgeVisual(rightVisual, rightThumb, edge, state.Height, state.Width - edge / 2.0, 0);
            UpdateEdgeVisual(topVisual, topThumb, state.Width, edge, 0, -edge / 2.0);
            UpdateEdgeVisual(bottomVisual, bottomThumb, state.Width, edge, 0, state.Height - edge / 2.0);
            UpdateCornerVisual(topLeftVisual, topLeftThumb, handle, -handle / 2.0, -handle / 2.0);
            UpdateCornerVisual(topRightVisual, topRightThumb, handle, state.Width - handle / 2.0, -handle / 2.0);
            UpdateCornerVisual(bottomRightVisual, bottomRightThumb, handle, state.Width - handle / 2.0, state.Height - handle / 2.0);
            UpdateCornerVisual(bottomLeftVisual, bottomLeftThumb, handle, -handle / 2.0, state.Height - handle / 2.0);

            if (rotateLine != null)
            {
                rotateLine.X1 = state.Width / 2.0;
                rotateLine.Y1 = 0;
                rotateLine.X2 = state.Width / 2.0;
                rotateLine.Y2 = -rotateOffset;
            }

            if (rotateVisual != null)
            {
                rotateVisual.Width = handle;
                rotateVisual.Height = handle;
                Canvas.SetLeft(rotateVisual, state.Width / 2.0 - handle / 2.0);
                Canvas.SetTop(rotateVisual, -rotateOffset - handle / 2.0);
            }

            if (rotateThumb != null)
            {
                rotateThumb.Width = handle * 1.8;
                rotateThumb.Height = handle * 1.8;
                Canvas.SetLeft(rotateThumb, state.Width / 2.0 - rotateThumb.Width / 2.0);
                Canvas.SetTop(rotateThumb, -rotateOffset - rotateThumb.Height / 2.0);
            }
        }

        group.MouseLeftButtonDown += (_, e) =>
        {
            if (IsFromThumb(e.OriginalSource as DependencyObject))
            {
                e.Handled = true;
                return;
            }

            SelectItem();
            e.Handled = true;
        };

        if (item.IsSelected)
        {
            centerThumb = CreateInvisibleThumb(Cursors.None);
            WireDrag(centerThumb, RoiEditHandle.Center, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);
            group.Children.Add(centerThumb);

            leftVisual = CreateHandleRectangle(Cursors.None);
            rightVisual = CreateHandleRectangle(Cursors.None);
            topVisual = CreateHandleRectangle(Cursors.None);
            bottomVisual = CreateHandleRectangle(Cursors.None);
            topLeftVisual = CreateHandleRectangle(Cursors.None);
            topRightVisual = CreateHandleRectangle(Cursors.None);
            bottomRightVisual = CreateHandleRectangle(Cursors.None);
            bottomLeftVisual = CreateHandleRectangle(Cursors.None);
            leftThumb = CreateInvisibleThumb(Cursors.None);
            rightThumb = CreateInvisibleThumb(Cursors.None);
            topThumb = CreateInvisibleThumb(Cursors.None);
            bottomThumb = CreateInvisibleThumb(Cursors.None);
            topLeftThumb = CreateInvisibleThumb(Cursors.None);
            topRightThumb = CreateInvisibleThumb(Cursors.None);
            bottomRightThumb = CreateInvisibleThumb(Cursors.None);
            bottomLeftThumb = CreateInvisibleThumb(Cursors.None);

            AddHandle(group, leftVisual, leftThumb, RoiEditHandle.Left, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);
            AddHandle(group, rightVisual, rightThumb, RoiEditHandle.Right, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);
            AddHandle(group, topVisual, topThumb, RoiEditHandle.Top, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);
            AddHandle(group, bottomVisual, bottomThumb, RoiEditHandle.Bottom, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);
            AddHandle(group, topLeftVisual, topLeftThumb, RoiEditHandle.TopLeft, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);
            AddHandle(group, topRightVisual, topRightThumb, RoiEditHandle.TopRight, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);
            AddHandle(group, bottomRightVisual, bottomRightThumb, RoiEditHandle.BottomRight, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);
            AddHandle(group, bottomLeftVisual, bottomLeftThumb, RoiEditHandle.BottomLeft, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);

            rotateLine = new Line
            {
                Stroke = item.Stroke,
                StrokeThickness = Math.Max(1.0 / Math.Max(_currentScale, 0.001), 0.5),
                IsHitTestVisible = false
            };
            rotateVisual = new Ellipse
            {
                Stroke = item.Stroke,
                Fill = Brushes.White,
                StrokeThickness = Math.Max(1.0 / Math.Max(_currentScale, 0.001), 0.5),
                IsHitTestVisible = false
            };
            rotateThumb = CreateInvisibleThumb(Cursors.Hand);
            group.Children.Add(rotateLine);
            group.Children.Add(rotateVisual);
            WireDrag(rotateThumb, RoiEditHandle.Rotate, () => dragState, value => dragState = value, state, UpdateVisual, item.Stroke);
            group.Children.Add(rotateThumb);
        }

        UpdateVisual();
        return group;
    }

    private double GetHandleImageSize() => HandleScreenSize / Math.Max(_currentScale, 0.001);

    private static Rectangle CreateHandleRectangle(Cursor cursor) => new()
    {
        Fill = Brushes.Transparent,
        Stroke = Brushes.Transparent,
        StrokeThickness = 0,
        Cursor = cursor,
        IsHitTestVisible = false
    };

    private static Thumb CreateInvisibleThumb(Cursor cursor)
    {
        var template = new ControlTemplate(typeof(Thumb));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        template.VisualTree = border;

        return new Thumb
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = cursor,
            Focusable = false,
            Template = template
        };
    }

    private static bool IsFromThumb(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Thumb)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static void UpdateEdgeVisual(
        Rectangle? visual,
        Thumb? thumb,
        double width,
        double height,
        double x,
        double y)
    {
        if (visual != null)
        {
            visual.Width = Math.Max(0, width);
            visual.Height = Math.Max(0, height);
            Canvas.SetLeft(visual, x);
            Canvas.SetTop(visual, y);
        }

        if (thumb != null)
        {
            thumb.Width = Math.Max(0, width);
            thumb.Height = Math.Max(0, height);
            Canvas.SetLeft(thumb, x);
            Canvas.SetTop(thumb, y);
        }
    }

    private static void UpdateCornerVisual(
        Rectangle? visual,
        Thumb? thumb,
        double size,
        double x,
        double y)
    {
        if (visual != null)
        {
            visual.Width = size;
            visual.Height = size;
            Canvas.SetLeft(visual, x);
            Canvas.SetTop(visual, y);
        }

        if (thumb != null)
        {
            thumb.Width = size;
            thumb.Height = size;
            Canvas.SetLeft(thumb, x);
            Canvas.SetTop(thumb, y);
        }
    }

    private void AddHandle(
        Panel group,
        Rectangle visual,
        Thumb thumb,
        RoiEditHandle handle,
        Func<RoiEditDragState?> getDragState,
        Action<RoiEditDragState?> setDragState,
        EditableRectangleState state,
        Action updateVisual,
        Brush cursorBrush)
    {
        group.Children.Add(visual);
        WireDrag(thumb, handle, getDragState, setDragState, state, updateVisual, cursorBrush);
        group.Children.Add(thumb);
    }

    private void WireDrag(
        Thumb thumb,
        RoiEditHandle handle,
        Func<RoiEditDragState?> getDragState,
        Action<RoiEditDragState?> setDragState,
        EditableRectangleState state,
        Action updateVisual,
        Brush cursorBrush)
    {
        thumb.MouseEnter += (_, _) => UpdateVisualCursor(handle, state, cursorBrush);
        thumb.MouseMove += (_, e) =>
        {
            var activeDrag = getDragState();
            if (activeDrag?.Handle == handle)
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    FinishDrag();
                    e.Handled = true;
                    return;
                }

                UpdateEditedRectangleState(state, activeDrag, TransformToImageCoordinates(Mouse.GetPosition(MainCanvas)));
                updateVisual();
                UpdateVisualCursor(handle, state, cursorBrush);
                e.Handled = true;
                return;
            }

            UpdateVisualCursor(handle, state, cursorBrush);
        };
        thumb.MouseLeave += (_, _) =>
        {
            if (getDragState()?.Handle != handle)
            {
                HideVisualCursor();
            }
        };
        thumb.PreviewMouseLeftButtonDown += (_, e) =>
        {
            setDragState(new RoiEditDragState
            {
                Handle = handle,
                Start = state.Snapshot(),
                StartMouseImagePoint = TransformToImageCoordinates(Mouse.GetPosition(MainCanvas))
            });
            thumb.CaptureMouse();
            UpdateVisualCursor(handle, state, cursorBrush);
            e.Handled = true;
        };
        thumb.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (getDragState()?.Handle == handle)
            {
                FinishDrag();
                e.Handled = true;
            }
        };
        thumb.LostMouseCapture += (_, _) =>
        {
            if (getDragState()?.Handle == handle)
            {
                FinishDrag();
            }
        };

        void FinishDrag()
        {
            var dragState = getDragState();
            if (dragState?.Handle != handle)
            {
                return;
            }

            setDragState(null);
            if (thumb.IsMouseCaptured)
            {
                thumb.ReleaseMouseCapture();
            }

            HideVisualCursor();
            OverlayItemEditCompleted?.Invoke(
                this,
                new OverlayItemEditCompletedEventArgs(
                    state.Id,
                    state.X,
                    state.Y,
                    state.Width,
                    state.Height,
                    state.Angle));
        }
    }

    private void UpdateVisualCursor(RoiEditHandle handle, EditableRectangleState state, Brush cursorBrush)
    {
        if (!TryGetVisualCursorInfo(handle, state, out var kind, out var angle))
        {
            HideVisualCursor();
            return;
        }

        var geometry = kind == VisualCursorKind.Move
            ? MoveVisualCursorGeometry
            : ResizeVisualCursorGeometry;
        var position = Mouse.GetPosition(VisualCursorLayer);
        var outerTransform = CreateVisualCursorTransform(angle, position);
        var innerTransform = CreateVisualCursorTransform(angle, position);

        VisualCursorOuter.Data = geometry;
        VisualCursorInner.Data = geometry;
        VisualCursorInner.Stroke = cursorBrush;
        VisualCursorOuter.RenderTransform = outerTransform;
        VisualCursorInner.RenderTransform = innerTransform;
        VisualCursorOuter.Visibility = Visibility.Visible;
        VisualCursorInner.Visibility = Visibility.Visible;
    }

    private void HideVisualCursor()
    {
        VisualCursorOuter.Visibility = Visibility.Collapsed;
        VisualCursorInner.Visibility = Visibility.Collapsed;
    }

    private static Transform CreateVisualCursorTransform(double angle, Point position)
    {
        var transform = new TransformGroup();
        transform.Children.Add(new RotateTransform(angle));
        transform.Children.Add(new TranslateTransform(position.X, position.Y));
        return transform;
    }

    private static bool TryGetVisualCursorInfo(
        RoiEditHandle handle,
        EditableRectangleState state,
        out VisualCursorKind kind,
        out double angle)
    {
        kind = VisualCursorKind.Resize;
        angle = 0;
        var diagonalAngle = Math.Atan2(
            Math.Max(MinRoiEdgeLength, state.Height),
            Math.Max(MinRoiEdgeLength, state.Width)) * 180.0 / Math.PI;

        switch (handle)
        {
            case RoiEditHandle.Center:
                kind = VisualCursorKind.Move;
                return true;
            case RoiEditHandle.Left:
            case RoiEditHandle.Right:
                angle = state.Angle;
                return true;
            case RoiEditHandle.Top:
            case RoiEditHandle.Bottom:
                angle = state.Angle + 90;
                return true;
            case RoiEditHandle.TopLeft:
            case RoiEditHandle.BottomRight:
                angle = state.Angle + diagonalAngle;
                return true;
            case RoiEditHandle.TopRight:
            case RoiEditHandle.BottomLeft:
                angle = state.Angle - diagonalAngle;
                return true;
            default:
                return false;
        }
    }

    private static void UpdateEditedRectangleState(
        EditableRectangleState state,
        RoiEditDragState drag,
        Point currentMouseImagePoint)
    {
        var start = drag.Start;
        var center = start.Center;
        var (u, v) = GetAxes(start.Angle);

        switch (drag.Handle)
        {
            case RoiEditHandle.Center:
                SetFromCenter(
                    state,
                    center + (currentMouseImagePoint - drag.StartMouseImagePoint),
                    start.Width,
                    start.Height,
                    start.Angle);
                break;

            case RoiEditHandle.Left:
                ResizeAlongAxis(
                    state,
                    center + u * (start.Width / 2.0),
                    currentMouseImagePoint,
                    u,
                    isPositiveSide: false,
                    start.Height,
                    start.Angle,
                    resizeWidth: true);
                break;

            case RoiEditHandle.Right:
                ResizeAlongAxis(
                    state,
                    center - u * (start.Width / 2.0),
                    currentMouseImagePoint,
                    u,
                    isPositiveSide: true,
                    start.Height,
                    start.Angle,
                    resizeWidth: true);
                break;

            case RoiEditHandle.Top:
                ResizeAlongAxis(
                    state,
                    center + v * (start.Height / 2.0),
                    currentMouseImagePoint,
                    v,
                    isPositiveSide: false,
                    start.Width,
                    start.Angle,
                    resizeWidth: false);
                break;

            case RoiEditHandle.Bottom:
                ResizeAlongAxis(
                    state,
                    center - v * (start.Height / 2.0),
                    currentMouseImagePoint,
                    v,
                    isPositiveSide: true,
                    start.Width,
                    start.Angle,
                    resizeWidth: false);
                break;

            case RoiEditHandle.TopLeft:
                ResizeFromCorner(state, center, currentMouseImagePoint, u, v, -1, -1, start.Width, start.Height, start.Angle);
                break;

            case RoiEditHandle.TopRight:
                ResizeFromCorner(state, center, currentMouseImagePoint, u, v, 1, -1, start.Width, start.Height, start.Angle);
                break;

            case RoiEditHandle.BottomRight:
                ResizeFromCorner(state, center, currentMouseImagePoint, u, v, 1, 1, start.Width, start.Height, start.Angle);
                break;

            case RoiEditHandle.BottomLeft:
                ResizeFromCorner(state, center, currentMouseImagePoint, u, v, -1, 1, start.Width, start.Height, start.Angle);
                break;

            case RoiEditHandle.Rotate:
                var startVector = drag.StartMouseImagePoint - center;
                var currentVector = currentMouseImagePoint - center;
                if (startVector.Length < 0.0001 || currentVector.Length < 0.0001)
                {
                    return;
                }

                var delta = Math.Atan2(currentVector.Y, currentVector.X) -
                            Math.Atan2(startVector.Y, startVector.X);
                SetFromCenter(
                    state,
                    center,
                    start.Width,
                    start.Height,
                    NormalizeAngle(start.Angle + delta * 180.0 / Math.PI));
                break;
        }
    }

    private static void ResizeFromCorner(
        EditableRectangleState state,
        Point startCenter,
        Point currentMouseImagePoint,
        Vector u,
        Vector v,
        int widthSign,
        int heightSign,
        double startWidth,
        double startHeight,
        double angle)
    {
        var anchor = startCenter -
                     u * (widthSign * startWidth / 2.0) -
                     v * (heightSign * startHeight / 2.0);
        var width = Math.Max(
            MinRoiEdgeLength,
            widthSign * Vector.Multiply(currentMouseImagePoint - anchor, u));
        var height = Math.Max(
            MinRoiEdgeLength,
            heightSign * Vector.Multiply(currentMouseImagePoint - anchor, v));
        var center = anchor +
                     u * (widthSign * width / 2.0) +
                     v * (heightSign * height / 2.0);

        SetFromCenter(state, center, width, height, angle);
    }

    private static void ResizeAlongAxis(
        EditableRectangleState state,
        Point anchor,
        Point currentMouseImagePoint,
        Vector axis,
        bool isPositiveSide,
        double fixedSize,
        double angle,
        bool resizeWidth)
    {
        var projected = isPositiveSide
            ? Vector.Multiply(currentMouseImagePoint - anchor, axis)
            : Vector.Multiply(anchor - currentMouseImagePoint, axis);
        var resized = Math.Max(MinRoiEdgeLength, projected);
        var center = isPositiveSide
            ? anchor + axis * (resized / 2.0)
            : anchor - axis * (resized / 2.0);

        if (resizeWidth)
        {
            SetFromCenter(state, center, resized, fixedSize, angle);
        }
        else
        {
            SetFromCenter(state, center, fixedSize, resized, angle);
        }
    }

    private static void SetFromCenter(
        EditableRectangleState state,
        Point center,
        double width,
        double height,
        double angle)
    {
        state.Width = Math.Max(MinRoiEdgeLength, width);
        state.Height = Math.Max(MinRoiEdgeLength, height);
        state.X = center.X - state.Width / 2.0;
        state.Y = center.Y - state.Height / 2.0;
        state.Angle = NormalizeAngle(angle);
    }

    private static (Vector U, Vector V) GetAxes(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var u = new Vector(Math.Cos(radians), Math.Sin(radians));
        var v = new Vector(-Math.Sin(radians), Math.Cos(radians));
        return (u, v);
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle <= -180)
        {
            angle += 360;
        }

        while (angle > 180)
        {
            angle -= 360;
        }

        return angle;
    }

    private void RenderDrawingPreview()
    {
        if (InteractionMode != ImageBoxInteractionMode.DrawRotatedRectangle ||
            _rotatedRectangleDrawStage == RotatedRectangleDrawStage.None ||
            _rotatedRectangleFirstImagePoint is not { } first ||
            _rotatedRectanglePreviewImagePoint is not { } preview)
        {
            return;
        }

        if (_rotatedRectangleDrawStage == RotatedRectangleDrawStage.WaitingSecondPoint)
        {
            OverlayCanvas.Children.Add(new Line
            {
                X1 = first.X,
                Y1 = first.Y,
                X2 = preview.X,
                Y2 = preview.Y,
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 1.4,
                IsHitTestVisible = false
            });
            return;
        }

        if (_rotatedRectangleSecondImagePoint is not { } second ||
            !RotatedRectangleDrawingGeometry.TryCreate(first, second, preview, MinRoiEdgeLength, out var corners))
        {
            return;
        }

        OverlayCanvas.Children.Add(new Polygon
        {
            Points = new PointCollection(corners),
            Stroke = Brushes.DeepSkyBlue,
            Fill = new SolidColorBrush(Color.FromArgb(32, 0, 191, 255)),
            StrokeThickness = 1.4,
            IsHitTestVisible = false
        });
    }

    private void UpdateCoordinateInfo(Point? canvasPoint)
    {
        if (!HasImage() || canvasPoint == null)
        {
            CoordinateInfoText.Text = "坐标: (-, -)";
            BrightnessInfoText.Text = "灰度: -";
            return;
        }

        var imagePosition = TransformToImageCoordinates(canvasPoint.Value);
        if (!IsPointInImage(imagePosition))
        {
            CoordinateInfoText.Text = "坐标: (-, -)";
            BrightnessInfoText.Text = "灰度: -";
            return;
        }

        var x = (int)Math.Round(imagePosition.X);
        var y = (int)Math.Round(imagePosition.Y);
        CoordinateInfoText.Text = $"坐标: ({x}, {y})";
        if (ShowBrightnessInfo)
        {
            BrightnessInfoText.Text = $"灰度: {GetPixelBrightness(x, y)}";
        }
    }

    private Point TransformToImageCoordinates(Point canvasPoint)
    {
        var imageLeft = Canvas.GetLeft(ImageLayer);
        var imageTop = Canvas.GetTop(ImageLayer);
        if (double.IsNaN(imageLeft))
        {
            imageLeft = 0;
        }

        if (double.IsNaN(imageTop))
        {
            imageTop = 0;
        }

        imageLeft += ImageTranslateTransform.X;
        imageTop += ImageTranslateTransform.Y;

        return new Point(
            (canvasPoint.X - imageLeft) / _currentScale,
            (canvasPoint.Y - imageTop) / _currentScale);
    }

    private bool IsPointInImage(Point point)
    {
        return HasImage() && point.X >= 0 && point.X < ImageSource!.Width && point.Y >= 0 && point.Y < ImageSource.Height;
    }

    private string GetPixelBrightness(int x, int y)
    {
        if (DisplayImage.Source is not BitmapSource bitmapSource ||
            x < 0 ||
            x >= bitmapSource.PixelWidth ||
            y < 0 ||
            y >= bitmapSource.PixelHeight)
        {
            return "-";
        }

        var source = bitmapSource.Format == PixelFormats.Bgra32
            ? bitmapSource
            : new FormatConvertedBitmap(bitmapSource, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride];
        source.CopyPixels(new Int32Rect(0, y, source.PixelWidth, 1), pixels, stride, 0);
        var index = x * 4;
        var b = pixels[index];
        var g = pixels[index + 1];
        var r = pixels[index + 2];
        return $"R:{r}, G:{g}, B:{b}";
    }

    private void ImageBoxControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HasActiveDrawing())
        {
            CancelActiveDrawing();
            e.Handled = true;
        }
    }

    private bool HasActiveDrawing() =>
        _rectangleStartImagePoint != null ||
        _drawingPolygonPoints.Count > 0 ||
        _rotatedRectangleDrawStage != RotatedRectangleDrawStage.None;

    private void CancelActiveDrawing()
    {
        HideVisualCursor();
        _rectangleStartImagePoint = null;
        _drawingPolygonPoints.Clear();
        _rotatedRectangleFirstImagePoint = null;
        _rotatedRectangleSecondImagePoint = null;
        _rotatedRectanglePreviewImagePoint = null;
        _rotatedRectangleDrawStage = RotatedRectangleDrawStage.None;
        _isPanning = false;
        MainCanvas.Cursor = Cursors.Arrow;
        if (MainCanvas.IsMouseCaptured)
        {
            MainCanvas.ReleaseMouseCapture();
        }

        RenderOverlayItems();
    }

    private Point ClampToImageBounds(Point imagePoint)
    {
        if (!HasImage())
        {
            return imagePoint;
        }

        return new Point(
            Math.Clamp(imagePoint.X, 0, ImageSource!.Width),
            Math.Clamp(imagePoint.Y, 0, ImageSource.Height));
    }

    private void HandleRotatedRectangleClick(Point imagePoint)
    {
        var point = ClampToImageBounds(imagePoint);
        if (_rotatedRectangleDrawStage == RotatedRectangleDrawStage.None)
        {
            _rotatedRectangleFirstImagePoint = point;
            _rotatedRectangleSecondImagePoint = null;
            _rotatedRectanglePreviewImagePoint = point;
            _rotatedRectangleDrawStage = RotatedRectangleDrawStage.WaitingSecondPoint;
            RenderOverlayItems();
            return;
        }

        if (_rotatedRectangleDrawStage == RotatedRectangleDrawStage.WaitingSecondPoint)
        {
            if (_rotatedRectangleFirstImagePoint is not { } first ||
                (point - first).Length < MinRoiEdgeLength)
            {
                RoiDrawRejected?.Invoke(this, new RoiDrawRejectedEventArgs("ROI 第一条边太短，请重新点击第二个点"));
                return;
            }

            _rotatedRectangleSecondImagePoint = point;
            _rotatedRectanglePreviewImagePoint = point;
            _rotatedRectangleDrawStage = RotatedRectangleDrawStage.WaitingThirdPoint;
            RenderOverlayItems();
            return;
        }

        if (_rotatedRectangleFirstImagePoint is not { } start ||
            _rotatedRectangleSecondImagePoint is not { } second ||
            !RotatedRectangleDrawingGeometry.TryCreate(start, second, point, MinRoiEdgeLength, out var corners))
        {
            RoiDrawRejected?.Invoke(this, new RoiDrawRejectedEventArgs("ROI 高度太小，请点击离第一条边更远的位置"));
            return;
        }

        CancelActiveDrawing();
        RoiDrawCompleted?.Invoke(
            this,
            new RoiDrawCompletedEventArgs(ImageBoxInteractionMode.DrawRotatedRectangle, corners));
    }

    private void UpdateRotatedRectanglePreview(Point imagePoint)
    {
        if (_rotatedRectangleDrawStage == RotatedRectangleDrawStage.None)
        {
            return;
        }

        _rotatedRectanglePreviewImagePoint = ClampToImageBounds(imagePoint);
        RenderOverlayItems();
    }

    private bool TryHandleRotatedRectangleMouseDown(MouseButtonEventArgs e)
    {
        if (!HasImage() || InteractionMode != ImageBoxInteractionMode.DrawRotatedRectangle)
        {
            return false;
        }

        Focus();
        MainCanvas.Focus();
        var canvasPoint = e.GetPosition(MainCanvas);
        var imagePoint = TransformToImageCoordinates(canvasPoint);
        if (!IsPointInImage(imagePoint))
        {
            return false;
        }

        ImageClicked?.Invoke(this, new ImagePointEventArgs(imagePoint));
        HandleRotatedRectangleClick(imagePoint);
        return true;
    }

    private void MainCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasImage())
        {
            return;
        }

        var mousePosition = e.GetPosition(MainCanvas);
        var oldScale = _currentScale;
        _currentScale = Math.Clamp(e.Delta > 0 ? _currentScale * ScaleRate : _currentScale / ScaleRate, MinScale, MaxScale);
        UpdateImageScale();
        UpdateScaleInfo();

        var imageLeft = Canvas.GetLeft(ImageLayer);
        var imageTop = Canvas.GetTop(ImageLayer);
        if (double.IsNaN(imageLeft))
        {
            imageLeft = 0;
        }

        if (double.IsNaN(imageTop))
        {
            imageTop = 0;
        }

        var imagePosition = new Point(imageLeft + ImageTranslateTransform.X, imageTop + ImageTranslateTransform.Y);
        var relativePosition = mousePosition - imagePosition;
        var scaleFactor = _currentScale / oldScale;
        ImageTranslateTransform.X += mousePosition.X - (relativePosition.X * scaleFactor) - imagePosition.X;
        ImageTranslateTransform.Y += mousePosition.Y - (relativePosition.Y * scaleFactor) - imagePosition.Y;
        UpdateCoordinateInfo(mousePosition);
        e.Handled = true;
    }

    private void MainCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (InteractionMode != ImageBoxInteractionMode.DrawRotatedRectangle)
        {
            return;
        }

        if (TryHandleRotatedRectangleMouseDown(e))
        {
            e.Handled = true;
        }
    }

    private void MainCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasImage())
        {
            return;
        }

        if (IsFromThumb(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        Focus();
        MainCanvas.Focus();
        var canvasPoint = e.GetPosition(MainCanvas);
        var imagePoint = TransformToImageCoordinates(canvasPoint);
        if (!IsPointInImage(imagePoint))
        {
            return;
        }

        if (e.ClickCount == 2 && InteractionMode != ImageBoxInteractionMode.DrawRotatedRectangle)
        {
            if (InteractionMode == ImageBoxInteractionMode.DrawPolygon && _drawingPolygonPoints.Count >= 3)
            {
                RoiDrawCompleted?.Invoke(this, new RoiDrawCompletedEventArgs(ImageBoxInteractionMode.DrawPolygon, _drawingPolygonPoints.ToArray()));
                _drawingPolygonPoints.Clear();
            }
            else
            {
                FitImageToView();
            }

            e.Handled = true;
            return;
        }

        ImageClicked?.Invoke(this, new ImagePointEventArgs(imagePoint));

        switch (InteractionMode)
        {
            case ImageBoxInteractionMode.DrawRectangle:
                _rectangleStartImagePoint = imagePoint;
                MainCanvas.CaptureMouse();
                break;
            case ImageBoxInteractionMode.DrawRotatedRectangle:
                TryHandleRotatedRectangleMouseDown(e);
                break;
            case ImageBoxInteractionMode.DrawPolygon:
                _drawingPolygonPoints.Add(imagePoint);
                break;
            case ImageBoxInteractionMode.PickPoint:
                break;
            default:
                _lastMousePosition = canvasPoint;
                _isPanning = true;
                MainCanvas.Cursor = Cursors.Hand;
                MainCanvas.CaptureMouse();
                break;
        }

        e.Handled = true;
    }

    private void MainCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (HasActiveDrawing())
        {
            CancelActiveDrawing();
            e.Handled = true;
        }
    }

    private void MainCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_rectangleStartImagePoint is { } start && InteractionMode == ImageBoxInteractionMode.DrawRectangle)
        {
            var end = TransformToImageCoordinates(e.GetPosition(MainCanvas));
            if (Math.Abs(end.X - start.X) >= 2 && Math.Abs(end.Y - start.Y) >= 2)
            {
                RoiDrawCompleted?.Invoke(
                    this,
                    new RoiDrawCompletedEventArgs(
                        ImageBoxInteractionMode.DrawRectangle,
                        [
                            new Point(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)),
                            new Point(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y))
                        ]));
            }
        }

        _rectangleStartImagePoint = null;
        _isPanning = false;
        MainCanvas.Cursor = Cursors.Arrow;
        MainCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void MainCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var mousePosition = e.GetPosition(MainCanvas);
        var imagePoint = HasImage() ? TransformToImageCoordinates(mousePosition) : new Point();
        UpdateCoordinateInfo(mousePosition);

        if (HasImage() && IsPointInImage(imagePoint))
        {
            ImageMouseMoved?.Invoke(this, new ImagePointEventArgs(imagePoint));
        }

        if (HasImage() &&
            InteractionMode == ImageBoxInteractionMode.DrawRotatedRectangle &&
            _rotatedRectangleDrawStage != RotatedRectangleDrawStage.None)
        {
            UpdateRotatedRectanglePreview(imagePoint);
            e.Handled = true;
            return;
        }

        if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
        {
            var moveVector = mousePosition - _lastMousePosition;
            ImageTranslateTransform.X += moveVector.X;
            ImageTranslateTransform.Y += moveVector.Y;
            _lastMousePosition = mousePosition;
            e.Handled = true;
        }
    }
}

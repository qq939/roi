namespace RoiAlignment.Core;

public static class RoiGeometry
{
    public static Point2fDto[] FromXywha(Xywha xywha)
    {
        if (xywha.Width < 0 || xywha.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(xywha), "Width and height must be non-negative.");
        }

        var radians = DegreesToRadians(xywha.AngleDegrees);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var halfWidth = xywha.Width / 2.0;
        var halfHeight = xywha.Height / 2.0;

        var local = new[]
        {
            (-halfWidth, -halfHeight),
            (halfWidth, -halfHeight),
            (halfWidth, halfHeight),
            (-halfWidth, halfHeight)
        };

        return local
            .Select(point =>
            {
                var x = xywha.X + point.Item1 * cos - point.Item2 * sin;
                var y = xywha.Y + point.Item1 * sin + point.Item2 * cos;
                return new Point2fDto(x, y);
            })
            .ToArray();
    }

    public static Xywha ToXywha(IReadOnlyList<Point2fDto> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 4)
        {
            throw new ArgumentException("At least four points are required to convert ROI points to xywha.", nameof(points));
        }

        var ordered = OrderCorners(points.Take(4).ToArray());
        var topWidth = Distance(ordered[0], ordered[1]);
        var bottomWidth = Distance(ordered[3], ordered[2]);
        var rightHeight = Distance(ordered[1], ordered[2]);
        var leftHeight = Distance(ordered[0], ordered[3]);

        var centerX = ordered.Average(point => point.X);
        var centerY = ordered.Average(point => point.Y);
        var width = (topWidth + bottomWidth) / 2.0;
        var height = (rightHeight + leftHeight) / 2.0;
        var angle = RadiansToDegrees(Math.Atan2(
            ordered[1].Y - ordered[0].Y,
            ordered[1].X - ordered[0].X));

        return new Xywha(centerX, centerY, width, height, NormalizeAngle(angle));
    }

    public static double PolygonArea(IReadOnlyList<Point2fDto> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        var area = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }

        return Math.Abs(area) / 2.0;
    }

    private static Point2fDto[] OrderCorners(Point2fDto[] points)
    {
        var centerX = points.Average(point => point.X);
        var centerY = points.Average(point => point.Y);

        var sorted = points
            .Select(point => new
            {
                Point = point,
                Angle = Math.Atan2(point.Y - centerY, point.X - centerX)
            })
            .OrderBy(item => item.Angle)
            .Select(item => item.Point)
            .ToArray();

        var topLeftIndex = 0;
        var bestScore = double.PositiveInfinity;
        for (var i = 0; i < sorted.Length; i++)
        {
            var score = sorted[i].X + sorted[i].Y;
            if (score < bestScore)
            {
                bestScore = score;
                topLeftIndex = i;
            }
        }

        var ordered = new Point2fDto[4];
        for (var i = 0; i < 4; i++)
        {
            ordered[i] = sorted[(topLeftIndex + i) % 4];
        }

        if (SignedArea(ordered) < 0)
        {
            ordered = [ordered[0], ordered[3], ordered[2], ordered[1]];
        }

        return ordered;
    }

    private static double SignedArea(IReadOnlyList<Point2fDto> points)
    {
        var area = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }

        return area / 2.0;
    }

    private static double Distance(Point2fDto first, Point2fDto second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

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
}

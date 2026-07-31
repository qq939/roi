using System.Windows;

namespace ImageBox;

internal static class RotatedRectangleDrawingGeometry
{
    public static bool TryCreate(
        Point first,
        Point second,
        Point thirdHint,
        double minimumEdgeLength,
        out Point[] corners)
    {
        corners = [];

        var edge = second - first;
        if (edge.Length < minimumEdgeLength)
        {
            return false;
        }

        edge.Normalize();
        var normal = new Vector(-edge.Y, edge.X);
        var signedHeight = Vector.Multiply(thirdHint - first, normal);
        if (Math.Abs(signedHeight) < minimumEdgeLength)
        {
            return false;
        }

        var offset = normal * signedHeight;
        corners =
        [
            first,
            second,
            second + offset,
            first + offset
        ];
        return true;
    }
}

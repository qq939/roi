namespace ClipInspect.Matching;

public static class VectorMath
{
    public static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Vector dimensions must match.");
        }

        var sum = 0f;
        for (var i = 0; i < left.Length; i++)
        {
            sum += left[i] * right[i];
        }

        return sum;
    }

    public static float[] NormalizeCopy(ReadOnlySpan<float> source)
    {
        var normSquared = 0.0;
        for (var i = 0; i < source.Length; i++)
        {
            normSquared += source[i] * source[i];
        }

        if (normSquared <= 0)
        {
            throw new ArgumentException("Feature vector cannot be empty or all zeros.");
        }

        var norm = Math.Sqrt(normSquared);
        var result = new float[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            result[i] = (float)(source[i] / norm);
        }

        return result;
    }
}

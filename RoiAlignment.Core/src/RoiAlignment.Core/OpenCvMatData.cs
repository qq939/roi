using System.Runtime.InteropServices;
using OpenCvSharp;

namespace RoiAlignment.Core;

internal static class OpenCvMatData
{
    public static DescriptorData FromMat(Mat mat)
    {
        if (mat.Empty())
        {
            return DescriptorData.Empty;
        }

        using var continuous = mat.Clone();
        var byteCount = checked((int)(continuous.Total() * continuous.ElemSize()));
        var buffer = new byte[byteCount];
        Marshal.Copy(continuous.Data, buffer, 0, byteCount);

        return new DescriptorData
        {
            Rows = continuous.Rows,
            Cols = continuous.Cols,
            MatType = (int)continuous.Type(),
            DataBase64 = Convert.ToBase64String(buffer)
        };
    }

    public static Mat ToMat(DescriptorData data)
    {
        if (data.IsEmpty)
        {
            return new Mat();
        }

        var buffer = Convert.FromBase64String(data.DataBase64);
        var mat = new Mat(data.Rows, data.Cols, data.MatType);
        var expectedByteCount = checked((int)(mat.Total() * mat.ElemSize()));
        if (buffer.Length != expectedByteCount)
        {
            mat.Dispose();
            throw new InvalidOperationException("Descriptor data size does not match descriptor metadata.");
        }

        Marshal.Copy(buffer, 0, mat.Data, buffer.Length);
        return mat;
    }
}

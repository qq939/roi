using ClipInspect.Core;
using ClipInspect.Matching;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ClipInspect.Onnx;

public sealed class OnnxClipImageEncoder : IImageFeatureEncoder, IDisposable
{
    private const int ImageSize = 224;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    public string ModelPath { get; }

    public OnnxClipImageEncoder(string modelPath)
    {
        ModelPath = modelPath;
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    public ValueTask<float[]> EncodeImageAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var input = PreprocessImage(imagePath);
        using var results = _session.Run(
            new[]
            {
                NamedOnnxValue.CreateFromTensor(_inputName, input)
            });

        var output = results.First(item => item.Name == _outputName).AsEnumerable<float>().ToArray();
        return ValueTask.FromResult(VectorMath.NormalizeCopy(output));
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private static DenseTensor<float> PreprocessImage(string imagePath)
    {
        using var image = Image.Load<Rgb24>(imagePath);
        ResizeAndCenterCrop(image);

        var tensor = new DenseTensor<float>([1, 3, ImageSize, ImageSize]);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < ImageSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < ImageSize; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = (pixel.R / 255f - Mean[0]) / Std[0];
                    tensor[0, 1, y, x] = (pixel.G / 255f - Mean[1]) / Std[1];
                    tensor[0, 2, y, x] = (pixel.B / 255f - Mean[2]) / Std[2];
                }
            }
        });

        return tensor;
    }

    private static void ResizeAndCenterCrop(Image<Rgb24> image)
    {
        var scale = (double)ImageSize / Math.Min(image.Width, image.Height);
        var resizedWidth = Math.Max(ImageSize, (int)Math.Round(image.Width * scale));
        var resizedHeight = Math.Max(ImageSize, (int)Math.Round(image.Height * scale));

        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(resizedWidth, resizedHeight),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic
        }));

        var left = Math.Max(0, (image.Width - ImageSize) / 2);
        var top = Math.Max(0, (image.Height - ImageSize) / 2);
        image.Mutate(context => context.Crop(new Rectangle(left, top, ImageSize, ImageSize)));
    }
}

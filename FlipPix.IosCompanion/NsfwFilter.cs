using FlipPix.Remote.Engine;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using Size = SixLabors.ImageSharp.Size;

namespace FlipPix.IosCompanion;

/// <summary>
/// The companion's content filter: Falconsai's ViT NSFW classifier (Apache-2.0, ONNX uint8 export: the int8 one needs
/// ConvInteger(int8), which ONNX Runtime's CPU provider doesn't implement),
/// run on the CPU so it never competes with ComfyUI for VRAM. Krea 2's license (§4.2) names this
/// classifier as an example of the filtering it requires; MiniMax H3's (§V.5) requires safeguards too.
/// Anything the classifier can't read is blocked rather than let through.
/// </summary>
public sealed class NsfwFilter : IContentFilter, IDisposable
{
    /// <summary>Probability of the "nsfw" class at or above which a picture is blocked.</summary>
    public const float DefaultThreshold = 0.5f;

    private const int Side = 224;
    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly float _threshold;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NsfwFilter(string modelPath, float threshold = DefaultThreshold)
    {
        var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
        _session = new InferenceSession(modelPath, options);
        _input = _session.InputMetadata.Keys.First();
        _threshold = threshold;
    }

    public async Task<string?> CheckImageAsync(byte[] image, CancellationToken ct)
    {
        float nsfw;
        try
        {
            nsfw = await Task.Run(() => NsfwProbability(image), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return "This couldn't be checked by the content filter, so it isn't shown.";
        }
        return nsfw >= _threshold ? "Blocked by the content filter." : null;
    }

    /// <summary>
    /// The model's "nsfw" probability. Preprocessing matches its ViTImageProcessor: RGB, resized to
    /// 224×224 bilinear (no crop), scaled to 0..1, normalised with mean 0.5 and std 0.5, NCHW.
    /// </summary>
    public float NsfwProbability(byte[] image)
    {
        using var img = Image.Load<Rgb24>(image);
        img.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(Side, Side),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Triangle,
        }));

        var tensor = new DenseTensor<float>(new[] { 1, 3, Side, Side });
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    tensor[0, 0, y, x] = row[x].R / 127.5f - 1f;
                    tensor[0, 1, y, x] = row[x].G / 127.5f - 1f;
                    tensor[0, 2, y, x] = row[x].B / 127.5f - 1f;
                }
            }
        });

        float[] logits;
        _gate.Wait();
        try
        {
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, tensor) });
            logits = results.First().AsEnumerable<float>().ToArray();
        }
        finally
        {
            _gate.Release();
        }
        // id2label: 0 = normal, 1 = nsfw.
        var max = Math.Max(logits[0], logits[1]);
        var normal = MathF.Exp(logits[0] - max);
        var nsfw = MathF.Exp(logits[1] - max);
        return nsfw / (normal + nsfw);
    }

    public void Dispose()
    {
        _session.Dispose();
        _gate.Dispose();
    }
}

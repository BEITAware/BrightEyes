using PhotoSauce.MagicScaler;
using PhotoSauce.NativeCodecs.Giflib;
using PhotoSauce.NativeCodecs.Libheif;
using PhotoSauce.NativeCodecs.Libjpeg;
using PhotoSauce.NativeCodecs.Libpng;
using PhotoSauce.NativeCodecs.Libwebp;

namespace BrightEyes.Processing.Execution;

internal static class PhotoSauceCodecBootstrap
{
    private static readonly Lazy<bool> Initialization = new(Configure);

    public static void EnsureConfigured() => _ = Initialization.Value;

    public static bool IsFormatSupported(string format) => format is
        "JPEG" or "PNG" or "GIF" or "WEBP" or "AVIF" or "HEIF";

    public static IDecoderOptions? CreateDecoderOptions(string format, int requestedFrame)
    {
        int frame = Math.Max(requestedFrame, 0);
        Range range = frame..(frame + 1);
        return format switch
        {
            "GIF" => new GifDecoderOptions(range, true),
            "PNG" => new PngDecoderOptions(range),
            "WEBP" => new WebpDecoderOptions(range, false, true),
            _ => null
        };
    }

    private static bool Configure()
    {
        CodecManager.Configure(codecs =>
        {
            codecs.Clear();
            codecs.UseGiflib();
            codecs.UseLibheif();
            codecs.UseLibjpeg();
            codecs.UseLibpng();
            codecs.UseLibwebp();
        });
        return true;
    }
}

using BrightEyes.Processing.BuiltIn;
using BrightEyes.Processing.Contracts;
using BrightEyes.Renderer;
using PhotoSauce.MagicScaler;
using PhotoSauce.MagicScaler.Experimental;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BrightEyes.Processing.Execution;

public sealed class ImageDecodeService
{
    public Task<DecodedImageFrame> DecodeAsync(
        RawImage source,
        DecoderProcess decoder,
        CancellationToken cancellationToken) => Task.Run(
        () => Decode(source, decoder, cancellationToken),
        cancellationToken);

    private static DecodedImageFrame Decode(
        RawImage source,
        DecoderProcess decoder,
        CancellationToken cancellationToken)
    {
        DecoderBackend requested = decoder.Backend.Value;
        var attempts = new List<DecoderBackend> { requested };
        if (decoder.AllowFallback.Value)
        {
            foreach (DecoderBackend backend in GetFallbackOrder(source.DetectedMetadata))
                if (!attempts.Contains(backend)) attempts.Add(backend);
        }

        var failures = new List<string>();
        foreach (DecoderBackend backend in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DecodedImageFrame frame = backend switch
                {
                    DecoderBackend.StarwardUltraHdr => DecodeUltraHdr(source, cancellationToken),
                    _ => DecodePhotoSauce(source, decoder, cancellationToken)
                };
                bool fallback = backend != requested;
                string detail = fallback
                    ? $"首选后端 {requested} 失败，实际使用 {backend}。{string.Join(" ", failures)}"
                    : frame.Diagnostic;
                return frame with { ActualBackend = backend, UsedFallback = fallback, Diagnostic = detail };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{backend}: {ex.GetBaseException().Message}");
            }
        }

        throw new NotSupportedException("所有获准解码后端均失败：" + string.Join(" | ", failures));
    }

    private static IEnumerable<DecoderBackend> GetFallbackOrder(DetectedImageMetadata metadata)
    {
        if (metadata.HasUltraHdrGainMap) yield return DecoderBackend.StarwardUltraHdr;
        yield return DecoderBackend.PhotoSauce;
    }

    private static DecodedImageFrame DecodePhotoSauce(
        RawImage source,
        DecoderProcess decoder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PhotoSauceCodecBootstrap.EnsureConfigured();
        bool preserveSourceEncoding = ShouldPreserveSourceEncoding(source, decoder);
        using var stream = new MemoryStream(source.CompressedData.ToArray(), writable: false);
        var settings = new ProcessImageSettings
        {
            ColorProfileMode = preserveSourceEncoding
                ? ColorProfileMode.Preserve
                : ColorProfileMode.ConvertToSrgb,
            BlendingMode = preserveSourceEncoding ? GammaMode.Companded : GammaMode.Linear,
            OrientationMode = decoder.ApplyExifOrientation.Value ? OrientationMode.Normalize : OrientationMode.Ignore,
            DecoderOptions = PhotoSauceCodecBootstrap.CreateDecoderOptions(
                source.DetectedMetadata.ContainerFormat,
                decoder.Frame.Value)
        };
        using var pipeline = MagicImageProcessor.BuildPipeline(stream, settings);
        IPixelSource pixels = pipeline.PixelSource;
        using IDisposable? formatConversion = pixels.Format == PhotoSauce.MagicScaler.PixelFormats.Bgra32bpp
            ? null
            : (IDisposable)TransformFactory.CreateConversionTransform(
                pixels,
                PhotoSauce.MagicScaler.PixelFormats.Bgra32bpp,
                null,
                null);
        if (formatConversion is IPixelSource convertedPixels)
        {
            pixels = convertedPixels;
        }

        if (pixels.Format != PhotoSauce.MagicScaler.PixelFormats.Bgra32bpp)
        {
            throw new InvalidDataException($"PhotoSauce returned unexpected pixel format {pixels.Format}.");
        }

        int stride = checked(pixels.Width * 4);
        byte[] rgba = new byte[checked(stride * pixels.Height)];
        pixels.CopyPixels(new Rectangle(0, 0, pixels.Width, pixels.Height), stride, rgba);
        ConvertBgraToRgbaInPlace(rgba, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(
            pixels.Width,
            pixels.Height,
            DecodedPixelStorage.Rgba8,
            rgba,
            null,
            decoder.EffectiveInputColorMetadata,
            DecoderBackend.PhotoSauce,
            preserveSourceEncoding
                ? "PhotoSauce 原生 codec CPU 压缩解码并保留源编码；GPU 按有效输入解释执行 transfer、矩阵、缩放和映射。"
                : "PhotoSauce 原生 codec CPU 压缩解码与 ICC->sRGB；GPU 按对应的有效输入解释执行后续处理。",
            false);
    }

    private static bool ShouldPreserveSourceEncoding(RawImage source, DecoderProcess decoder)
    {
        DetectedImageMetadata detected = source.DetectedMetadata;
        if (detected.HasCicp) return true;

        ColorSourceMetadata effective = decoder.EffectiveInputColorMetadata;
        AlphaRepresentation detectedAlpha = detected.HasAlpha
            ? AlphaRepresentation.Straight
            : AlphaRepresentation.Opaque;
        return effective.Transfer != detected.Transfer ||
               effective.Primaries != detected.Primaries ||
               effective.Alpha != detectedAlpha ||
               effective.ReferenceWhiteNits != detected.ReferenceWhiteNits ||
               effective.NominalPeakNits != detected.NominalPeakNits;
    }

    private static void ConvertBgraToRgbaInPlace(byte[] pixels, CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 0xfffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);
        }
    }

    private static DecodedImageFrame DecodeUltraHdr(RawImage source, CancellationToken cancellationToken)
    {
        if (!source.DetectedMetadata.HasUltraHdrGainMap)
            throw new InvalidDataException("文件未检测到 Ultra HDR gain map。");
        Assembly assembly = LoadStarwardCodecAssembly() ?? throw new FileNotFoundException("Starward.Codec unavailable.");
        Type decoderType = assembly.GetType("Starward.Codec.UltraHdr.UhdrDecoder", true)!;
        Type compressedType = assembly.GetType("Starward.Codec.UltraHdr.UhdrCompressedImage", true)!;
        Type pixelFormatType = assembly.GetType("Starward.Codec.UltraHdr.UhdrPixelFormat", true)!;
        Type transferType = assembly.GetType("Starward.Codec.UltraHdr.UhdrColorTransfer", true)!;
        Type gamutType = assembly.GetType("Starward.Codec.UltraHdr.UhdrColorGamut", true)!;
        Type rangeType = assembly.GetType("Starward.Codec.UltraHdr.UhdrColorRange", true)!;
        byte[] data = source.CompressedData.ToArray();
        object compressed = Activator.CreateInstance(compressedType)!;
        GCHandle pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            SetField(compressedType, compressed, "Data", pinned.AddrOfPinnedObject());
            SetField(compressedType, compressed, "DataSize", (ulong)data.LongLength);
            SetField(compressedType, compressed, "Capacity", (ulong)data.LongLength);
            SetField(compressedType, compressed, "ColorGamut", EnumValue(gamutType, "BT2100"));
            SetField(compressedType, compressed, "ColorTransfer", EnumValue(transferType, "PQ"));
            SetField(compressedType, compressed, "ColorRange", EnumValue(rangeType, "FullRange"));
            object nativeDecoder = Activator.CreateInstance(decoderType)!;
            decoderType.GetMethod("SetImage", new[] { compressedType })!.Invoke(nativeDecoder, new[] { compressed });
            decoderType.GetMethod("SetOutImagePixelFormat", new[] { pixelFormatType })!.Invoke(nativeDecoder, new[] { EnumValue(pixelFormatType, "_32bppRGBA1010102")! });
            decoderType.GetMethod("SetOutColorTransfer", new[] { transferType })!.Invoke(nativeDecoder, new[] { EnumValue(transferType, "PQ")! });
            decoderType.GetMethod("Decode", Type.EmptyTypes)!.Invoke(nativeDecoder, null);
            cancellationToken.ThrowIfCancellationRequested();
            object raw = decoderType.GetMethod("GetDecodedImage", Type.EmptyTypes)!.Invoke(nativeDecoder, null)!;
            Type rawType = raw.GetType();
            int width = Convert.ToInt32(GetFieldValue(rawType, raw, "Width"));
            int height = Convert.ToInt32(GetFieldValue(rawType, raw, "Height"));
            object planes = GetFieldValue(rawType, raw, "Plane")!;
            object strides = GetFieldValue(rawType, raw, "Stride")!;
            IntPtr plane = (IntPtr)GetFixedArray3Element(planes, 0);
            int rowBytes = checked(Convert.ToInt32(GetFixedArray3Element(strides, 0)) * 4);
            var output = new float[checked(width * height * 4)];
            var row = new byte[checked(width * 4)];
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(IntPtr.Add(plane, y * rowBytes), row, 0, row.Length);
                int destination = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int offset = x * 4;
                    uint packed = (uint)(row[offset] | row[offset + 1] << 8 | row[offset + 2] << 16 | row[offset + 3] << 24);
                    output[destination++] = (packed & 0x3ff) / 1023.0f;
                    output[destination++] = ((packed >> 10) & 0x3ff) / 1023.0f;
                    output[destination++] = ((packed >> 20) & 0x3ff) / 1023.0f;
                    output[destination++] = ((packed >> 30) & 0x3) / 3.0f;
                }
            }
            return new(
                width,
                height,
                DecodedPixelStorage.Rgba32Float,
                null,
                output,
                ColorSourceMetadata.PqRec2020Opaque,
                DecoderBackend.StarwardUltraHdr,
                "Starward/libultrahdr CPU 解码为 PQ 10-bit；当前后端输出为固定 HDR rendition，gain map 元数据仍保留在不可变来源元数据中。",
                false);
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    private static Assembly? LoadStarwardCodecAssembly()
    {
        try { return Assembly.Load("Starward.Codec"); }
        catch
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Starward.Codec.dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }

    private static void SetField(Type type, object instance, string name, object? value) =>
        (type.GetField(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingFieldException(type.FullName, name)).SetValue(instance, value);

    private static object? GetFieldValue(Type type, object instance, string name) =>
        (type.GetField(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingFieldException(type.FullName, name)).GetValue(instance);

    private static object? EnumValue(Type type, string name) =>
        (type.GetField(name, BindingFlags.Public | BindingFlags.Static) ?? throw new ArgumentException($"Enum {type.Name} has no value {name}")).GetValue(null);

    private static object GetFixedArray3Element(object value, int index)
    {
        Type type = value.GetType();
        PropertyInfo? item = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, null, new[] { typeof(int) }, null);
        if (item is not null) return item.GetValue(value, new object[] { index })!;
        string[][] candidates = { new[] { "Value0", "Value1", "Value2" }, new[] { "Item0", "Item1", "Item2" }, new[] { "X", "Y", "Z" }, new[] { "_0", "_1", "_2" } };
        foreach (string[] names in candidates)
        {
            FieldInfo? field = type.GetField(names[index], BindingFlags.Public | BindingFlags.Instance);
            if (field is not null) return field.GetValue(value)!;
        }
        Type elementType = type.IsGenericType ? type.GetGenericArguments()[0] : throw new MissingFieldException(type.FullName, "array element");
        int size = elementType == typeof(IntPtr) ? IntPtr.Size : Marshal.SizeOf(elementType);
        GCHandle pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            IntPtr pointer = pinned.AddrOfPinnedObject() + index * size;
            if (elementType == typeof(IntPtr)) return Marshal.ReadIntPtr(pointer);
            if (elementType == typeof(uint)) return unchecked((uint)Marshal.ReadInt32(pointer));
            if (elementType == typeof(int)) return Marshal.ReadInt32(pointer);
            return Marshal.PtrToStructure(pointer, elementType)!;
        }
        finally { pinned.Free(); }
    }
}

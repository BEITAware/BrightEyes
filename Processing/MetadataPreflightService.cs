using BrightEyes.Processing.Contracts;
using BrightEyes.Processing.Execution;
using BrightEyes.Renderer;
using PhotoSauce.MagicScaler;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BrightEyes.Processing;

public sealed class MetadataPreflightService
{
    public async Task<RawImage> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        DetectedImageMetadata metadata = await Task.Run(
            () => InspectContainer(filePath, bytes),
            cancellationToken).ConfigureAwait(false);
        string identity = Convert.ToHexString(SHA256.HashData(bytes));
        return new RawImage(filePath, bytes, metadata, identity);
    }

    private static DetectedImageMetadata InspectContainer(string filePath, byte[] bytes)
    {
        string format = DetectFormat(bytes, Path.GetExtension(filePath));
        int width = 0;
        int height = 0;
        int frames = 1;
        int orientation = 1;
        int bitDepth = GuessBitDepth(format, bytes);
        bool hasAlpha = GuessAlpha(format, bytes);
        bool hasIcc = ContainsAscii(bytes, "ICC_PROFILE") || ContainsAscii(bytes, "iCCP") || ContainsAscii(bytes, "acsp");
        var facts = new List<MetadataFact>();
        var conflicts = new List<string>();

        TryReadPhotoSauceMetadata(bytes, ref width, ref height, ref frames, ref orientation, ref hasAlpha, facts);
        TryReadNativeDimensions(format, bytes, ref width, ref height, ref bitDepth, ref hasAlpha);

        bool hasCicp = ColorMetadataDetector.TryDetect(bytes, out ColorSourceMetadata color);
        bool hasGainMap = ContainsAscii(bytes, "hdrgm") ||
                          ContainsAscii(bytes, "gainmap") ||
                          ContainsAscii(bytes, "hdrgainmap") ||
                          ContainsAscii(bytes, "ultrahdr");

        ColorPrimaries primaries;
        ColorTransferFunction transfer;
        PhotometricModel photometric;
        float referenceWhite;
        float peak;
        string colorSource;
        MetadataConfidence colorConfidence;
        bool colorAssumption;

        if (hasCicp)
        {
            primaries = color.Primaries;
            transfer = color.Transfer;
            referenceWhite = transfer == ColorTransferFunction.Srgb ? 100.0f : 203.0f;
            peak = color.NominalPeakNits;
            colorSource = "CICP/nclx";
            colorConfidence = MetadataConfidence.Authoritative;
            colorAssumption = false;
        }
        else
        {
            primaries = ColorPrimaries.Rec709;
            transfer = ColorTransferFunction.Srgb;
            referenceWhite = 100.0f;
            peak = 100.0f;
            colorSource = hasIcc ? "ICC/PhotoSauce" : $"{format} format default";
            colorConfidence = hasIcc ? MetadataConfidence.High : MetadataConfidence.Assumed;
            colorAssumption = !hasIcc;
        }

        if (hasGainMap)
        {
            photometric = PhotometricModel.DisplayLinearAbsolute;
            peak = Math.Max(peak, 1000.0f);
        }
        else
        {
            photometric = transfer switch
            {
                ColorTransferFunction.Pq => PhotometricModel.DisplayLinearAbsolute,
                ColorTransferFunction.Hlg => PhotometricModel.SceneLinearRelative,
                _ => PhotometricModel.DisplayLinearRelative
            };
        }

        facts.Add(new("container", "格式", format, "Container signature", MetadataConfidence.Authoritative));
        facts.Add(new("dimensions", "尺寸", width > 0 && height > 0 ? $"{width} x {height}" : "未知", "PhotoSauce/container", width > 0 ? MetadataConfidence.High : MetadataConfidence.Unknown, width <= 0));
        facts.Add(new("frames", "帧数", frames.ToString(), "PhotoSauce/container", MetadataConfidence.Medium));
        facts.Add(new("orientation", "EXIF 方向", orientation.ToString(), orientation == 1 ? "EXIF default" : "EXIF", orientation == 1 ? MetadataConfidence.Assumed : MetadataConfidence.High, orientation == 1));
        facts.Add(new("icc", "ICC", hasIcc ? "存在" : "未声明", hasIcc ? "Container" : "Scan", hasIcc ? MetadataConfidence.High : MetadataConfidence.Medium, !hasIcc));
        facts.Add(new("cicp", "CICP/nclx", hasCicp ? $"{primaries} / {transfer}" : "未声明", hasCicp ? "Container" : "Scan", hasCicp ? MetadataConfidence.Authoritative : MetadataConfidence.Medium, !hasCicp));
        facts.Add(new("bitDepth", "位深", $"{bitDepth} bit", "Container scan", MetadataConfidence.Medium));
        facts.Add(new("alpha", "Alpha", hasAlpha ? "存在" : "不透明", "PhotoSauce/container", MetadataConfidence.Medium));
        facts.Add(new("primaries", "原色", primaries.ToString(), colorSource, colorConfidence, colorAssumption));
        facts.Add(new("transfer", "传递函数", transfer.ToString(), colorSource, colorConfidence, colorAssumption));
        facts.Add(new("photometric", "光度模型", photometric.ToString(), colorSource, colorConfidence, colorAssumption));
        facts.Add(new("referenceWhite", "参考白", $"{referenceWhite:0.#} nit", colorSource, colorConfidence, colorAssumption));
        facts.Add(new("nominalPeak", "标称峰值", $"{peak:0.#} nit", colorSource, colorConfidence, colorAssumption));
        facts.Add(new("maxCll", "MaxCLL", "未声明", "Container scan", MetadataConfidence.Unknown, true));
        facts.Add(new("maxFall", "MaxFALL", "未声明", "Container scan", MetadataConfidence.Unknown, true));
        facts.Add(new("gainMap", "Ultra HDR gain map", hasGainMap ? "存在" : "未声明", hasGainMap ? "XMP/container" : "Scan", hasGainMap ? MetadataConfidence.High : MetadataConfidence.Medium));

        if (hasIcc && hasCicp)
        {
            const string conflict = "文件同时包含 ICC 与 CICP/nclx；输入解释采用 CICP，PhotoSauce 保留源编码并由 GPU 完成色彩变换。";
            conflicts.Add(conflict);
            int index = facts.FindIndex(fact => fact.StableId == "cicp");
            facts[index] = facts[index] with { Conflict = conflict };
        }

        return new DetectedImageMetadata(
            format,
            width,
            height,
            frames,
            orientation,
            bitDepth,
            hasAlpha,
            hasIcc,
            hasCicp,
            hasGainMap,
            primaries,
            transfer,
            photometric,
            ColorRange.Full,
            referenceWhite,
            peak,
            null,
            null,
            facts.AsReadOnly(),
            conflicts.AsReadOnly(),
            ProbeBackends(format, hasGainMap));
    }

    private static void TryReadPhotoSauceMetadata(
        byte[] bytes,
        ref int width,
        ref int height,
        ref int frames,
        ref int orientation,
        ref bool hasAlpha,
        ICollection<MetadataFact> facts)
    {
        try
        {
            PhotoSauceCodecBootstrap.EnsureConfigured();
            ImageFileInfo info = ImageFileInfo.Load(bytes);
            frames = Math.Max(info.Frames.Count, 1);
            ImageFileInfo.FrameInfo frame = info.Frames[0];
            width = frame.Width;
            height = frame.Height;
            orientation = (int)frame.ExifOrientation;
            hasAlpha |= frame.HasAlpha;
        }
        catch (Exception ex)
        {
            facts.Add(new("photosauce", "PhotoSauce 元数据", "不可用", "PhotoSauce", MetadataConfidence.Low, false, ex.Message));
        }
    }

    private static void TryReadNativeDimensions(
        string format,
        ReadOnlySpan<byte> data,
        ref int width,
        ref int height,
        ref int bitDepth,
        ref bool hasAlpha)
    {
        if (format == "PNG" && data.Length >= 29)
        {
            width = checked((int)ReadBigEndian32(data[16..]));
            height = checked((int)ReadBigEndian32(data[20..]));
            bitDepth = data[24];
            hasAlpha |= data[25] is 4 or 6;
        }
        else if (format == "GIF" && data.Length >= 10)
        {
            width = data[6] | data[7] << 8;
            height = data[8] | data[9] << 8;
            bitDepth = (data[10] & 0x07) + 1;
        }
        else if (format == "BMP" && data.Length >= 30)
        {
            width = Math.Abs(BitConverter.ToInt32(data[18..22]));
            height = Math.Abs(BitConverter.ToInt32(data[22..26]));
            int bitsPerPixel = BitConverter.ToUInt16(data[28..30]);
            bitDepth = bitsPerPixel >= 48 ? 16 : 8;
            hasAlpha |= bitsPerPixel == 32;
        }
        else if (format is "AVIF" or "HEIF")
        {
            for (int i = 4; i + 16 <= data.Length; i++)
            {
                if (!data.Slice(i, 4).SequenceEqual("ispe"u8)) continue;
                width = checked((int)ReadBigEndian32(data[(i + 8)..]));
                height = checked((int)ReadBigEndian32(data[(i + 12)..]));
                break;
            }
        }
    }

    private static IReadOnlyList<BackendCapability> ProbeBackends(string format, bool hasGainMap)
    {
        bool starward = false;
        try
        {
            starward = Assembly.Load("Starward.Codec") is not null;
        }
        catch
        {
            starward = File.Exists(Path.Combine(AppContext.BaseDirectory, "Starward.Codec.dll"));
        }

        bool photoSauce = PhotoSauceCodecBootstrap.IsFormatSupported(format);
        return new[]
        {
            new BackendCapability(
                "PhotoSauce",
                photoSauce,
                photoSauce ? "PhotoSauce native codecs" : $"{format} is unsupported without WIC",
                false,
                true,
                16),
            new BackendCapability(
                "StarwardUltraHdr",
                starward,
                starward ? "Starward.Codec/libultrahdr" : "Starward.Codec unavailable",
                true,
                true,
                10)
        };
    }

    private static string DetectFormat(ReadOnlySpan<byte> data, string extension)
    {
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff) return "JPEG";
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "PNG";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return "GIF";
        if (data.Length >= 2 && data[..2].SequenceEqual("BM"u8)) return "BMP";
        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            ReadOnlySpan<byte> brand = data.Slice(8, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return "AVIF";
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8)) return "HEIF";
        }
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "WEBP";
        if (data.Length >= 4 && (data[..4].SequenceEqual(new byte[] { 0x49, 0x49, 0x2a, 0x00 }) || data[..4].SequenceEqual(new byte[] { 0x4d, 0x4d, 0x00, 0x2a }))) return "TIFF";
        return string.IsNullOrWhiteSpace(extension) ? "UNKNOWN" : extension.TrimStart('.').ToUpperInvariant();
    }

    private static int GuessBitDepth(string format, ReadOnlySpan<byte> data) => format switch
    {
        "PNG" when data.Length > 24 => data[24],
        "AVIF" or "HEIF" => 10,
        _ => 8
    };

    private static bool GuessAlpha(string format, ReadOnlySpan<byte> data) => format switch
    {
        "PNG" when data.Length > 25 => data[25] is 4 or 6,
        "WEBP" => ContainsAscii(data, "ALPH"),
        _ => false
    };

    private static bool ContainsAscii(ReadOnlySpan<byte> data, string value)
    {
        byte[] needle = Encoding.ASCII.GetBytes(value);
        for (int i = 0; i <= data.Length - needle.Length; i++)
        {
            int j = 0;
            for (; j < needle.Length; j++)
            {
                byte actual = data[i + j];
                byte expected = needle[j];
                if (actual >= (byte)'a' && actual <= (byte)'z') actual -= 32;
                if (expected >= (byte)'a' && expected <= (byte)'z') expected -= 32;
                if (actual != expected) break;
            }
            if (j == needle.Length) return true;
        }
        return false;
    }

    private static uint ReadBigEndian32(ReadOnlySpan<byte> data) =>
        ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
}

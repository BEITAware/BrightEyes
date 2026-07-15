using System;

namespace BrightEyes.Renderer;

/// <summary>
/// Reads the compact CICP/nclx signalling used by PNG, AVIF and HEIF. ICC
/// profiles are still handed to WIC/PhotoSauce for normal SDR conversion; the
/// detector only claims a transfer when the container explicitly identifies a
/// PQ or HLG stream.
/// </summary>
public static class ColorMetadataDetector
{
    public static bool TryDetect(ReadOnlySpan<byte> data, out ColorSourceMetadata metadata)
    {
        if (TryFindPngCicp(data, out byte primaries, out byte transfer))
        {
            if (TryMapCicp(primaries, transfer, out metadata))
                return true;
        }

        if (TryFindNclx(data, out primaries, out transfer))
        {
            if (TryMapCicp(primaries, transfer, out metadata))
                return true;
        }

        metadata = default;
        return false;
    }

    private static bool TryFindPngCicp(ReadOnlySpan<byte> data, out byte primaries, out byte transfer)
    {
        primaries = 0;
        transfer = 0;
        ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (data.Length < signature.Length || !data[..signature.Length].SequenceEqual(signature))
            return false;

        int offset = 8;
        while (offset + 12 <= data.Length)
        {
            uint length = ReadBigEndian32(data[offset..]);
            if (length > int.MaxValue || offset + 12 + (int)length > data.Length)
                return false;
            ReadOnlySpan<byte> type = data.Slice(offset + 4, 4);
            if (type.SequenceEqual("cICP"u8) && length >= 4)
            {
                primaries = data[offset + 8];
                transfer = data[offset + 9];
                return true;
            }
            offset += 12 + (int)length;
        }
        return false;
    }

    private static bool TryFindNclx(ReadOnlySpan<byte> data, out byte primaries, out byte transfer)
    {
        primaries = 0;
        transfer = 0;
        for (int i = 4; i + 11 < data.Length; i++)
        {
            if (data[i] != (byte)'n' || data[i + 1] != (byte)'c' || data[i + 2] != (byte)'l' || data[i + 3] != (byte)'x')
                continue;
            int payload = i + 4;
            ushort colourPrimaries = ReadBigEndian16(data[payload..]);
            ushort transferCharacteristics = ReadBigEndian16(data[(payload + 2)..]);
            if (colourPrimaries > byte.MaxValue || transferCharacteristics > byte.MaxValue)
                continue;
            primaries = (byte)colourPrimaries;
            transfer = (byte)transferCharacteristics;
            return true;
        }
        return false;
    }

    private static bool TryMapCicp(byte primaries, byte transfer, out ColorSourceMetadata metadata)
    {
        ColorPrimaries gamut = primaries switch
        {
            1 => ColorPrimaries.Rec709,
            9 => ColorPrimaries.Rec2020,
            12 => ColorPrimaries.DisplayP3,
            _ when transfer is 16 or 18 => ColorPrimaries.Rec2020,
            _ => ColorPrimaries.Rec709
        };
        switch (transfer)
        {
            case 1:
            case 13:
                metadata = new ColorSourceMetadata(ColorTransferFunction.Srgb, gamut, AlphaRepresentation.Straight, 100.0f, 100.0f);
                return true;
            case 16:
                metadata = new ColorSourceMetadata(ColorTransferFunction.Pq, gamut, AlphaRepresentation.Straight, 100.0f, 10000.0f);
                return true;
            case 18:
                metadata = new ColorSourceMetadata(ColorTransferFunction.Hlg, gamut, AlphaRepresentation.Straight, 100.0f, 1000.0f);
                return true;
            case 8:
                metadata = new ColorSourceMetadata(ColorTransferFunction.Linear, gamut, AlphaRepresentation.Straight, 100.0f, 1000.0f);
                return true;
            default:
                metadata = default;
                return false;
        }
    }

    private static ushort ReadBigEndian16(ReadOnlySpan<byte> value) =>
        (ushort)((value[0] << 8) | value[1]);

    private static uint ReadBigEndian32(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];
}

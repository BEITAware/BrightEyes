using BrightEyes.Processing.Contracts;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrightEyes.Converters;

public sealed class AnalysisDataToBitmapConverter : IValueConverter
{
    private const int RegularWidth = 208;
    private const int RegularHeight = 132;
    private const int StrictWidth = 112;
    private const int StrictHeight = 272;

    private readonly record struct RasterRect(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width - 1;
        public int Bottom => Top + Height - 1;
    }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AnalysisData analysis || analysis.Values.IsEmpty) return null;
        int width = analysis.Kind == AnalysisKind.StrictHistogram ? StrictWidth : RegularWidth;
        int height = analysis.Kind == AnalysisKind.StrictHistogram ? StrictHeight : RegularHeight;
        int[] pixels = Enumerable.Repeat(Argb(255, 20, 28, 34), width * height).ToArray();
        switch (analysis.Kind)
        {
            case AnalysisKind.Waveform:
                DrawWaveform(pixels, width, height, analysis);
                break;
            case AnalysisKind.Vectorscope:
                DrawVectorscope(pixels, width, height, analysis);
                break;
            case AnalysisKind.StrictHistogram:
                DrawStrictHistogram(pixels, width, height, analysis);
                break;
            default:
                DrawHistogram(pixels, width, height, analysis);
                break;
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * sizeof(int), 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawWaveform(int[] pixels, int width, int height, AnalysisData analysis)
    {
        var plot = new RasterRect(8, 7, width - 16, height - 14);
        DrawPlotChrome(pixels, width, plot, false);
        ReadOnlySpan<float> values = analysis.Values.Span;
        if (analysis.Presentation == AnalysisPresentation.RgbParade)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int segmentLeft = plot.Left + channel * plot.Width / 3;
                int segmentRight = plot.Left + (channel + 1) * plot.Width / 3 - 1;
                DrawWaveformPlane(pixels, width, values, channel, segmentLeft, segmentRight, plot.Top, plot.Bottom, ChannelColor(channel));
                if (channel > 0)
                    DrawVerticalLine(pixels, width, segmentLeft, plot.Top, plot.Bottom, Argb(255, 67, 82, 91));
            }
            return;
        }

        if (analysis.Presentation == AnalysisPresentation.RgbOverlay)
        {
            for (int channel = 0; channel < 3; channel++)
                DrawWaveformPlane(pixels, width, values, channel, plot.Left, plot.Right, plot.Top, plot.Bottom, ChannelColor(channel));
            return;
        }

        DrawWaveformPlane(pixels, width, values, 0, plot.Left, plot.Right, plot.Top, plot.Bottom, (198, 232, 238));
    }

    private static void DrawWaveformPlane(
        int[] pixels,
        int width,
        ReadOnlySpan<float> values,
        int plane,
        int left,
        int right,
        int top,
        int bottom,
        (byte R, byte G, byte B) color)
    {
        const int sourceWidth = 256;
        const int sourceHeight = 128;
        int planeOffset = plane * sourceWidth * sourceHeight;
        if (planeOffset >= values.Length) return;
        for (int sourceY = 0; sourceY < sourceHeight; sourceY++)
        {
            int y = bottom - sourceY * (bottom - top) / (sourceHeight - 1);
            for (int sourceX = 0; sourceX < sourceWidth; sourceX++)
            {
                int index = planeOffset + sourceY * sourceWidth + sourceX;
                if (index >= values.Length) return;
                float density = values[index];
                if (density <= 0.0f) continue;
                int x = left + sourceX * (right - left) / (sourceWidth - 1);
                BlendAdditive(pixels, width, x, y, color.R, color.G, color.B, MathF.Pow(density, 0.55f) * 0.9f);
            }
        }
    }

    private static void DrawHistogram(int[] pixels, int width, int height, AnalysisData analysis)
    {
        var plot = new RasterRect(8, 7, width - 16, height - 14);
        DrawPlotChrome(pixels, width, plot, false);
        ReadOnlySpan<float> values = analysis.Values.Span;
        (byte R, byte G, byte B) color = analysis.Channel switch
        {
            AnalysisChannel.Red => (225, 78, 70),
            AnalysisChannel.Green => (76, 203, 112),
            AnalysisChannel.Blue => (73, 139, 237),
            AnalysisChannel.Alpha => (210, 218, 224),
            _ => (86, 207, 177)
        };
        int count = Math.Min(256, values.Length);
        for (int bin = 0; bin < count; bin++)
        {
            float amplitude = ScaleAmplitude(values[bin], analysis.LogarithmicScale);
            int barTop = plot.Bottom - (int)MathF.Round(amplitude * (plot.Height - 1));
            int x0 = plot.Left + bin * plot.Width / count;
            int x1 = Math.Max(x0, plot.Left + (bin + 1) * plot.Width / count - 1);
            for (int x = x0; x <= x1; x++)
                for (int y = plot.Bottom; y >= barTop; y--)
                {
                    float vertical = (plot.Bottom - y) / (float)Math.Max(1, plot.Height - 1);
                    BlendAdditive(pixels, width, x, y, color.R, color.G, color.B, 0.38f + vertical * 0.42f);
                }
        }
    }

    private static void DrawStrictHistogram(int[] pixels, int width, int height, AnalysisData analysis)
    {
        var plot = new RasterRect(20, 8, width - 28, 256);
        FillRect(pixels, width, plot, Argb(255, 10, 16, 21));
        for (int bin = 0; bin < 256; bin++)
        {
            int y = plot.Bottom - bin;
            int rowBackground = bin % 16 == 0 ? Argb(255, 35, 49, 58) : Argb(255, 13, 21, 27);
            DrawHorizontalLine(pixels, width, plot.Left, plot.Right, y, rowBackground);
        }
        for (int step = 0; step <= 4; step++)
        {
            int x = plot.Left + step * (plot.Width - 1) / 4;
            DrawVerticalLine(pixels, width, x, plot.Top, plot.Bottom, Argb(255, 43, 58, 67));
        }

        ReadOnlySpan<float> values = analysis.Values.Span;
        int count = Math.Min(256, values.Length);
        for (int bin = 0; bin < count; bin++)
        {
            float amplitude = ScaleAmplitude(values[bin], analysis.LogarithmicScale);
            int length = Math.Clamp((int)MathF.Round(amplitude * (plot.Width - 1)), 0, plot.Width - 1);
            int y = plot.Bottom - bin;
            for (int x = plot.Left; x <= plot.Left + length; x++)
            {
                float progress = (x - plot.Left) / (float)Math.Max(1, plot.Width - 1);
                BlendAdditive(pixels, width, x, y, 78, 201, 168, 0.52f + progress * 0.38f);
            }
        }

        DrawBorder(pixels, width, plot, Argb(255, 111, 139, 153));
        DrawVerticalLine(pixels, width, 12, plot.Top, plot.Bottom, Argb(255, 82, 103, 113));
        for (int bin = 0; bin <= 256; bin += 16)
        {
            int y = plot.Bottom - Math.Min(bin, 255);
            DrawHorizontalLine(pixels, width, 12, 18, y, Argb(255, 151, 171, 180));
        }
    }

    private static void DrawVectorscope(int[] pixels, int width, int height, AnalysisData analysis)
    {
        int size = Math.Min(height - 12, width - 16);
        var plot = new RasterRect((width - size) / 2, 6, size, size);
        FillRect(pixels, width, plot, Argb(255, 9, 15, 20));
        int centerX = plot.Left + plot.Width / 2;
        int centerY = plot.Top + plot.Height / 2;
        int radius = plot.Width / 2 - 4;
        DrawCircle(pixels, width, centerX, centerY, radius, Argb(255, 80, 102, 112));
        DrawCircle(pixels, width, centerX, centerY, radius / 2, Argb(255, 38, 55, 63));
        DrawHorizontalLine(pixels, width, plot.Left + 3, plot.Right - 3, centerY, Argb(255, 48, 66, 75));
        DrawVerticalLine(pixels, width, centerX, plot.Top + 3, plot.Bottom - 3, Argb(255, 48, 66, 75));
        DrawDiagonal(pixels, width, centerX - radius, centerY - radius, centerX + radius, centerY + radius, Argb(255, 31, 46, 54));
        DrawDiagonal(pixels, width, centerX - radius, centerY + radius, centerX + radius, centerY - radius, Argb(255, 31, 46, 54));

        ReadOnlySpan<float> values = analysis.Values.Span;
        const int sourceSize = 128;
        int count = Math.Min(values.Length, sourceSize * sourceSize);
        for (int index = 0; index < count; index++)
        {
            float density = values[index];
            if (density <= 0.0f) continue;
            int sourceX = index % sourceSize;
            int sourceY = index / sourceSize;
            float dx = (sourceX - 63.5f) / 63.5f;
            float dy = (sourceY - 63.5f) / 63.5f;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance > 1.0f || distance <= 0.0001f) continue;
            float plottedDistance = analysis.LogarithmicScale
                ? MathF.Log(1.0f + 9.0f * distance) / MathF.Log(10.0f)
                : distance;
            float targetX = dx / distance * plottedDistance;
            float targetY = dy / distance * plottedDistance;
            int x = centerX + (int)MathF.Round(targetX * radius);
            int y = centerY - (int)MathF.Round(targetY * radius);
            (byte red, byte green, byte blue) = HueColor(dx, dy);
            BlendAdditive(pixels, width, x, y, red, green, blue, MathF.Pow(density, 0.5f));
        }

        DrawTarget(pixels, width, centerX + radius, centerY, 62, 110, 239);
        DrawTarget(pixels, width, centerX, centerY - radius, 239, 66, 64);
        DrawTarget(pixels, width, centerX - radius, centerY, 225, 211, 62);
        DrawTarget(pixels, width, centerX, centerY + radius, 56, 211, 213);
        DrawBorder(pixels, width, plot, Argb(255, 111, 139, 153));
    }

    private static void DrawPlotChrome(int[] pixels, int width, RasterRect plot, bool square)
    {
        FillRect(pixels, width, plot, Argb(255, 9, 15, 20));
        for (int step = 1; step < 4; step++)
        {
            int x = plot.Left + step * (plot.Width - 1) / 4;
            int y = plot.Top + step * (plot.Height - 1) / 4;
            DrawVerticalLine(pixels, width, x, plot.Top, plot.Bottom, Argb(255, 31, 47, 56));
            DrawHorizontalLine(pixels, width, plot.Left, plot.Right, y, Argb(255, 31, 47, 56));
        }
        DrawHorizontalLine(pixels, width, plot.Left, plot.Right, plot.Top + (plot.Height - 1) / 2, Argb(255, 48, 66, 75));
        DrawVerticalLine(pixels, width, plot.Left + (plot.Width - 1) / 2, plot.Top, plot.Bottom, Argb(255, 48, 66, 75));
        DrawBorder(pixels, width, plot, Argb(255, 111, 139, 153));
        DrawHorizontalLine(pixels, width, plot.Left + 1, plot.Right - 1, plot.Top + 1, Argb(255, 47, 65, 74));
    }

    private static float ScaleAmplitude(float value, bool logarithmic) => logarithmic
        ? MathF.Log(1.0f + 31.0f * Math.Clamp(value, 0.0f, 1.0f)) / MathF.Log(32.0f)
        : Math.Clamp(value, 0.0f, 1.0f);

    private static (byte R, byte G, byte B) ChannelColor(int channel) => channel switch
    {
        0 => (242, 68, 62),
        1 => (61, 225, 105),
        _ => (61, 126, 250)
    };

    private static (byte R, byte G, byte B) HueColor(float dx, float dy)
    {
        float red = Math.Clamp(0.5f + dy * 0.5f, 0.0f, 1.0f);
        float blue = Math.Clamp(0.5f + dx * 0.5f, 0.0f, 1.0f);
        float green = Math.Clamp(0.5f - dx * 0.35f - dy * 0.35f, 0.0f, 1.0f);
        float minimum = Math.Min(red, Math.Min(green, blue));
        red -= minimum;
        green -= minimum;
        blue -= minimum;
        float maximum = Math.Max(red, Math.Max(green, blue));
        if (maximum <= 0.0001f) return (210, 220, 224);
        return ((byte)(red / maximum * 255.0f), (byte)(green / maximum * 255.0f), (byte)(blue / maximum * 255.0f));
    }

    private static void DrawTarget(int[] pixels, int width, int x, int y, byte red, byte green, byte blue)
    {
        for (int offset = -2; offset <= 2; offset++)
        {
            SetPixel(pixels, width, x + offset, y, Argb(255, red, green, blue));
            SetPixel(pixels, width, x, y + offset, Argb(255, red, green, blue));
        }
    }

    private static void BlendAdditive(int[] pixels, int width, int x, int y, byte red, byte green, byte blue, float amount)
    {
        if (x < 0 || y < 0 || x >= width || y >= pixels.Length / width) return;
        amount = Math.Clamp(amount, 0.0f, 1.0f);
        int index = y * width + x;
        uint current = unchecked((uint)pixels[index]);
        int currentRed = (int)((current >> 16) & 0xff);
        int currentGreen = (int)((current >> 8) & 0xff);
        int currentBlue = (int)(current & 0xff);
        int resultRed = Math.Min(255, currentRed + (int)MathF.Round(red * amount));
        int resultGreen = Math.Min(255, currentGreen + (int)MathF.Round(green * amount));
        int resultBlue = Math.Min(255, currentBlue + (int)MathF.Round(blue * amount));
        pixels[index] = Argb(255, (byte)resultRed, (byte)resultGreen, (byte)resultBlue);
    }

    private static void FillRect(int[] pixels, int width, RasterRect rect, int color)
    {
        for (int y = rect.Top; y <= rect.Bottom; y++)
            for (int x = rect.Left; x <= rect.Right; x++)
                SetPixel(pixels, width, x, y, color);
    }

    private static void DrawBorder(int[] pixels, int width, RasterRect rect, int color)
    {
        DrawHorizontalLine(pixels, width, rect.Left, rect.Right, rect.Top, color);
        DrawHorizontalLine(pixels, width, rect.Left, rect.Right, rect.Bottom, color);
        DrawVerticalLine(pixels, width, rect.Left, rect.Top, rect.Bottom, color);
        DrawVerticalLine(pixels, width, rect.Right, rect.Top, rect.Bottom, color);
    }

    private static void DrawHorizontalLine(int[] pixels, int width, int left, int right, int y, int color)
    {
        for (int x = left; x <= right; x++) SetPixel(pixels, width, x, y, color);
    }

    private static void DrawVerticalLine(int[] pixels, int width, int x, int top, int bottom, int color)
    {
        for (int y = top; y <= bottom; y++) SetPixel(pixels, width, x, y, color);
    }

    private static void DrawDiagonal(int[] pixels, int width, int x0, int y0, int x1, int y1, int color)
    {
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        for (int step = 0; step <= steps; step++)
        {
            int x = x0 + (x1 - x0) * step / Math.Max(steps, 1);
            int y = y0 + (y1 - y0) * step / Math.Max(steps, 1);
            SetPixel(pixels, width, x, y, color);
        }
    }

    private static void DrawCircle(int[] pixels, int width, int centerX, int centerY, int radius, int color)
    {
        int x = radius;
        int y = 0;
        int error = 1 - x;
        while (x >= y)
        {
            SetCirclePoints(pixels, width, centerX, centerY, x, y, color);
            y++;
            if (error < 0)
                error += 2 * y + 1;
            else
            {
                x--;
                error += 2 * (y - x) + 1;
            }
        }
    }

    private static void SetCirclePoints(int[] pixels, int width, int cx, int cy, int x, int y, int color)
    {
        SetPixel(pixels, width, cx + x, cy + y, color);
        SetPixel(pixels, width, cx + y, cy + x, color);
        SetPixel(pixels, width, cx - y, cy + x, color);
        SetPixel(pixels, width, cx - x, cy + y, color);
        SetPixel(pixels, width, cx - x, cy - y, color);
        SetPixel(pixels, width, cx - y, cy - x, color);
        SetPixel(pixels, width, cx + y, cy - x, color);
        SetPixel(pixels, width, cx + x, cy - y, color);
    }

    private static void SetPixel(int[] pixels, int width, int x, int y, int color)
    {
        if (x < 0 || y < 0 || x >= width || y >= pixels.Length / width) return;
        pixels[y * width + x] = color;
    }

    private static int Argb(byte alpha, byte red, byte green, byte blue) =>
        unchecked((int)((uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | blue));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

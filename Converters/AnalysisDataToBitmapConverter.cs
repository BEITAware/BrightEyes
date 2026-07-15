using BrightEyes.Processing.Contracts;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrightEyes.Converters;

public sealed class AnalysisDataToBitmapConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AnalysisData analysis || analysis.Values.IsEmpty) return null;
        bool waveform = analysis.AnalysisType == "Waveform";
        bool vectorscope = analysis.AnalysisType == "Vectorscope";
        int width = waveform ? 256 : vectorscope ? 128 : 256;
        int height = waveform || vectorscope ? 128 : 96;
        var pixels = Enumerable.Repeat(unchecked((int)0xff111820), width * height).ToArray();
        ReadOnlySpan<float> values = analysis.Values.Span;

        if (!waveform && !vectorscope)
        {
            for (int x = 0; x < width && x < values.Length; x++)
            {
                int barHeight = Math.Clamp((int)Math.Round(values[x] * (height - 1)), 0, height - 1);
                for (int y = height - 1; y >= height - 1 - barHeight; y--)
                    pixels[y * width + x] = unchecked((int)0xff4ebc8b);
            }
        }
        else
        {
            int count = Math.Min(values.Length, width * height);
            for (int index = 0; index < count; index++)
            {
                int sourceY = index / width;
                int x = index % width;
                int y = height - 1 - sourceY;
                byte intensity = (byte)Math.Clamp((int)Math.Round(values[index] * 255.0f), 0, 255);
                byte red = vectorscope ? (byte)(intensity * 0.55f) : (byte)(intensity * 0.20f);
                byte green = vectorscope ? intensity : (byte)(intensity * 0.72f);
                byte blue = vectorscope ? (byte)(intensity * 0.75f) : intensity;
                pixels[y * width + x] = unchecked((int)(0xff000000u | (uint)(red << 16) | (uint)(green << 8) | blue));
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * sizeof(int), 0);
        bitmap.Freeze();
        return bitmap;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}

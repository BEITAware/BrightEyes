using BrightEyes.Renderer;
using Microsoft.Win32;
using PhotoSauce.MagicScaler;
using PhotoSauce.MagicScaler.Transforms;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using System.Text;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace BrightEyes.ViewModels;

public sealed class MainViewModel : IDisposable, INotifyPropertyChanged
{
    private RendererD3D11? _renderer;
    private bool _disposed;
    private RelayCommand? _openImageCommand;
    private RelayCommand? _inspectCodecCommand;
    private string _sdrReferenceWhiteText = "100";
    private string _sdrDisplayLuminanceText = "100";
    private DisplayColorMetadata _displayColorMetadata = DisplayColorMetadata.SdrFallback;

    public Action<IntPtr, int, int> InitializeRendererAction { get; }
    public Action<int, int> ResizeRendererAction { get; }
    public ICommand WindowClosingCommand { get; }
    public ICommand OpenImageCommand => _openImageCommand ??= new RelayCommand(_ => OpenImage());
    public ICommand InspectCodecCommand => _inspectCodecCommand ??= new RelayCommand(_ => InspectStarwardCodec());

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SdrReferenceWhiteText
    {
        get => _sdrReferenceWhiteText;
        set
        {
            if (_sdrReferenceWhiteText == value) return;
            _sdrReferenceWhiteText = value;
            OnPropertyChanged();
            UpdateSdrCalibration();
        }
    }

    public string SdrDisplayLuminanceText
    {
        get => _sdrDisplayLuminanceText;
        set
        {
            if (_sdrDisplayLuminanceText == value) return;
            _sdrDisplayLuminanceText = value;
            OnPropertyChanged();
            UpdateSdrCalibration();
        }
    }

    public bool IsSdrCalibrationEnabled => !_displayColorMetadata.IsHdr;

    public string DisplayColorSummary => _displayColorMetadata.IsHdr
        ? $"HDR / {_displayColorMetadata.ColorSpace}  {_displayColorMetadata.MaxLuminanceNits:0.#} nit peak, {_displayColorMetadata.MaxFullFrameLuminanceNits:0.#} nit full frame, SDR white {_displayColorMetadata.SdrWhiteLevelNits:0.#} nit{(_displayColorMetadata.HasSystemSdrWhiteLevel ? string.Empty : " (fallback)")}"
        : $"SDR / {_displayColorMetadata.ColorSpace}";

    static MainViewModel()
    {
        // MagicScaler requires an explicit codec registration. Microsoft WIC
        // codecs cover JPEG/PNG/TIFF and installed Microsoft image extensions.
        CodecManager.Configure(codecs => codecs.UseWicCodecs(WicCodecPolicy.Microsoft));
    }

    public MainViewModel()
    {
        WindowClosingCommand = new RelayCommand(_ => Dispose());
        InitializeRendererAction = (handle, width, height) =>
        {
            if (handle == IntPtr.Zero || width <= 0 || height <= 0) return;
            _renderer = new RendererD3D11(handle, width, height);
            _renderer.Initialize();
            // Keep the application's existing solid blue presentation background.
            _renderer.SetSdrBackgroundColor(249f / 255f, 250f / 255f, 254f / 255f, 1f);
            _renderer.SetHdrBackgroundColor(0.58f, 0.59f, 0.62f, 1f);
            _displayColorMetadata = _renderer.DisplayColorMetadata;
            OnPropertyChanged(nameof(IsSdrCalibrationEnabled));
            OnPropertyChanged(nameof(DisplayColorSummary));
            UpdateSdrCalibration();
            _renderer.Start();
        };
        ResizeRendererAction = (width, height) => _renderer?.Resize(width, height);
    }

    private async void OpenImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开图像",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.avif;*.heif;*.heic;*.webp;*.tif;*.tiff;*.bmp;*.gif|All files|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(dialog.FileName);
            await Task.Run(() =>
            {
                if (!TryDecodeUltraHdr(fileBytes) && !TryDecodeExplicitHdr(fileBytes))
                    DecodeWithPhotoSauce(fileBytes);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            System.Windows.MessageBox.Show(ex.Message, "Decode failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryDecodeUltraHdr(byte[] fileBytes)
    {
        try
        {
            if (!ContainsAscii(fileBytes, "hdrgm") &&
                !ContainsAscii(fileBytes, "gainmap") &&
                !ContainsAscii(fileBytes, "ultrahdr"))
            {
                return false;
            }
            var asm = LoadStarwardCodecAssembly();
            if (asm == null) return false;
            Type decoderType = asm.GetType("Starward.Codec.UltraHdr.UhdrDecoder", true)!;
            Type compressedType = asm.GetType("Starward.Codec.UltraHdr.UhdrCompressedImage", true)!;
            Type pixelFormatType = asm.GetType("Starward.Codec.UltraHdr.UhdrPixelFormat", true)!;
            Type transferType = asm.GetType("Starward.Codec.UltraHdr.UhdrColorTransfer", true)!;
            Type gamutType = asm.GetType("Starward.Codec.UltraHdr.UhdrColorGamut", true)!;
            Type rangeType = asm.GetType("Starward.Codec.UltraHdr.UhdrColorRange", true)!;

            object compressed = Activator.CreateInstance(compressedType)!;
            GCHandle pinned = GCHandle.Alloc(fileBytes, GCHandleType.Pinned);
            try
            {
                SetField(compressedType, compressed, "Data", pinned.AddrOfPinnedObject());
                SetField(compressedType, compressed, "DataSize", (ulong)fileBytes.LongLength);
                SetField(compressedType, compressed, "Capacity", (ulong)fileBytes.LongLength);
                SetField(compressedType, compressed, "ColorGamut", EnumValue(gamutType, "BT2100"));
                SetField(compressedType, compressed, "ColorTransfer", EnumValue(transferType, "PQ"));
                SetField(compressedType, compressed, "ColorRange", EnumValue(rangeType, "FullRange"));

                object decoder = Activator.CreateInstance(decoderType)!;
                decoderType.GetMethod("SetImage", new[] { compressedType })!.Invoke(decoder, new[] { compressed });
                decoderType.GetMethod("SetOutImagePixelFormat", new[] { pixelFormatType })!.Invoke(decoder, new[] { EnumValue(pixelFormatType, "_32bppRGBA1010102")! });
                decoderType.GetMethod("SetOutColorTransfer", new[] { transferType })!.Invoke(decoder, new[] { EnumValue(transferType, "PQ")! });
                decoderType.GetMethod("Decode", Type.EmptyTypes)!.Invoke(decoder, null);
                object raw = decoderType.GetMethod("GetDecodedImage", Type.EmptyTypes)!.Invoke(decoder, null)!;

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
                    Marshal.Copy(IntPtr.Add(plane, y * rowBytes), row, 0, row.Length);
                    int dst = y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        int o = x * 4;
                        uint packed = (uint)(row[o] | (row[o + 1] << 8) | (row[o + 2] << 16) | (row[o + 3] << 24));
                        output[dst++] = (packed & 0x3ff) / 1023f;
                        output[dst++] = ((packed >> 10) & 0x3ff) / 1023f;
                        output[dst++] = ((packed >> 20) & 0x3ff) / 1023f;
                        output[dst++] = ((packed >> 30) & 0x3) / 3f;
                    }
                }
                _renderer?.SetExternalTextureFromRgba32f(output, width, height, ColorSourceMetadata.PqRec2020Opaque);
                return true;
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UltraHDR decoder declined the image: {ex.Message}");
            return false;
        }
    }

    private static bool ContainsAscii(byte[] data, string text)
    {
        byte[] needle = Encoding.ASCII.GetBytes(text);
        for (int i = 0; i <= data.Length - needle.Length; i++)
        {
            int j = 0;
            for (; j < needle.Length; j++)
            {
                byte value = data[i + j];
                byte expected = needle[j];
                if (value != expected && value != (expected >= 'a' && expected <= 'z' ? (byte)(expected - 32) : expected)) break;
            }
            if (j == needle.Length) return true;
        }
        return false;
    }

    private bool TryDecodeExplicitHdr(byte[] fileBytes)
    {
        if (!ColorMetadataDetector.TryDetect(fileBytes, out ColorSourceMetadata metadata) ||
            metadata.Transfer is not (ColorTransferFunction.Pq or ColorTransferFunction.Hlg))
        {
            return false;
        }

        // WIC color-manages HDR AVIF frames to an 8-bit SDR bitmap. Decode
        // those files through the libavif command line tool shipped by
        // Starward.Codec so the original PQ/HLG code values reach the GPU.
        if (IsAvif(fileBytes) && TryDecodeAvifWithNativeDecoder(fileBytes, metadata))
        {
            return true;
        }

        try
        {
            using var stream = new MemoryStream(fileBytes, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            BitmapSource source = frame.Format == WpfPixelFormats.Rgba128Float
                ? frame
                : new FormatConvertedBitmap(frame, WpfPixelFormats.Rgba128Float, null, 0.0);
            int stride = checked(source.PixelWidth * sizeof(float) * 4);
            float[] pixels = new float[checked(source.PixelWidth * source.PixelHeight * 4)];
            source.CopyPixels(pixels, stride, 0);
            // The WIC AVIF decoder exposes an SDR-rendered Bgr32 frame rather
            // than the signalled PQ/HLG samples. Treat this fallback as sRGB
            // so it cannot be decoded a second time as PQ and overexposed.
            ColorSourceMetadata effectiveMetadata = IsAvif(fileBytes) && frame.Format != WpfPixelFormats.Rgba128Float
                ? ColorSourceMetadata.Sdr
                : metadata;
            _renderer?.SetExternalTextureFromRgba32f(pixels, source.PixelWidth, source.PixelHeight, effectiveMetadata);
            return true;
        }
        catch (Exception ex)
        {
            throw new NotSupportedException(
                "The image declares a PQ/HLG transfer function, but the installed WIC codec cannot expose its pixels without SDR conversion.",
                ex);
        }
    }

    private void DecodeWithPhotoSauce(byte[] fileBytes)
    {
        bool isJpeg = IsJpeg(fileBytes);
        using var stream = new MemoryStream(fileBytes, writable: false);
        var settings = new ProcessImageSettings
        {
            ColorProfileMode = ColorProfileMode.ConvertToSrgb,
            BlendingMode = GammaMode.Linear
        };
        using var pipeline = MagicImageProcessor.BuildPipeline(stream, settings);
        Guid outputFormat = isJpeg
            ? PhotoSauce.MagicScaler.PixelFormats.Bgr24bpp
            : PhotoSauce.MagicScaler.PixelFormats.Bgra32bpp;
        pipeline.AddTransform(new FormatConversionTransform(outputFormat));
        var source = pipeline.PixelSource;
        if (source.Format != outputFormat)
        {
            throw new InvalidDataException(
                $"The SDR decoder returned pixel format {source.Format} instead of the requested {outputFormat}.");
        }

        if (isJpeg)
        {
            DecodeOpaqueSdrJpeg(source);
            return;
        }

        int stride = checked(source.Width * 4);
        byte[] pixels = new byte[checked(stride * source.Height)];
        source.CopyPixels(new Rectangle(0, 0, source.Width, source.Height), stride, pixels);
        _renderer?.SetExternalTextureFromBgra32(pixels, source.Width, source.Height);
    }

    private void DecodeOpaqueSdrJpeg(IPixelSource source)
    {
        int stride = checked(source.Width * 4);
        byte[] pixels = new byte[checked(stride * source.Height)];
        source.CopyPixels(new Rectangle(0, 0, source.Width, source.Height), stride, pixels);

        // Expand each packed BGR row backwards so source bytes cannot be
        // overwritten before they have been copied to their BGRA position.
        for (int y = 0; y < source.Height; y++)
        {
            int rowOffset = y * stride;
            for (int x = source.Width - 1; x >= 0; x--)
            {
                int sourceOffset = rowOffset + x * 3;
                int destinationOffset = rowOffset + x * 4;
                byte blue = pixels[sourceOffset];
                byte green = pixels[sourceOffset + 1];
                byte red = pixels[sourceOffset + 2];
                pixels[destinationOffset] = blue;
                pixels[destinationOffset + 1] = green;
                pixels[destinationOffset + 2] = red;
                pixels[destinationOffset + 3] = byte.MaxValue;
            }
        }

        ColorSourceMetadata metadata = ColorSourceMetadata.Sdr with
        {
            Alpha = AlphaRepresentation.Opaque
        };
        _renderer?.SetExternalTextureFromBgra32(pixels, source.Width, source.Height, metadata);
    }

    private static bool IsJpeg(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff;

    private static bool IsAvif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data.Slice(4, 4).SequenceEqual("ftyp"u8))
            return false;

        uint boxSize = ReadBigEndian32(data);
        int boxEnd = boxSize >= 16 && boxSize <= data.Length
            ? (int)boxSize
            : Math.Min(data.Length, 256);
        for (int offset = 8; offset + 4 <= boxEnd; offset += 4)
        {
            ReadOnlySpan<byte> brand = data.Slice(offset, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return true;
        }
        return false;
    }

    private static uint ReadBigEndian32(ReadOnlySpan<byte> data) =>
        ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];

    private bool TryDecodeAvifWithNativeDecoder(byte[] fileBytes, ColorSourceMetadata metadata)
    {
        string? decoderPath = FindAvifDecoder();
        if (decoderPath == null)
            return false;

        string token = Path.Combine(Path.GetTempPath(), $"BrightEyes-{Guid.NewGuid():N}");
        string inputPath = token + ".avif";
        string outputPath = token + ".png";
        try
        {
            File.WriteAllBytes(inputPath, fileBytes);
            var startInfo = new ProcessStartInfo
            {
                FileName = decoderPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--depth");
            startInfo.ArgumentList.Add("16");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(standardOutput, standardError);
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                Debug.WriteLine($"libavif failed to decode HDR AVIF: {standardError.Result}");
                return false;
            }

            using var stream = File.OpenRead(outputPath);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            var source = new FormatConvertedBitmap(frame, WpfPixelFormats.Rgba64, null, 0.0);
            int width = source.PixelWidth;
            int height = source.PixelHeight;
            int stride = checked(width * 8);
            byte[] raw = new byte[checked(stride * height)];
            source.CopyPixels(raw, stride, 0);
            float[] pixels = new float[checked(width * height * 4)];
            int dst = 0;
            for (int i = 0; i < raw.Length; i += 2)
            {
                ushort value = (ushort)(raw[i] | (raw[i + 1] << 8));
                pixels[dst++] = value / 65535.0f;
            }

            _renderer?.SetExternalTextureFromRgba32f(pixels, width, height, metadata);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Native HDR AVIF decode unavailable: {ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteFile(inputPath);
            TryDeleteFile(outputPath);
        }
    }

    private static string? FindAvifDecoder()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "win-x64", "avifdec.exe"),
            Path.Combine(AppContext.BaseDirectory, "avifdec.exe")
        };
        foreach (string candidate in candidates)
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not remove temporary image file '{path}': {ex.Message}");
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
        var item = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, null, new[] { typeof(int) }, null);
        if (item != null) return item.GetValue(value, new object[] { index })!;
        string[][] candidates = { new[] { "Value0", "Value1", "Value2" }, new[] { "Item0", "Item1", "Item2" }, new[] { "X", "Y", "Z" }, new[] { "_0", "_1", "_2" } };
        foreach (string[] names in candidates)
        {
            var field = type.GetField(names[index], BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(value)!;
        }
        Type elementType = type.IsGenericType ? type.GetGenericArguments()[0] : throw new MissingFieldException(type.FullName, "array element");
        int size = elementType == typeof(IntPtr) ? IntPtr.Size : Marshal.SizeOf(elementType);
        GCHandle pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            IntPtr ptr = pinned.AddrOfPinnedObject() + index * size;
            if (elementType == typeof(IntPtr)) return Marshal.ReadIntPtr(ptr);
            if (elementType == typeof(uint)) return unchecked((uint)Marshal.ReadInt32(ptr));
            if (elementType == typeof(int)) return Marshal.ReadInt32(ptr);
            return Marshal.PtrToStructure(ptr, elementType)!;
        }
        finally { pinned.Free(); }
    }

    private void InspectStarwardCodec()
    {
        try
        {
            var asm = LoadStarwardCodecAssembly();
            if (asm == null) { System.Windows.MessageBox.Show("Starward.Codec is unavailable."); return; }
            Debug.WriteLine($"Assembly: {asm.FullName}");
            foreach (var type in asm.GetExportedTypes())
                if (type.Namespace?.Contains("Starward.Codec", StringComparison.OrdinalIgnoreCase) == true)
                    Debug.WriteLine(type.FullName);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "Codec inspection failed"); }
    }

    public void SetExternalSharedTextureHandle(IntPtr handle, bool isRec2020PQ = true) => _renderer?.SetExternalTextureFromSharedHandle(handle, isRec2020PQ);
    public void SetExternalSharedTextureHandle(IntPtr handle, ColorSourceMetadata metadata) => _renderer?.SetExternalTextureFromSharedHandle(handle, metadata);
    public void SetExternalTexture(ID3D11Texture2D texture, bool isRec2020PQ = true) => _renderer?.SetExternalTexture(texture, isRec2020PQ);
    public void SetExternalTexture(ID3D11Texture2D texture, ColorSourceMetadata metadata) => _renderer?.SetExternalTexture(texture, metadata);
    public void ClearExternalTexture() => _renderer?.ClearExternalTexture();

    private void UpdateSdrCalibration()
    {
        if (_displayColorMetadata.IsHdr) return;
        if (!TryParsePositive(_sdrReferenceWhiteText, out float referenceWhite) ||
            !TryParsePositive(_sdrDisplayLuminanceText, out float displayPeak))
        {
            return;
        }
        _renderer?.SetSdrCalibration(referenceWhite, displayPeak);
    }

    private static bool TryParsePositive(string value, out float result)
    {
        bool parsed = float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
                      float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        return parsed && float.IsFinite(result) && result > 0.0f;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed) return;
        _renderer?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

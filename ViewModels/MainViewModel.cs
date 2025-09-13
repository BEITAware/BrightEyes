using BrightEyes.Renderer;
using System;
using System.Diagnostics;
using System.Windows.Input;
using Vortice.Direct3D11;
using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;

namespace BrightEyes.ViewModels
{
    public class MainViewModel : IDisposable
    {
        private RendererD3D11? _renderer;
        private bool _disposed = false;

        public Action<IntPtr, int, int> InitializeRendererAction { get; }
        public Action<int, int> ResizeRendererAction { get; }
        public ICommand WindowClosingCommand { get; }
        private RelayCommand _inspectCodecCommand;
        public ICommand InspectCodecCommand => _inspectCodecCommand ??= new RelayCommand(_ => InspectStarwardCodec());

        public MainViewModel()
        {
            WindowClosingCommand = new RelayCommand(_ => Dispose());
            Debug.WriteLine("MainViewModel constructor started.");
            InitializeRendererAction = (handle, width, height) =>
            {
                Debug.WriteLine($"InitializeRendererAction invoked with handle: {handle}, width: {width}, height: {height}");
                if (handle != IntPtr.Zero && width > 0 && height > 0)
                {
                    _renderer = new RendererD3D11(handle, width, height);
                    _renderer.Initialize();
                    // Set brighter, lower-saturation blue to fit theme and reduce glare
                    _renderer.SetSdrBackgroundColor(249f/255f, 250f/255f, 254f/255f, 1.0f); // #F9FAFE
                    _renderer.SetHdrBackgroundColor(0.58f, 0.59f, 0.62f, 1.0f);
                    _renderer.Start();
                }
                else
                {
                    Debug.WriteLine("Invalid parameters for renderer initialization.");
                }
            };

            ResizeRendererAction = (width, height) =>
            {
                //Debug.WriteLine($"ResizeRendererAction invoked with width: {width}, height: {height}");
                _renderer?.Resize(width, height);
            };
            Debug.WriteLine("MainViewModel constructor finished.");
        }

        // 将使用新的 .NET Wrapper，因此移除本地 P/Invoke。

        private RelayCommand _openImageCommand;
        public ICommand OpenImageCommand => _openImageCommand ??= new RelayCommand(_ => OpenImage());

        private void OpenImage()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "打开图像",
                Filter = "Images|*.jpg;*.jpeg;*.png;*.avif;*.heif|所有文件 (*.*)|*.*",
                Multiselect = false
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                byte[] fileBytes = File.ReadAllBytes(dlg.FileName);

                // 加载 Starward.Codec 程序集
                var asm = LoadStarwardCodecAssembly();
                if (asm == null)
                {
                    System.Windows.MessageBox.Show("未能加载 Starward.Codec 程序集。", "解码失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                // 反射类型
                Type tDecoder = asm.GetType("Starward.Codec.UltraHdr.UhdrDecoder", throwOnError: true)!;
                Type tCompressedImage = asm.GetType("Starward.Codec.UltraHdr.UhdrCompressedImage", throwOnError: true)!;
                Type tPixelFormat = asm.GetType("Starward.Codec.UltraHdr.UhdrPixelFormat", throwOnError: true)!;
                Type tColorXfer = asm.GetType("Starward.Codec.UltraHdr.UhdrColorTransfer", throwOnError: true)!;
                Type tColorGamut = asm.GetType("Starward.Codec.UltraHdr.UhdrColorGamut", throwOnError: true)!;
                Type tColorRange = asm.GetType("Starward.Codec.UltraHdr.UhdrColorRange", throwOnError: true)!;

                // 构造压缩图像描述（避免 ReadOnlySpan 反射问题）
                object compressed = Activator.CreateInstance(tCompressedImage)!;
                GCHandle hFile = GCHandle.Alloc(fileBytes, GCHandleType.Pinned);
                try
                {
                    SetField(tCompressedImage, compressed, "Data", hFile.AddrOfPinnedObject());
                    SetField(tCompressedImage, compressed, "DataSize", (ulong)fileBytes.LongLength);
                    SetField(tCompressedImage, compressed, "Capacity", (ulong)fileBytes.LongLength);
                    SetField(tCompressedImage, compressed, "ColorGamut", EnumValue(tColorGamut, "BT2100"));
                    SetField(tCompressedImage, compressed, "ColorTransfer", EnumValue(tColorXfer, "PQ"));
                    SetField(tCompressedImage, compressed, "ColorRange", EnumValue(tColorRange, "FullRange"));

                    // 解码
                    dynamic dec = Activator.CreateInstance(tDecoder)!;
                    // SetImage(UhdrCompressedImage)
                    tDecoder.GetMethod("SetImage", new[] { tCompressedImage })!.Invoke(dec, new object[] { compressed });
                    // 输出像素格式：RGBA10:10:10:2 更易转换到 RGBA32F
                    tDecoder.GetMethod("SetOutImagePixelFormat", new[] { tPixelFormat })!.Invoke(dec, new object[] { EnumValue(tPixelFormat, "_32bppRGBA1010102")! });
                    // 输出传递特性：PQ
                    tDecoder.GetMethod("SetOutColorTransfer", new[] { tColorXfer })!.Invoke(dec, new object[] { EnumValue(tColorXfer, "PQ")! });

                    tDecoder.GetMethod("Decode", Type.EmptyTypes)!.Invoke(dec, null);
                    object raw = tDecoder.GetMethod("GetDecodedImage", Type.EmptyTypes)!.Invoke(dec, null)!;

                    // 读取宽高与 Plane/Stride
                    Type tRaw = raw.GetType();
                    int width = Convert.ToInt32(GetFieldValue(tRaw, raw, "Width"));
                    int height = Convert.ToInt32(GetFieldValue(tRaw, raw, "Height"));
                    object planeArr = GetFieldValue(tRaw, raw, "Plane")!;
                    object strideArr = GetFieldValue(tRaw, raw, "Stride")!;

                    IntPtr plane0 = (IntPtr)GetFixedArray3Element(planeArr, 0);
                    uint stridePixels = Convert.ToUInt32(GetFixedArray3Element(strideArr, 0));
                    int rowBytes = checked((int)stridePixels * 4);

                    // 将 RGBA1010102 归一化为 RGBA32F
                    var output = new float[width * height * 4];
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr rowPtr = IntPtr.Add(plane0, y * rowBytes);
                        var row = new byte[width * 4];
                        Marshal.Copy(rowPtr, row, 0, row.Length);
                        int dst = y * width * 4;
                        for (int x = 0; x < width; x++)
                        {
                            int o = x * 4;
                            uint p = (uint)(row[o] | (row[o + 1] << 8) | (row[o + 2] << 16) | (row[o + 3] << 24));
                            output[dst++] = (p & 0x3FF) / 1023f;
                            output[dst++] = ((p >> 10) & 0x3FF) / 1023f;
                            output[dst++] = ((p >> 20) & 0x3FF) / 1023f;
                            output[dst++] = ((p >> 30) & 0x3) / 3f;
                        }
                    }

                    _renderer?.SetExternalTextureFromRgba32f(output, width, height, isRec2020PQ: true);
                }
                finally
                {
                    if (hFile.IsAllocated) hFile.Free();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                System.Windows.MessageBox.Show(ex.Message, "解码失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private static Assembly? LoadStarwardCodecAssembly()
        {
            try
            {
                return Assembly.Load("Starward.Codec");
            }
            catch
            {
                var baseDir = AppContext.BaseDirectory;
                var dll = Path.Combine(baseDir, "Starward.Codec.dll");
                if (File.Exists(dll)) return Assembly.LoadFrom(dll);
                return null;
            }
        }

        private static void SetField(Type type, object instance, string fieldName, object? value)
        {
            var f = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) throw new MissingFieldException(type.FullName, fieldName);
            f.SetValue(instance, value);
        }

        private static object? GetFieldValue(Type type, object instance, string fieldName)
        {
            var f = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) throw new MissingFieldException(type.FullName, fieldName);
            return f.GetValue(instance);
        }

        private static object? EnumValue(Type enumType, string name)
        {
            var f = enumType.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (f == null) throw new ArgumentException($"Enum {enumType.Name} 无此字段: {name}");
            return f.GetValue(null);
        }

        private static object GetFixedArray3Element(object fixedArray3, int index)
        {
            var t = fixedArray3.GetType();
            // 尝试默认索引器 Item[int]
            var itemProp = t.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance, null, null, new[] { typeof(int) }, null);
            if (itemProp != null)
            {
                return itemProp.GetValue(fixedArray3, new object[] { index })!;
            }
            // 尝试常见字段名
            string[][] candidates = new[]
            {
                new[] { "Value0", "Value1", "Value2" },
                new[] { "Item0", "Item1", "Item2" },
                new[] { "X", "Y", "Z" },
                new[] { "_0", "_1", "_2" }
            };
            foreach (var names in candidates)
            {
                var f = t.GetField(names[index], BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return f.GetValue(fixedArray3)!;
            }
            // 直接按顺序读取内存（FixedArray3<T> 是顺序布局的3个元素）
            // 兼容 IntPtr / UInt32 / Int32 / Single 等常见类型
            var elemType = t.IsGenericType ? t.GetGenericArguments()[0] : null;
            if (elemType == null) throw new MissingFieldException(t.FullName, $"[index {index}] 元素不可访问");
            int elemSize = elemType == typeof(IntPtr) ? IntPtr.Size : Marshal.SizeOf(elemType);
            GCHandle handle = GCHandle.Alloc(fixedArray3, GCHandleType.Pinned);
            try
            {
                IntPtr basePtr = handle.AddrOfPinnedObject() + index * elemSize;
                if (elemType == typeof(IntPtr)) return Marshal.ReadIntPtr(basePtr);
                if (elemType == typeof(uint)) return unchecked((uint)Marshal.ReadInt32(basePtr));
                if (elemType == typeof(int)) return Marshal.ReadInt32(basePtr);
                if (elemType == typeof(float))
                {
                    var bytes = new byte[4];
                    Marshal.Copy(basePtr, bytes, 0, 4);
                    return BitConverter.ToSingle(bytes, 0);
                }
                // 通用回退
                return Marshal.PtrToStructure(basePtr, elemType)!;
            }
            finally
            {
                handle.Free();
            }
        }

        private void InspectStarwardCodec()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var candidates = new[]
                {
                    Path.Combine(baseDir, "Starward.Codec.dll"),
                    "Starward.Codec"
                };

                System.Reflection.Assembly? asm = null;
                foreach (var c in candidates)
                {
                    try
                    {
                        if (c.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(c))
                        {
                            asm = System.Reflection.Assembly.LoadFrom(c);
                            break;
                        }
                        else
                        {
                            asm = System.Reflection.Assembly.Load(c);
                            break;
                        }
                    }
                    catch
                    {
                        // try next
                    }
                }

                if (asm == null)
                {
                    Debug.WriteLine("[Starward.Codec] 未能加载程序集。请确认已引用并复制到输出目录。");
                    System.Windows.MessageBox.Show("未能加载 Starward.Codec 程序集，请确认已安装到项目并复制到输出目录。", "Starward.Codec", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                var types = asm.GetExportedTypes();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Assembly: {asm.FullName}");

                foreach (var t in types)
                {
                    if (!(t.Namespace?.Contains("Starward", StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                    if (!(t.Namespace?.Contains("Codec", StringComparison.OrdinalIgnoreCase) ?? false)) continue;

                    sb.AppendLine($"\nType: {t.FullName}");

                    // Constructors
                    foreach (var c in t.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        var pars = c.GetParameters();
                        var sig = string.Join(", ", Array.ConvertAll(pars, p => $"{p.ParameterType.Name} {p.Name}"));
                        sb.AppendLine($"  ctor({sig})");
                    }

                    // Methods (filter common decode terms)
                    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                    {
                        var name = m.Name;
                        if (name.Contains("Decode", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Ultra", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("HDR", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("AVIF", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("HEIF", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Set", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Get", StringComparison.OrdinalIgnoreCase))
                        {
                            var pars = m.GetParameters();
                            var sig = string.Join(", ", Array.ConvertAll(pars, p => $"{p.ParameterType.Name} {p.Name}"));
                            sb.AppendLine($"  - {m.ReturnType.Name} {name}({sig})");
                        }
                    }

                    // Properties (common image properties)
                    foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                    {
                        if (p.Name.Contains("Width", StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Contains("Height", StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Contains("Format", StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Contains("Color", StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Contains("Stride", StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Contains("Plane", StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Contains("Data", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.AppendLine($"  * {p.PropertyType.Name} {p.Name}");
                        }
                    }

                    // Fields (for structs like UhdrCompressedImage / UhdrRawImage)
                    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
                    {
                        if (f.IsPublic)
                        {
                            sb.AppendLine($"  # {f.FieldType.Name} {f.Name}");
                        }
                    }

                    // Enum values
                    if (t.IsEnum)
                    {
                        var names = Enum.GetNames(t);
                        foreach (var n in names)
                        {
                            var val = Convert.ToInt32(Enum.Parse(t, n));
                            sb.AppendLine($"  = {n} ({val})");
                        }
                    }
                }

                var text = sb.ToString();
                Debug.WriteLine(text);
                // 输出到调试控制台，不再弹出长对话框
                Debug.WriteLine("[Starward.Codec] 反射信息已输出到调试控制台。");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                System.Windows.MessageBox.Show(ex.Message, "Starward.Codec 反射失败", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 供外部设置共享句柄的RGBA32F纹理作为输入。默认视为Rec.2020 PQ。
        /// </summary>
        public void SetExternalSharedTextureHandle(IntPtr sharedHandle, bool isRec2020PQ = true)
        {
            _renderer?.SetExternalTextureFromSharedHandle(sharedHandle, isRec2020PQ);
        }

        /// <summary>
        /// 供外部直接传入同进程创建的ID3D11Texture2D（RGBA32F）。要求与渲染器同一D3D设备或兼容。
        /// </summary>
        public void SetExternalTexture(ID3D11Texture2D texture, bool isRec2020PQ = true)
        {
            _renderer?.SetExternalTexture(texture, isRec2020PQ);
        }

      /// <summary>
      /// 清除外部纹理，恢复内部测试渲染。
      /// </summary>
      public void ClearExternalTexture()
      {
          _renderer?.ClearExternalTexture();
      }

        public void Dispose()
        {
            Debug.WriteLine("MainViewModel Dispose started.");
            Dispose(true);
            GC.SuppressFinalize(this);
            Debug.WriteLine("MainViewModel Dispose finished.");
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _renderer?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
using System;
using System.Diagnostics;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using System.Numerics;
using Vortice.Mathematics;
using System.IO;
using System.Runtime.InteropServices;

namespace BrightEyes.Renderer
{
    public class RendererD3D11 : IDisposable
    {
        private IntPtr _windowHandle;
        private int _width;
        private int _height;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _deviceContext;
        private IDXGISwapChain1? _swapChain;
        private ID3D11RenderTargetView? _renderTargetView;

        private ID3D11Texture2D? _hdrRenderTarget;
        private ID3D11RenderTargetView? _hdrRenderTargetView;

        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _pixelShader;
        private ID3D11PixelShader? _presentPixelShader;
        private ID3D11ShaderResourceView? _hdrShaderResourceView;
        private ColorManagementEffect? _colorManagementEffect;

        private ID3D11SamplerState? _linearClampSampler;
        private ID3D11SamplerState? _pointClampSampler;
        private ID3D11Buffer? _scaleConstantBuffer;

        [StructLayout(LayoutKind.Sequential)]
        private struct ScaleConstants
        {
            public Vector2 SourceSize;
            public Vector2 SourceTexelSize;
        }

        // External texture support
        private ID3D11Texture2D? _externalTexture;
        private ID3D11ShaderResourceView? _externalTextureShaderResourceView;
        private bool _externalTextureIsRec2020PQ = true;
        private ColorSourceMetadata _externalColorMetadata = ColorSourceMetadata.PqRec2020Opaque;
        private bool _externalTextureOwned = false;

        private Thread? _renderThread;
        private bool _isRunning = true;
        private readonly object _lockObject = new object();

        // Tracks current content size for aspect-correct viewport
        private int _sourceWidth;
        private int _sourceHeight;

        // Background colors for SDR and HDR outputs
        // SDR default: very light low-saturation blue near SDR paper white (#F8FAFE)
        private Color4 _sdrBackgroundColor = new Color4(0.9725f, 0.9804f, 0.9961f, 1.0f);
        // HDR default: slightly brighter, low-saturation blue (kept below peak white)
        private Color4 _hdrBackgroundColor = new Color4(0.58f, 0.59f, 0.62f, 1.0f);
        private bool _isHdrOutput = false;
        private DisplayColorMetadata _displayColorMetadata = DisplayColorMetadata.SdrFallback;
        private float _sdrReferenceWhiteNits = 100.0f;
        private float _sdrDisplayPeakNits = 100.0f;
        private Vector4 _sdrBackgroundEncoded = new Vector4(0.9725f, 0.9804f, 0.9961f, 1.0f);
        private Vector4 _hdrBackgroundEncoded = new Vector4(0.58f, 0.59f, 0.62f, 1.0f);
        private int _scaleSourceWidth = -1;
        private int _scaleSourceHeight = -1;

        public RendererD3D11(IntPtr windowHandle, int width, int height)
        {
            _windowHandle = windowHandle;
            _width = width;
            _height = height;
        }

        public DisplayColorMetadata DisplayColorMetadata => _displayColorMetadata;

        public bool IsHdrOutput => _isHdrOutput;

        private const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

        public void Initialize()
        {
            try
            {
                var deviceCreationFlags = DeviceCreationFlags.None;
#if DEBUG
                deviceCreationFlags |= DeviceCreationFlags.Debug;
#endif
                D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    deviceCreationFlags,
                    new[] { FeatureLevel.Level_11_0 },
                    out _device).CheckError();
                
                _deviceContext = _device.ImmediateContext;

                var swapChainDescription = new SwapChainDescription1
                {
                    Width = _width,
                    Height = _height,
                    Format = Format.R10G10B10A2_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipDiscard,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = SwapChainFlags.None
                };

                DXGI.CreateDXGIFactory1<IDXGIFactory2>(out var factory).CheckError();
                
                _swapChain = factory.CreateSwapChainForHwnd(
                    _device,
                    _windowHandle,
                    swapChainDescription
                );
                // Query the output nearest this window instead of assuming adapter/output zero.
                bool hdrSupported = false;
                DisplayColorMetadata displayMetadata = DisplayColorMetadata.SdrFallback;
                try
                {
                    displayMetadata = DetectDisplayMetadata(factory);
                    hdrSupported = displayMetadata.IsHdr;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not detect HDR support: {ex.Message}");
                    hdrSupported = false; // Fallback to SDR
                    displayMetadata = DisplayColorMetadata.SdrFallback;
                }
                finally
                {
                    factory.Dispose();
                }

                if (hdrSupported)
                {
                    Debug.WriteLine("HDR display detected. Setting color space to HDR10 (PQ/Rec.2020).");
                    using (var swapChain3 = _swapChain.QueryInterface<IDXGISwapChain3>())
                    {
                        const ColorSpaceType colorSpace = ColorSpaceType.RgbFullG2084NoneP2020;
                        if (swapChain3.CheckColorSpaceSupport(colorSpace).HasFlag(SwapChainColorSpaceSupportFlags.Present))
                        {
                            swapChain3.SetColorSpace1(colorSpace);
                        }
                        else
                        {
                            // An HDR-capable output is not enough: the actual
                            // swap chain must be able to present HDR10.
                            hdrSupported = false;
                            displayMetadata = displayMetadata with { IsHdr = false };
                        }
                    }
                }
                else
                {
                    using (var swapChain3 = _swapChain.QueryInterface<IDXGISwapChain3>())
                    {
                        const ColorSpaceType colorSpace = ColorSpaceType.RgbFullG22NoneP709;
                        if (swapChain3.CheckColorSpaceSupport(colorSpace).HasFlag(SwapChainColorSpaceSupportFlags.Present))
                        {
                            swapChain3.SetColorSpace1(colorSpace);
                        }
                    }
                    Debug.WriteLine("SDR display detected. Using the sRGB/Rec.709 terminal color space.");
                }

                // Track whether swapchain is presenting in HDR colorspace
                _isHdrOutput = hdrSupported;
                _displayColorMetadata = displayMetadata with { IsHdr = hdrSupported };

                CreateResources();

                // Compile and create shaders
                string baseDirectory = AppContext.BaseDirectory;
                string vsPath = Path.Combine(baseDirectory, "Renderer/Shaders/FullScreenVS.hlsl");
                string psPath = Path.Combine(baseDirectory, "Renderer/Shaders/HDRWhitePS.hlsl");
                string presentPsPath = Path.Combine(baseDirectory, "Renderer/Shaders/PresentPS.hlsl");

               var vertexShaderByteCode = Compiler.CompileFromFile(vsPath, "main", "vs_5_0");
               _vertexShader = _device.CreateVertexShader(vertexShaderByteCode.Span);

               var pixelShaderByteCode = Compiler.CompileFromFile(psPath, "main", "ps_5_0");
               _pixelShader = _device.CreatePixelShader(pixelShaderByteCode.Span);

                var presentPsByteCode = Compiler.CompileFromFile(presentPsPath, "main", "ps_5_0");
                _presentPixelShader = _device.CreatePixelShader(presentPsByteCode.Span);

               _colorManagementEffect = new ColorManagementEffect(_device);

               // Create a default linear clamp sampler for tone-mapped sampling
               var samplerDesc = new SamplerDescription
               {
                   Filter = Filter.MinMagMipLinear,
                   AddressU = TextureAddressMode.Clamp,
                   AddressV = TextureAddressMode.Clamp,
                   AddressW = TextureAddressMode.Clamp
               };
               _linearClampSampler = _device.CreateSamplerState(samplerDesc);

               // Linear/anisotropic sampler for bicubic 4-fetch method
               var pointSamplerDesc = new SamplerDescription
               {
                   Filter = Filter.Anisotropic,
                   AddressU = TextureAddressMode.Clamp,
                   AddressV = TextureAddressMode.Clamp,
                   AddressW = TextureAddressMode.Clamp,
                   MaxAnisotropy = 8,
               };
               _pointClampSampler = _device.CreateSamplerState(pointSamplerDesc);

               // Constant buffer for scaling parameters
               var cbDesc = new BufferDescription
               {
                   ByteWidth = Marshal.SizeOf<ScaleConstants>(),
                   Usage = ResourceUsage.Dynamic,
                   BindFlags = BindFlags.ConstantBuffer,
                   CPUAccessFlags = CpuAccessFlags.Write,
                   MiscFlags = ResourceOptionFlags.None,
                   StructureByteStride = 0
               };
               _scaleConstantBuffer = _device.CreateBuffer(cbDesc);
           }
            catch (Exception ex)
            {
                Debug.WriteLine("An exception occurred during D3D11 initialization:");
                Debug.WriteLine(ex.ToString());
                throw;
            }
        }

        private DisplayColorMetadata DetectDisplayMetadata(IDXGIFactory2 factory)
        {
            IntPtr targetMonitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
            bool hasSystemSdrWhite = false;
            float systemSdrWhiteNits = 0.0f;
            try
            {
                hasSystemSdrWhite = WindowsDisplayMetadata.TryGetSdrWhiteLevelNits(targetMonitor, out systemSdrWhiteNits);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Could not query the system SDR white level: {ex.Message}");
            }
            DisplayColorMetadata? firstOutput = null;
            using var factory1 = factory.QueryInterface<IDXGIFactory1>();
            for (int adapterIndex = 0; ; adapterIndex++)
            {
                if (factory1.EnumAdapters1(adapterIndex, out var adapter).Failure)
                {
                    break;
                }
                using (adapter)
                {
                    for (int outputIndex = 0; ; outputIndex++)
                    {
                        if (adapter.EnumOutputs(outputIndex, out var output).Failure)
                        {
                            break;
                        }
                        using (output)
                        using (var output6 = output.QueryInterface<IDXGIOutput6>())
                        {
                            var description = output6.Description1;
                            bool isHdr = description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                            var metadata = new DisplayColorMetadata(
                                isHdr,
                                Math.Max(description.MinLuminance, 0.0f),
                                Math.Max(description.MaxLuminance, 0.0f),
                                Math.Max(description.MaxFullFrameLuminance, 0.0f),
                                hasSystemSdrWhite ? systemSdrWhiteNits : (isHdr ? 203.0f : 100.0f),
                                hasSystemSdrWhite,
                                description.ColorSpace.ToString());
                            firstOutput ??= metadata;
                            if (targetMonitor == IntPtr.Zero || description.Monitor == targetMonitor)
                            {
                                return metadata;
                            }
                        }
                    }
                }
            }
            return firstOutput ?? DisplayColorMetadata.SdrFallback;
        }

        private void CreateResources()
        {
            using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                _renderTargetView = _device.CreateRenderTargetView(backBuffer);
            }

            // Create HDR Render Target
            var hdrDesc = new Texture2DDescription
            {
                Width = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.None,
            };
            _hdrRenderTarget = _device.CreateTexture2D(hdrDesc);
            _hdrRenderTargetView = _device.CreateRenderTargetView(_hdrRenderTarget);
           _hdrShaderResourceView = _device.CreateShaderResourceView(_hdrRenderTarget);

            UpdateViewport();
        }

        private void UpdateScaleConstants(int sourceWidth, int sourceHeight)
        {
            if (_deviceContext == null || _scaleConstantBuffer == null)
            {
                return;
            }
            if (_scaleSourceWidth == sourceWidth && _scaleSourceHeight == sourceHeight)
            {
                return;
            }

            var constants = new ScaleConstants
            {
                SourceSize = new Vector2(sourceWidth, sourceHeight),
                SourceTexelSize = new Vector2(sourceWidth > 0 ? 1.0f / sourceWidth : 0.0f, sourceHeight > 0 ? 1.0f / sourceHeight : 0.0f)
            };

            var mapped = _deviceContext.Map(_scaleConstantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                Marshal.StructureToPtr(constants, mapped.DataPointer, false);
            }
            finally
            {
                _deviceContext.Unmap(_scaleConstantBuffer, 0);
            }
            _scaleSourceWidth = sourceWidth;
            _scaleSourceHeight = sourceHeight;
        }

        public void Start()
        {
            _renderThread = new Thread(RunRenderLoop) { IsBackground = true, Name = "D3DRenderLoop" };
            _renderThread.Start();
        }

        private void RunRenderLoop()
        {
            while (_isRunning)
            {
                lock (_lockObject)
                {
                    // Clear backbuffer so letterbox/pillarbox areas use theme background
                    _deviceContext.OMSetRenderTargets(_renderTargetView);
                    _deviceContext.ClearRenderTargetView(_renderTargetView, _isHdrOutput ? _hdrBackgroundColor : _sdrBackgroundColor);
                    if (_externalTextureShaderResourceView != null)
                    {
                        // External texture path
                        if (_externalTextureIsRec2020PQ &&
                            _externalColorMetadata.Alpha == AlphaRepresentation.Opaque &&
                            _isHdrOutput)
                        {
                            // Direct present (pass-through) to swap chain backbuffer.
                            _deviceContext.OMSetRenderTargets(_renderTargetView);
                            _deviceContext.VSSetShader(_vertexShader);
                            _deviceContext.PSSetShader(_presentPixelShader);
                            _deviceContext.PSSetShaderResources(0, new[] { _externalTextureShaderResourceView });
                            if (_scaleConstantBuffer != null)
                            {
                                UpdateScaleConstants(_sourceWidth, _sourceHeight);
                                _deviceContext.PSSetConstantBuffers(0, new[] { _scaleConstantBuffer });
                            }
                            if (_pointClampSampler != null)
                            {
                                _deviceContext.PSSetSamplers(0, new[] { _pointClampSampler });
                            }
                            _deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                            _deviceContext.Draw(3, 0);
                        }
                        else
                        {
                            // BCM bridge: source -> absolute linear Rec.2020/nits -> terminal encoding.
                            _colorManagementEffect?.Apply(
                                _deviceContext,
                                _externalTextureShaderResourceView,
                                _renderTargetView,
                                _sourceWidth,
                                _sourceHeight,
                                _externalColorMetadata,
                                _displayColorMetadata,
                                _sdrReferenceWhiteNits,
                                _sdrDisplayPeakNits,
                                _sdrBackgroundEncoded,
                                _hdrBackgroundEncoded);
                        }
                    }
                    else
                    {
                        // Internal test pattern path: render to a PQ working texture,
                        // then pass it through the same BCM terminal bridge.
                        _deviceContext.OMSetRenderTargets(_hdrRenderTargetView);
                        _deviceContext.ClearRenderTargetView(_hdrRenderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
                        _deviceContext.VSSetShader(_vertexShader);
                        _deviceContext.PSSetShader(_pixelShader);
                        _deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        _deviceContext.Draw(3, 0);

                        _colorManagementEffect?.Apply(
                            _deviceContext,
                            _hdrShaderResourceView,
                            _renderTargetView,
                            _width,
                            _height,
                            ColorSourceMetadata.PqRec2020Opaque,
                            _displayColorMetadata,
                            _sdrReferenceWhiteNits,
                            _sdrDisplayPeakNits,
                            _sdrBackgroundEncoded,
                            _hdrBackgroundEncoded);
                    }

                    // Step F: Present the final result to the screen
                    _swapChain.Present(1, 0);
               }
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Sets an external texture as the source to render. The compatibility
        /// overload preserves the historical PQ/legacy-linear defaults; callers
        /// that know the exact transfer and gamut should use the metadata overload.
        /// </summary>
        public void SetExternalTexture(ID3D11Texture2D texture, bool isRec2020PQ = true)
        {
            SetExternalTexture(texture, isRec2020PQ
                ? ColorSourceMetadata.PqRec2020Opaque
                : ColorSourceMetadata.LegacyLinearProPhoto);
        }

        public void SetExternalTexture(ID3D11Texture2D texture, ColorSourceMetadata colorMetadata)
        {
            if (_device == null)
            {
                throw new InvalidOperationException("Renderer not initialized.");
            }
            lock (_lockObject)
            {
                // Release previous owned texture if any
                _externalTextureShaderResourceView?.Dispose();
                if (_externalTextureOwned)
                {
                    _externalTexture?.Dispose();
                }

                _externalTexture = texture; // Borrowed: do not Dispose on Clear/Dispose
                _externalColorMetadata = colorMetadata.Normalize();
                _externalTextureIsRec2020PQ = _externalColorMetadata.Transfer == ColorTransferFunction.Pq &&
                                               _externalColorMetadata.Primaries == ColorPrimaries.Rec2020;
                _externalTextureShaderResourceView = _device.CreateShaderResourceView(texture);
                _externalTextureOwned = false;

                // Track source size and update viewport
                var desc = texture.Description;
                _sourceWidth = desc.Width;
                _sourceHeight = desc.Height;
                UpdateViewport();
            }
        }

        /// <summary>
        /// Opens a shared D3D11 texture by handle and sets it as the external source.
        /// The shared resource must be created with appropriate shared flags by the producer.
        /// </summary>
        public void SetExternalTextureFromSharedHandle(IntPtr sharedHandle, bool isRec2020PQ = true)
        {
            SetExternalTextureFromSharedHandle(sharedHandle, isRec2020PQ
                ? ColorSourceMetadata.PqRec2020Opaque
                : ColorSourceMetadata.LegacyLinearProPhoto);
        }

        public void SetExternalTextureFromSharedHandle(IntPtr sharedHandle, ColorSourceMetadata colorMetadata)
        {
            if (_device == null)
            {
                throw new InvalidOperationException("Renderer not initialized.");
            }
            lock (_lockObject)
            {
                _externalTextureShaderResourceView?.Dispose();
                if (_externalTextureOwned)
                {
                    _externalTexture?.Dispose();
                }

                var openedTexture = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
                _externalTexture = openedTexture; // Owned by renderer
                _externalColorMetadata = colorMetadata.Normalize();
                _externalTextureIsRec2020PQ = _externalColorMetadata.Transfer == ColorTransferFunction.Pq &&
                                               _externalColorMetadata.Primaries == ColorPrimaries.Rec2020;
                _externalTextureShaderResourceView = _device.CreateShaderResourceView(openedTexture);
                _externalTextureOwned = true;

                var desc = openedTexture.Description;
                _sourceWidth = desc.Width;
                _sourceHeight = desc.Height;
                UpdateViewport();
            }
        }

        /// <summary>
        /// Clears and releases any external texture currently bound.
        /// </summary>
        public void ClearExternalTexture()
        {
            lock (_lockObject)
            {
                _externalTextureShaderResourceView?.Dispose();
                _externalTextureShaderResourceView = null;
                if (_externalTextureOwned)
                {
                    _externalTexture?.Dispose();
                }
                _externalTexture = null;
                _externalTextureOwned = false;
                _externalColorMetadata = ColorSourceMetadata.Sdr;
                _externalTextureIsRec2020PQ = false;

                // Reset to full-area viewport when no external content
                _sourceWidth = 0;
                _sourceHeight = 0;
                UpdateViewport();
            }
        }

        public void Resize(int newWidth, int newHeight)
        {
            lock (_lockObject)
            {
                if (newWidth == 0 || newHeight == 0)
                {
                    return;
                }

                _width = newWidth;
                _height = newHeight;

                _renderTargetView?.Dispose();
                _hdrRenderTargetView?.Dispose();
               _hdrShaderResourceView?.Dispose();
               _hdrRenderTarget?.Dispose();

                _swapChain.ResizeBuffers(0, newWidth, newHeight, Format.R10G10B10A2_UNorm, SwapChainFlags.None);

                CreateResources();
            }
        }

        /// <summary>
        /// Creates a GPU texture from CPU-side RGBA32F data and sets it as the external source.
        /// </summary>
        public void SetExternalTextureFromRgba32f(float[] rgba32f, int width, int height, bool isRec2020PQ = true)
        {
            SetExternalTextureFromRgba32f(rgba32f, width, height, isRec2020PQ
                ? ColorSourceMetadata.PqRec2020Opaque
                : ColorSourceMetadata.LegacyLinearProPhoto);
        }

        public void SetExternalTextureFromRgba32f(float[] rgba32f, int width, int height, ColorSourceMetadata colorMetadata)
        {
            if (_device == null)
            {
                throw new InvalidOperationException("Renderer not initialized.");
            }
            if (rgba32f == null) throw new ArgumentNullException(nameof(rgba32f));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("Invalid image size");
            if (rgba32f.Length < width * height * 4) throw new ArgumentException("RGBA32F buffer too small");

            lock (_lockObject)
            {
                // Create texture with full mip-chain and enable auto-gen mips
                var desc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 0, // full chain
                    ArraySize = 1,
                    Format = Format.R32G32B32A32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.GenerateMips
                };

                var newTexture = _device.CreateTexture2D(desc);

                int rowPitchBytes = width * sizeof(float) * 4;
                GCHandle handle = GCHandle.Alloc(rgba32f, GCHandleType.Pinned);
                try
                {
                    if (_deviceContext == null)
                    {
                        throw new InvalidOperationException("Device context not initialized.");
                    }
                    _deviceContext.UpdateSubresource(new MappedSubresource(handle.AddrOfPinnedObject(), rowPitchBytes, 0), newTexture, 0);
                }
                finally
                {
                    handle.Free();
                }

                // Replace current external texture
                _externalTextureShaderResourceView?.Dispose();
                if (_externalTextureOwned)
                {
                    _externalTexture?.Dispose();
                }
                _externalTexture = newTexture;
                _externalTextureOwned = true;
                _externalColorMetadata = colorMetadata.Normalize();
                _externalTextureIsRec2020PQ = _externalColorMetadata.Transfer == ColorTransferFunction.Pq &&
                                               _externalColorMetadata.Primaries == ColorPrimaries.Rec2020;
                _externalTextureShaderResourceView = _device.CreateShaderResourceView(_externalTexture);

                // Generate mipmaps for better minification quality
                if (_deviceContext != null && _externalTextureShaderResourceView != null)
                {
                    _deviceContext.GenerateMips(_externalTextureShaderResourceView);
                }

                _sourceWidth = width;
                _sourceHeight = height;
                UpdateViewport();
            }
        }

        /// <summary>
        /// Uploads PhotoSauce's normalized BGRA8 output to the existing tone-mapping pipeline.
        /// This avoids the large CPU-side RGBA32F expansion used by HDR sources.
        /// </summary>
        public void SetExternalTextureFromBgra32(byte[] bgra, int width, int height)
        {
            SetExternalTextureFromBgra32(bgra, width, height, ColorSourceMetadata.Sdr);
        }

        public void SetExternalTextureFromBgra32(
            byte[] bgra,
            int width,
            int height,
            ColorSourceMetadata colorMetadata)
        {
            if (_device == null) throw new InvalidOperationException("Renderer not initialized.");
            if (bgra == null) throw new ArgumentNullException(nameof(bgra));
            int rowPitch = checked(width * 4);
            if (width <= 0 || height <= 0 || bgra.Length < checked(rowPitch * height)) throw new ArgumentException("Invalid BGRA image buffer.", nameof(bgra));

            lock (_lockObject)
            {
                var desc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 0,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    MiscFlags = ResourceOptionFlags.GenerateMips
                };
                var texture = _device.CreateTexture2D(desc);
                var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    _deviceContext!.UpdateSubresource(new MappedSubresource(handle.AddrOfPinnedObject(), rowPitch, 0), texture, 0);
                }
                finally { handle.Free(); }
                ReplaceExternalTexture(texture, colorMetadata);
                _deviceContext!.GenerateMips(_externalTextureShaderResourceView!);
                _sourceWidth = width;
                _sourceHeight = height;
                UpdateViewport();
            }
        }

        private void ReplaceExternalTexture(ID3D11Texture2D texture, ColorSourceMetadata colorMetadata)
        {
            _externalTextureShaderResourceView?.Dispose();
            if (_externalTextureOwned) _externalTexture?.Dispose();
            _externalTexture = texture;
            _externalTextureOwned = true;
            _externalColorMetadata = colorMetadata.Normalize();
            _externalTextureIsRec2020PQ = _externalColorMetadata.Transfer == ColorTransferFunction.Pq &&
                                           _externalColorMetadata.Primaries == ColorPrimaries.Rec2020;
            _externalTextureShaderResourceView = _device!.CreateShaderResourceView(texture);
        }

        public void Dispose()
        {
            _isRunning = false;
            _renderThread?.Join();
            
            _renderTargetView?.Dispose();
            _hdrRenderTargetView?.Dispose();
            _hdrRenderTarget?.Dispose();
           _hdrShaderResourceView?.Dispose();
           _vertexShader?.Dispose();
           _pixelShader?.Dispose();
           _presentPixelShader?.Dispose();
            _colorManagementEffect?.Dispose();
           _linearClampSampler?.Dispose();
           _pointClampSampler?.Dispose();
           _scaleConstantBuffer?.Dispose();
           _externalTextureShaderResourceView?.Dispose();
           if (_externalTextureOwned)
           {
               _externalTexture?.Dispose();
           }
           _swapChain?.Dispose();
           _deviceContext?.Dispose();
           _device?.Dispose();
        }

        /// <summary>
        /// Computes and applies an aspect-correct viewport centered in the render area.
        /// </summary>
        private void UpdateViewport()
        {
            if (_deviceContext == null)
            {
                return;
            }
            if (_width <= 0 || _height <= 0)
            {
                return;
            }

            int contentW = _sourceWidth > 0 ? _sourceWidth : _width;
            int contentH = _sourceHeight > 0 ? _sourceHeight : _height;

            float contentAspect = (float)contentW / Math.Max(1, contentH);
            float targetAspect = (float)_width / Math.Max(1, _height);

            float vpWidth;
            float vpHeight;
            float vpX;
            float vpY;

            if (contentAspect > targetAspect)
            {
                // Wider than area: pillarbox
                vpWidth = _width;
                vpHeight = _width / contentAspect;
                vpX = 0.0f;
                vpY = (_height - vpHeight) * 0.5f;
            }
            else
            {
                // Taller than area: letterbox
                vpHeight = _height;
                vpWidth = _height * contentAspect;
                vpY = 0.0f;
                vpX = (_width - vpWidth) * 0.5f;
            }

            var viewport = new Viewport(vpX, vpY, vpWidth, vpHeight, 0.0f, 1.0f);
            _deviceContext.RSSetViewport(viewport);
        }

        /// <summary>
        /// Sets SDR background color used for clears. Values are in [0,1].
        public void SetSdrBackgroundColor(float red, float green, float blue, float alpha = 1.0f)
        {
            lock (_lockObject)
            {
                _sdrBackgroundColor = new Color4(red, green, blue, alpha);
                _sdrBackgroundEncoded = new Vector4(red, green, blue, alpha);
            }
        }

        /// Sets HDR background color used for clears when presenting in HDR.
        /// Values are linear in [0,1] in the swapchain target space (PQ mapped by OS).
        public void SetHdrBackgroundColor(float red, float green, float blue, float alpha = 1.0f)
        {
            lock (_lockObject)
            {
                _hdrBackgroundColor = new Color4(red, green, blue, alpha);
                _hdrBackgroundEncoded = new Vector4(red, green, blue, alpha);
            }
        }

        /// <summary>
        /// Sets the two SDR calibration values used when the active output is
        /// SDR. They are ignored by the bridge while HDR output is active.
        /// </summary>
        public void SetSdrCalibration(float referenceWhiteNits, float displayPeakNits)
        {
            lock (_lockObject)
            {
                _sdrReferenceWhiteNits = NormalizePositive(referenceWhiteNits, 100.0f);
                _sdrDisplayPeakNits = NormalizePositive(displayPeakNits, 100.0f);
            }
        }

        private static float NormalizePositive(float value, float fallback) =>
            float.IsFinite(value) && value > 0.0f ? value : fallback;
    }
}

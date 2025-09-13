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
        private ToneMappingEffect? _toneMappingEffect;

        private ID3D11SamplerState? _linearClampSampler;

        // External texture support
        private ID3D11Texture2D? _externalTexture;
        private ID3D11ShaderResourceView? _externalTextureShaderResourceView;
        private bool _externalTextureIsRec2020PQ = true;
        private bool _externalTextureOwned = false;

        private Thread? _renderThread;
        private bool _isRunning = true;
        private readonly object _lockObject = new object();

        // Tracks current content size for aspect-correct viewport
        private int _sourceWidth;
        private int _sourceHeight;

        public RendererD3D11(IntPtr windowHandle, int width, int height)
        {
            _windowHandle = windowHandle;
            _width = width;
            _height = height;
        }

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
                // --- HDR Support Detection ---
                bool hdrSupported = false;
                try
                {
                    using (var factory1 = factory.QueryInterface<IDXGIFactory1>())
                    {
                        factory1.EnumAdapters1(0, out var adapter).CheckError();
                        using (adapter)
                        {
                            adapter.EnumOutputs(0, out var output).CheckError();
                            using (output)
                            {
                                using (var output6 = output.QueryInterface<IDXGIOutput6>())
                                {
                                    var desc1 = output6.Description1;
                                    if ((desc1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020))
                                    {
                                        hdrSupported = true;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not detect HDR support: {ex.Message}");
                    hdrSupported = false; // Fallback to SDR
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
                    }
                }
                else
                {
                    Debug.WriteLine("SDR display detected or HDR support check failed. Using default SDR color space.");
                }

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

               _toneMappingEffect = new ToneMappingEffect(_device);

               // Create a default linear clamp sampler for sampling source textures
               var samplerDesc = new SamplerDescription
               {
                   Filter = Filter.MinMagMipLinear,
                   AddressU = TextureAddressMode.Clamp,
                   AddressV = TextureAddressMode.Clamp,
                   AddressW = TextureAddressMode.Clamp
               };
               _linearClampSampler = _device.CreateSamplerState(samplerDesc);
           }
            catch (Exception ex)
            {
                Debug.WriteLine("An exception occurred during D3D11 initialization:");
                Debug.WriteLine(ex.ToString());
                throw;
            }
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
                    // Clear backbuffer so letterbox/pillarbox areas are black
                    _deviceContext.OMSetRenderTargets(_renderTargetView);
                    _deviceContext.ClearRenderTargetView(_renderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
                    if (_externalTextureShaderResourceView != null)
                    {
                        // External texture path
                        if (_externalTextureIsRec2020PQ)
                        {
                            // Direct present (pass-through) to swap chain backbuffer
                            _deviceContext.OMSetRenderTargets(_renderTargetView);
                            _deviceContext.VSSetShader(_vertexShader);
                            _deviceContext.PSSetShader(_presentPixelShader);
                            _deviceContext.PSSetShaderResources(0, new[] { _externalTextureShaderResourceView });
                            if (_linearClampSampler != null)
                            {
                                _deviceContext.PSSetSamplers(0, new[] { _linearClampSampler });
                            }
                            _deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                            _deviceContext.Draw(3, 0);
                        }
                        else
                        {
                            // Fall back to tone mapping pipeline if the source is not PQ Rec.2020
                            _toneMappingEffect?.Apply(_deviceContext, _externalTextureShaderResourceView, _renderTargetView);
                        }
                    }
                    else
                    {
                        // Internal test pattern path: render to HDR RT, then present via tone mapping (legacy)
                        _deviceContext.OMSetRenderTargets(_hdrRenderTargetView);
                        _deviceContext.ClearRenderTargetView(_hdrRenderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
                        _deviceContext.VSSetShader(_vertexShader);
                        _deviceContext.PSSetShader(_pixelShader);
                        _deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        _deviceContext.Draw(3, 0);

                        _toneMappingEffect?.Apply(_deviceContext, _hdrShaderResourceView, _renderTargetView);
                    }

                    // Step F: Present the final result to the screen
                    _swapChain.Present(1, 0);
               }
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Sets an external RGBA32F texture as the source to render. Default assumes Rec.2020 PQ encoding.
        /// The renderer will sample this texture and present directly if PQ; otherwise it will route through tone mapping.
        /// </summary>
        public void SetExternalTexture(ID3D11Texture2D texture, bool isRec2020PQ = true)
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
                _externalTextureIsRec2020PQ = isRec2020PQ;
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
                _externalTextureIsRec2020PQ = isRec2020PQ;
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
            if (_device == null)
            {
                throw new InvalidOperationException("Renderer not initialized.");
            }
            if (rgba32f == null) throw new ArgumentNullException(nameof(rgba32f));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("Invalid image size");
            if (rgba32f.Length < width * height * 4) throw new ArgumentException("RGBA32F buffer too small");

            lock (_lockObject)
            {
                var desc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R32G32B32A32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                };

                int rowPitchBytes = width * sizeof(float) * 4;
                GCHandle handle = GCHandle.Alloc(rgba32f, GCHandleType.Pinned);
                try
                {
                    var subresource = new SubresourceData(handle.AddrOfPinnedObject(), rowPitchBytes, 0);
                    var newTexture = _device.CreateTexture2D(desc, new[] { subresource });

                    // Replace current external texture
                    _externalTextureShaderResourceView?.Dispose();
                    if (_externalTextureOwned)
                    {
                        _externalTexture?.Dispose();
                    }
                    _externalTexture = newTexture;
                    _externalTextureOwned = true;
                    _externalTextureIsRec2020PQ = isRec2020PQ;
                    _externalTextureShaderResourceView = _device.CreateShaderResourceView(_externalTexture);

                    _sourceWidth = width;
                    _sourceHeight = height;
                    UpdateViewport();
                }
                finally
                {
                    handle.Free();
                }
            }
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
           _toneMappingEffect?.Dispose();
           _linearClampSampler?.Dispose();
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
    }
}
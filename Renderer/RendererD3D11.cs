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
        private ID3D11ShaderResourceView? _hdrShaderResourceView;
        private ToneMappingEffect? _toneMappingEffect;

        private Thread? _renderThread;
        private bool _isRunning = true;
        private readonly object _lockObject = new object();

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

               var vertexShaderByteCode = Compiler.CompileFromFile(vsPath, "main", "vs_5_0");
               _vertexShader = _device.CreateVertexShader(vertexShaderByteCode.Span);

               var pixelShaderByteCode = Compiler.CompileFromFile(psPath, "main", "ps_5_0");
               _pixelShader = _device.CreatePixelShader(pixelShaderByteCode.Span);

               _toneMappingEffect = new ToneMappingEffect(_device);
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

           var viewport = new Viewport(0, 0, _width, _height, 0.0f, 1.0f);
            _deviceContext.RSSetViewport(viewport);
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
                   // Step A & B: Render the test pattern to the HDR float render target
                   _deviceContext.OMSetRenderTargets(_hdrRenderTargetView);
                   _deviceContext.ClearRenderTargetView(_hdrRenderTargetView, new Color4(0.0f, 0.0f, 0.0f, 1.0f));
                   _deviceContext.VSSetShader(_vertexShader);
                   _deviceContext.PSSetShader(_pixelShader);
                   _deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                   _deviceContext.Draw(3, 0);

                    // Step C & D & E: Apply tone mapping and render to the back buffer
                    _toneMappingEffect.Apply(_deviceContext, _hdrShaderResourceView, _renderTargetView);

                    // Step F: Present the final result to the screen
                    _swapChain.Present(1, 0);
               }
                Thread.Sleep(10);
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
           _toneMappingEffect?.Dispose();
           _swapChain?.Dispose();
           _deviceContext?.Dispose();
           _device?.Dispose();
        }
    }
}
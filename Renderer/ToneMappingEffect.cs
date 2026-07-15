using System;
using System.IO;
using Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BrightEyes.Renderer
{
    public class ToneMappingEffect : IDisposable
    {
        private ID3D11VertexShader? _vertexShader;
        private ID3D11PixelShader? _pixelShader;
        private ID3D11SamplerState? _pointClampSampler;
        private ID3D11Buffer? _scaleConstantBuffer;
        private int _constantWidth = -1;
        private int _constantHeight = -1;
        private bool _constantInputIsSdr;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ScaleConstants
        {
            public float SourceWidth;
            public float SourceHeight;
            public float SourceTexelWidth;
            public float SourceTexelHeight;
            public float InputIsSdr;
            public float Padding0;
            public float Padding1;
            public float Padding2;
        }

        public ToneMappingEffect(ID3D11Device device)
        {
            // 在这里加载和创建资源
            string baseDirectory = AppContext.BaseDirectory;
            string vsPath = Path.Combine(baseDirectory, "Renderer/Shaders/FullScreenVS.hlsl");
            string psPath = Path.Combine(baseDirectory, "Renderer/ToneMappingPS.hlsl");

            var vertexShaderByteCode = Compiler.CompileFromFile(vsPath, "main", "vs_5_0");
            _vertexShader = device.CreateVertexShader(vertexShaderByteCode.Span);

            var pixelShaderByteCode = Compiler.CompileFromFile(psPath, "main", "ps_5_0");
            _pixelShader = device.CreatePixelShader(pixelShaderByteCode.Span);

            var pointSamplerDesc = new SamplerDescription
            {
                Filter = Vortice.Direct3D11.Filter.Anisotropic,
                AddressU = Vortice.Direct3D11.TextureAddressMode.Clamp,
                AddressV = Vortice.Direct3D11.TextureAddressMode.Clamp,
                AddressW = Vortice.Direct3D11.TextureAddressMode.Clamp,
                MaxAnisotropy = 8
            };
            _pointClampSampler = device.CreateSamplerState(pointSamplerDesc);

            _scaleConstantBuffer = device.CreateBuffer(new Vortice.Direct3D11.BufferDescription
            {
                ByteWidth = System.Runtime.InteropServices.Marshal.SizeOf<ScaleConstants>(),
                Usage = Vortice.Direct3D11.ResourceUsage.Dynamic,
                BindFlags = Vortice.Direct3D11.BindFlags.ConstantBuffer,
                CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Write,
                MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None,
                StructureByteStride = 0
            });
        }

        public void Apply(ID3D11DeviceContext context, ID3D11ShaderResourceView source, ID3D11RenderTargetView destination, int sourceWidth, int sourceHeight, bool inputIsSdr = false)
        {
            // 在这里实现渲染逻辑
            context.OMSetRenderTargets(destination);
            context.VSSetShader(_vertexShader);
            context.PSSetShader(_pixelShader);
            context.PSSetShaderResources(0, new[] { source });
            if (_scaleConstantBuffer != null && (_constantWidth != sourceWidth || _constantHeight != sourceHeight || _constantInputIsSdr != inputIsSdr))
            {
                var mapped = context.Map(_scaleConstantBuffer, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var constants = new ScaleConstants
                    {
                        SourceWidth = sourceWidth,
                        SourceHeight = sourceHeight,
                        SourceTexelWidth = sourceWidth > 0 ? 1.0f / sourceWidth : 0.0f,
                        SourceTexelHeight = sourceHeight > 0 ? 1.0f / sourceHeight : 0.0f,
                        InputIsSdr = inputIsSdr ? 1.0f : 0.0f
                    };
                    System.Runtime.InteropServices.Marshal.StructureToPtr(constants, mapped.DataPointer, false);
                }
                finally
                {
                    context.Unmap(_scaleConstantBuffer, 0);
                }
                _constantWidth = sourceWidth;
                _constantHeight = sourceHeight;
                _constantInputIsSdr = inputIsSdr;
            }
            if (_scaleConstantBuffer != null)
            {
                context.PSSetConstantBuffers(0, new[] { _scaleConstantBuffer });
            }
            if (_pointClampSampler != null)
            {
                context.PSSetSamplers(0, new[] { _pointClampSampler });
            }
            context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            context.Draw(3, 0);
        }

        public void Dispose()
        {
            _vertexShader?.Dispose();
            _pixelShader?.Dispose();
            _pointClampSampler?.Dispose();
            _scaleConstantBuffer?.Dispose();
        }
    }
}

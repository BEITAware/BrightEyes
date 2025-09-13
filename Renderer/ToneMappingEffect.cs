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
        }

        public void Apply(ID3D11DeviceContext context, ID3D11ShaderResourceView source, ID3D11RenderTargetView destination)
        {
            // 在这里实现渲染逻辑
            context.OMSetRenderTargets(destination);
            context.VSSetShader(_vertexShader);
            context.PSSetShader(_pixelShader);
            context.PSSetShaderResources(0, new[] { source });
            context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            context.Draw(3, 0);
        }

        public void Dispose()
        {
            _vertexShader?.Dispose();
            _pixelShader?.Dispose();
        }
    }
}
using System.Collections.Concurrent;
using Vortice.D3DCompiler;

namespace BrightEyes.Renderer;

internal static class ShaderBytecodeCache
{
    private static readonly ConcurrentDictionary<ShaderKey, Lazy<byte[]>> Cache = new();

    public static byte[] Get(string path, string entryPoint, string profile)
    {
        var key = new ShaderKey(System.IO.Path.GetFullPath(path), entryPoint, profile);
        return Cache.GetOrAdd(
            key,
            static shader => new Lazy<byte[]>(
                () => Compiler.CompileFromFile(shader.Path, shader.EntryPoint, shader.Profile).Span.ToArray(),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private readonly record struct ShaderKey(string Path, string EntryPoint, string Profile);
}

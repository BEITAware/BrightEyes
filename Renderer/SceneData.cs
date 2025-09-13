using System.Runtime.InteropServices;
using System.Numerics;

namespace BrightEyes.Renderer
{
    // Aligned to 256 bytes for constant buffer alignment requirements.
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 256)]
    public struct SceneConstants
    {
        public Vector2 Pan;
        public float Zoom;
    }
}
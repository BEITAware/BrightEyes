using System.ComponentModel;

namespace BrightEyes.Processing.BuiltIn;

public static class BuiltInProcessIds
{
    public const string ImageSource = "brighteyes.image-source";
    public const string Decoder = "brighteyes.decoder";
    // Kept only for sidecar migration. This process is no longer registered.
    public const string RenderMapper = "brighteyes.render-mapper";
    public const string ViewRendering = "brighteyes.view";
    public const string Waveform = "brighteyes.waveform";
    public const string Histogram = "brighteyes.histogram";
    public const string Vectorscope = "brighteyes.vectorscope";
    public const string StrictHistogram = "brighteyes.strict-histogram";
    public const string GaussianBlur = "brighteyes.gaussian-blur";
}

public enum DecoderBackend
{
    PhotoSauce,
    StarwardUltraHdr
}

public enum DecoderAlphaMode
{
    PreserveStraight,
    ForceOpaque,
    Unpremultiply
}

public enum ToneMappingMode
{
    ColorCorrectV1,
    Disabled,
    BrightEyesV0Compatibility
}

public enum GamutMappingMode
{
    PerceptualCompress,
    ClipAtTerminal,
    Disabled
}

public enum BackgroundMode
{
    ApplicationBackground,
    TransparentBlack,
    White
}

public enum ViewInterpolationMode
{
    [Description("BrightEyes Bilinear")]
    BrightEyesBilinear = 0,
    Bilinear = 1,
    Bicubic = 2,
    NearestNeighbor = 3,
    Area = 4
}

public enum WaveformDisplayMode
{
    Luminance,
    ParadeRgb,
    OverlayRgb
}

public enum HistogramChannel
{
    Luminance,
    Red,
    Green,
    Blue,
    Alpha
}

namespace BrightEyes.Processing.BuiltIn;

public static class BuiltInProcessIds
{
    public const string ImageSource = "brighteyes.image-source";
    public const string Decoder = "brighteyes.decoder";
    public const string RenderMapper = "brighteyes.render-mapper";
    public const string View = "brighteyes.view";
    public const string Oscilloscope = "brighteyes.oscilloscope";
}

public enum DecoderBackend
{
    PhotoSauce,
    StarwardUltraHdr
}

public enum DecoderPrecision
{
    Rgba32Float
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

public enum OscilloscopeKind
{
    Histogram,
    Waveform,
    Vectorscope
}

public enum OscilloscopeChannel
{
    Rgb,
    Luminance,
    Red,
    Green,
    Blue,
    Alpha
}

public enum SignalUnit
{
    ScRgb,
    Nits,
    LinearValue
}

namespace Katana.Core;

public sealed record UsbAudioLevels(bool Available, int? ProcessedPercent, int? DryPercent)
{
    public const int Address = 0x51;
    public static UsbAudioLevels Unavailable { get; } = new(false, null, null);
}

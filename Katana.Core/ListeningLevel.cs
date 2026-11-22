namespace Katana.Core;

public sealed record ListeningLevel(bool Available, bool Enabled, bool OutputPosition, int? Decibels)
{
    public const int Address = (4 << 7) | 0x32;
    public static ListeningLevel Unavailable { get; } = new(false, false, false, null);
}

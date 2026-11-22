namespace Katana.Core;

public sealed class SysExFramer
{
    private readonly List<byte> buffer = [];

    public byte[]? Feed(byte value)
    {
        // MIDI real-time bytes can legally occur inside SysEx messages.
        if (value >= 0xF8)
        {
            return null;
        }
        if (value == 0xF0)
        {
            buffer.Clear();
            buffer.Add(value);
            return null;
        }
        if (buffer.Count == 0)
        {
            return null;
        }
        if (value == 0xF7)
        {
            buffer.Add(value);
            var frame = buffer.ToArray();
            buffer.Clear();
            return frame;
        }
        if (value >= 0x80 || buffer.Count >= 4096)
        {
            buffer.Clear();
            return null;
        }
        buffer.Add(value);
        return null;
    }
}

namespace Katana.Core;

public static class KatanaProtocol
{
    public static byte[] IdentityRequest() => [0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7];
    public static byte[] GainRequest(byte deviceId) => Message(deviceId, 0x11, [0, 0, 0, 1]);
    public static byte[] GainWrite(byte deviceId, byte value)
    {
        if (value > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return Message(deviceId, 0x12, [value]);
    }

    public static bool IsIdentity(byte[] frame) => frame.Length == 15
        && frame.AsSpan(0, 2).SequenceEqual(new byte[] { 0xF0, 0x7E })
        && frame.AsSpan(3, 7).SequenceEqual(new byte[] { 0x06, 0x02, 0x41, 0x33, 0x03, 0, 0 })
        && frame[2] < 0x80 && frame.AsSpan(10, 4).IndexOfAnyInRange((byte)0x80, byte.MaxValue) < 0
        && frame[^1] == 0xF7;

    public static bool IsGainResponse(byte[] frame, byte deviceId) => frame.Length == 15
        && frame.AsSpan(0, 12).SequenceEqual(new byte[] { 0xF0, 0x41, deviceId, 0, 0, 0, 0x33, 0x12, 0x60, 0, 0, 0x52 })
        && frame[^1] == 0xF7;

    public static byte ParseGain(byte[] frame, byte deviceId)
    {
        if (!IsGainResponse(frame, deviceId) || frame[12] > 100 || frame[13] > 127
            || Checksum(frame.AsSpan(8, 5)) != frame[13])
        {
            throw new InvalidDataException("Invalid Katana gain response (address, value or checksum).");
        }
        return frame[12];
    }

    public static byte Checksum(ReadOnlySpan<byte> data)
    {
        var sum = 0;
        foreach (var value in data)
        {
            sum += value;
        }
        return (byte)((-sum) & 0x7F);
    }

    public static byte[] ReadRequest(byte deviceId, int address, int length)
    {
        if (length is < 1 or > PatchProfile.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        return DataMessage(deviceId, 0x11, address, AddressBytes(length));
    }

    public static byte[] WriteRequest(byte deviceId, int address, byte[] data) => DataMessage(deviceId, 0x12, address, data);

    public static byte[] AddressBytes(int address)
    {
        if (address is < 0 or > 0x0FFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }
        return [(byte)((address >> 21) & 127), (byte)((address >> 14) & 127), (byte)((address >> 7) & 127), (byte)(address & 127)];
    }

    public static bool IsDataResponse(byte[] frame, byte deviceId) => frame.Length >= 15
        && frame.AsSpan(0, 8).SequenceEqual(new byte[] { 0xF0, 0x41, deviceId, 0, 0, 0, 0x33, 0x12 })
        && frame[^1] == 0xF7;

    public static (int Address, byte[] Data) ParseData(byte[] frame, byte deviceId)
    {
        if (!IsDataResponse(frame, deviceId) || frame.AsSpan(8, frame.Length - 9).IndexOfAnyInRange((byte)128, byte.MaxValue) >= 0
            || Checksum(frame.AsSpan(8, frame.Length - 10)) != frame[^2])
        {
            throw new InvalidDataException("Invalid Katana data response.");
        }
        var address = (frame[8] << 21) | (frame[9] << 14) | (frame[10] << 7) | frame[11];
        return (address, frame[12..^2]);
    }

    private static byte[] DataMessage(byte deviceId, byte operation, int address, byte[] data)
    {
        if (deviceId > 127 || data.Length is < 1 or > 241 || data.Any(value => value > 127))
        {
            throw new ArgumentException("Invalid MIDI data or device ID.");
        }
        var frame = new byte[14 + data.Length];
        new byte[] { 0xF0, 0x41, deviceId, 0, 0, 0, 0x33, operation }.CopyTo(frame, 0);
        AddressBytes(address).CopyTo(frame, 8);
        data.CopyTo(frame, 12);
        frame[^2] = Checksum(frame.AsSpan(8, 4 + data.Length));
        frame[^1] = 0xF7;
        return frame;
    }

    private static byte[] Message(byte deviceId, byte operation, byte[] data)
    {
        if (deviceId > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceId));
        }
        var frame = new byte[14 + data.Length];
        new byte[] { 0xF0, 0x41, deviceId, 0, 0, 0, 0x33, operation, 0x60, 0, 0, 0x52 }.CopyTo(frame, 0);
        data.CopyTo(frame, 12);
        frame[^2] = Checksum(frame.AsSpan(8, 4 + data.Length));
        frame[^1] = 0xF7;
        return frame;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Katana.Core;

public sealed class AlsaMidiTransport : IMidiTransport
{
    private const int Again = -11; // Linux EAGAIN
    private const int Interrupted = -4; // Linux EINTR
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(3);
    private IntPtr input;
    private IntPtr output;
    private bool disposed;

    public AlsaMidiTransport(string port)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("This transport requires Linux ALSA.");
        }
        var result = Native.Open(out input, out output, port, 2); // SND_RAWMIDI_NONBLOCK (1 is APPEND, output-only)
        if (result < 0)
        {
            Dispose();
            Check(result, "open MIDI port");
        }
    }

    public void Send(byte[] message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var clock = Stopwatch.StartNew();
        var remaining = message;
        while (remaining.Length > 0)
        {
            CheckDeadline(clock, "sending MIDI");
            var result = (long)Native.Write(output, remaining, (nuint)remaining.Length);
            if (result > 0)
            {
                remaining = remaining[(int)result..];
            }
            else if (result == Again || result == Interrupted || result == 0)
            {
                Wait(clock, "sending MIDI");
            }
            else
            {
                Check(result, "write MIDI");
            }
        }
    }

    public byte[] Request(byte[] message, Func<byte[], bool> matches)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Discard stale replies before sending a new request, never after sending it.
        var discard = new byte[256];
        var discardClock = Stopwatch.StartNew();
        while (true)
        {
            var stale = (long)Native.Read(input, discard, (nuint)discard.Length);
            if (stale == Again || stale == 0)
            {
                break;
            }
            if (stale < 0 && stale != Interrupted)
            {
                Check(stale, "clear stale MIDI input");
            }
            Wait(discardClock, "clearing stale MIDI input");
        }
        Send(message);
        var clock = Stopwatch.StartNew();
        var framer = new SysExFramer();
        var buffer = new byte[256];
        while (true)
        {
            var result = (long)Native.Read(input, buffer, (nuint)buffer.Length);
            if (result > 0)
            {
                for (var index = 0; index < result; index++)
                {
                    var frame = framer.Feed(buffer[index]);
                    if (frame is not null && matches(frame))
                    {
                        return frame;
                    }
                }
            }
            else if (result != Again && result != Interrupted && result != 0)
            {
                Check(result, "read MIDI");
            }
            Wait(clock, "waiting for the amp response");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (input != IntPtr.Zero)
        {
            Native.Close(input);
            input = IntPtr.Zero;
        }
        if (output != IntPtr.Zero)
        {
            Native.Close(output);
            output = IntPtr.Zero;
        }
    }

    private void Wait(Stopwatch clock, string operation)
    {
        CheckDeadline(clock, operation);
        Thread.Sleep(2);
    }

    private void CheckDeadline(Stopwatch clock, string operation)
    {
        if (clock.Elapsed >= timeout)
        {
            throw new TimeoutException($"Timed out {operation}.");
        }
    }

    private static void Check(long result, string operation)
    {
        if (result < 0)
        {
            var description = Marshal.PtrToStringUTF8(Native.Error((int)result));
            throw new IOException($"Could not {operation}: {description} ({result}).");
        }
    }

    private static class Native
    {
        private const string Library = "libasound.so.2";
        [DllImport(Library, EntryPoint = "snd_rawmidi_open", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Open(out IntPtr input, out IntPtr output, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int mode);
        [DllImport(Library, EntryPoint = "snd_rawmidi_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Close(IntPtr handle);
        [DllImport(Library, EntryPoint = "snd_rawmidi_write", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Write(IntPtr handle, byte[] buffer, nuint size);
        [DllImport(Library, EntryPoint = "snd_rawmidi_read", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Read(IntPtr handle, [Out] byte[] buffer, nuint size);
        [DllImport(Library, EntryPoint = "snd_strerror", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Error(int error);
    }
}

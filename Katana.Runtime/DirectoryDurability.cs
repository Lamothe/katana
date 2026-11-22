using System.Runtime.InteropServices;

namespace Katana.Web;

// On Linux, syncing file contents is not sufficient to persist a rename/delete.
internal static class DirectoryDurability
{
    public static void Create(string directory)
    {
        var path = Path.GetFullPath(directory);
        var missing = new Stack<string>();
        for (var current = path; !Directory.Exists(current);)
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new IOException("Could not locate an existing preset directory ancestor.");
        }
        while (missing.TryPop(out var child))
        {
            Directory.CreateDirectory(child);
            Sync(Path.GetDirectoryName(child)!);
        }
    }

    public static void Sync(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Preset storage currently requires Linux directory synchronisation.");
        }
        var descriptor = Native.Open(directory, 0x10000 | 0x80000); // O_DIRECTORY | O_CLOEXEC, O_RDONLY
        if (descriptor < 0)
        {
            throw new IOException($"Could not open preset directory for synchronisation ({Marshal.GetLastPInvokeError()}).");
        }
        try
        {
            while (Native.Sync(descriptor) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 4) // EINTR
                {
                    throw new IOException($"Preset directory synchronisation failed ({error}); persistence is unverified.");
                }
            }
        }
        finally
        {
            Native.Close(descriptor);
        }
    }

    private static class Native
    {
        [DllImport("libc.so.6", EntryPoint = "open", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc.so.6", EntryPoint = "fsync", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        internal static extern int Sync(int descriptor);
        [DllImport("libc.so.6", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Close(int descriptor);
    }
}

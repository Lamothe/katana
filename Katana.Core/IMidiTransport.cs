namespace Katana.Core;

// Single-owner, synchronous transport: callers must not use it concurrently.
public interface IMidiTransport : IDisposable
{
    void Send(byte[] message);
    byte[] Request(byte[] message, Func<byte[], bool> matches);
}

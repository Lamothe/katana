using Katana.Core;

if (args.Length is < 1 or > 2 || args[0] is not ("identity" or "gain" or "test-gain"))
{
    Console.Error.WriteLine("Usage: Katana.Cli <identity|gain|test-gain> [ALSA port]");
    return 2;
}
try
{
    using var transport = new AlsaMidiTransport(args.Length == 2 ? args[1] : "hw:KATANA,0,0");
    var amp = new KatanaDevice(transport);
    var identity = amp.Identify();
    Console.WriteLine($"Katana identity: {Convert.ToHexStringLower(identity)}");
    switch (args[0])
    {
        case "gain":
            Console.WriteLine($"Current gain: {amp.ReadGain()}");
            break;
        case "test-gain":
            amp.TestGain(Console.WriteLine);
            break;
    }
    return 0;
}
catch (Exception error) when (error is IOException or TimeoutException or PlatformNotSupportedException or DllNotFoundException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

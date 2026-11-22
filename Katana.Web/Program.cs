using System.Net;
using System.Security.Cryptography;
using System.Text;
using Katana.Core;
using Katana.Web;

var builder = WebApplication.CreateBuilder(args);
var desktopToken = Environment.GetEnvironmentVariable("KATANA_DESKTOP_TOKEN");
if (desktopToken is { Length: < 32 })
{
    throw new InvalidOperationException("Katana desktop requires a private launch token.");
}
var desktopTokenBytes = desktopToken is null ? null : Encoding.UTF8.GetBytes(desktopToken);
var port = desktopTokenBytes is null ? builder.Configuration.GetValue("Katana:Port", 5025) : 0;
if (desktopTokenBytes is null && port is < 1 or > 65535)
{
    throw new ArgumentOutOfRangeException(nameof(port), "Katana port must be between 1 and 65535.");
}
builder.WebHost.PreferHostingUrls(false);
if (desktopTokenBytes is null)
{
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
}
builder.WebHost.ConfigureKestrel(server =>
{
    // Discard configuration-defined listeners: Katana only serves loopback traffic.
    server.Configure(new ConfigurationBuilder().Build());
    server.Listen(IPAddress.Loopback, port);
});
builder.Services.AddSingleton(new PresetStore(builder.Configuration["Katana:PresetDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Katana", "presets")));
builder.Services.AddSingleton(provider => new DeviceSession(builder.Configuration["Katana:MidiPort"] ?? "hw:KATANA,0,0", provider.GetRequiredService<PresetStore>()));
var app = builder.Build();

app.Use(async (context, next) =>
{
    // Local hardware controller: reject hostile Host headers/DNS rebinding and cross-origin mutations.
    var remote = context.Connection.RemoteIpAddress;
    if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)
        || context.Request.Host.Host is not ("127.0.0.1" or "localhost")
        || (desktopTokenBytes is not null && !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(context.Request.Headers["X-Katana-Desktop-Token"].ToString()), desktopTokenBytes)))
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Method is not ("GET" or "HEAD"))
        {
            var origin = context.Request.Headers.Origin.ToString();
            var expected = $"{context.Request.Scheme}://{context.Request.Host}";
            if ((origin.Length > 0 && origin != expected) || !context.Request.HasJsonContentType()
                || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
            {
                await Results.Problem(statusCode: 403, title: "Request blocked", detail: "Use the local editor with same-origin JSON requests.").ExecuteAsync(context);
                return;
            }
        }
    }
    try
    {
        await next(context);
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException or InvalidDataException or TimeoutException or System.Text.Json.JsonException or DllNotFoundException)
    {
        app.Logger.LogWarning(error, "Katana request {Method} {Path} failed", context.Request.Method, context.Request.Path);
        var status = error switch
        {
            KeyNotFoundException => 404,
            ArgumentException => 400,
            InvalidOperationException => 409,
            _ => 503
        };
        await Results.Problem(statusCode: status, title: status == 503 ? "Operation could not be verified" : "Request could not be completed", detail: error.Message).ExecuteAsync(context);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/parameters", () => Parameters.All);
app.MapPost("/api/device/connect", (DeviceSession device, CancellationToken cancellationToken) => device.Connect(cancellationToken));
app.MapGet("/api/device/connection", (DeviceSession device, CancellationToken cancellationToken) => device.CheckConnection(cancellationToken));
app.MapGet("/api/device/monitor", (DeviceSession device, CancellationToken cancellationToken) => device.Monitor(cancellationToken));
app.MapGet("/api/device", (DeviceSession device, CancellationToken cancellationToken) => device.Read(cancellationToken));
app.MapPut("/api/device/parameters/{id}", (string id, ParameterChange change, DeviceSession device, CancellationToken cancellationToken) => device.Set(id, change.Value ?? throw new ArgumentException("A parameter value is required."), cancellationToken));
app.MapPut("/api/device/listening-level", (ListeningLevelChange change, DeviceSession device, CancellationToken cancellationToken) => device.SetListeningLevel(change.Decibels ?? throw new ArgumentException("A listening level is required."), change.ConfigureOutputEq, cancellationToken));
app.MapPut("/api/device/usb-audio/{output}", (string output, UsbAudioLevelChange change, DeviceSession device, CancellationToken cancellationToken) => device.SetUsbAudioLevel(output, change.Percent ?? throw new ArgumentException("A USB recording level is required."), cancellationToken));
app.MapPost("/api/device/slots/{slot:int}/recall", (int slot, DeviceSession device, CancellationToken cancellationToken) => device.Recall(slot, cancellationToken));
app.MapPost("/api/device/slots/{slot:int}/commit", (int slot, DeviceSession device, CancellationToken cancellationToken) => device.Commit(slot, cancellationToken));
app.MapPost("/api/presets/{id:guid}/slots/{slot:int}/apply", (Guid id, int slot, DeviceSession device, CancellationToken cancellationToken) => device.Apply(id, slot, cancellationToken));
app.MapPost("/api/device/commit", (DeviceSession device, CancellationToken cancellationToken) => device.Commit(cancellationToken));
app.MapGet("/api/presets", (PresetStore presets) => presets.List());
app.MapPost("/api/presets", (PresetName request, DeviceSession device, CancellationToken cancellationToken) => device.Capture(request.Name, cancellationToken));
app.MapPut("/api/presets/{id:guid}", (Guid id, PresetName request, PresetStore presets) => presets.Rename(id, request.Name));
app.MapDelete("/api/presets/{id:guid}", (Guid id, PresetStore presets) => { presets.Delete(id); return Results.NoContent(); });
app.MapPost("/api/presets/{id:guid}/apply", (Guid id, DeviceSession device, CancellationToken cancellationToken) => device.Apply(id, cancellationToken));
app.MapFallbackToFile("index.html");
if (desktopTokenBytes is null)
{
    await app.RunAsync();
}
else
{
    try
    {
        await app.StartAsync();
        Console.WriteLine($"KATANA_DESKTOP_READY {app.Urls.Single()}");
        // Electron holds stdin open. EOF also shuts down the host after a desktop crash.
        _ = Task.Run(async () =>
        {
            try { await Console.In.ReadLineAsync(); }
            finally { app.Lifetime.StopApplication(); }
        });
        await app.WaitForShutdownAsync();
    }
    finally
    {
        await app.DisposeAsync();
    }
}

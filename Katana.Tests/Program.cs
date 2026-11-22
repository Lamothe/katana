using Katana.Core;
using Katana.Web;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception($"FAIL: {name}");
    }
    count++;
}
void Reject(Action action, string name)
{
    try
    {
        action();
    }
    catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException)
    {
        count++;
        return;
    }
    throw new Exception($"FAIL: {name}");
}
Katana.Tests.EffectMetadataChecks.Run(Check, Reject);
// Cancellation while queued must be checked before any transport or hardware work.
using (var cancelled = new CancellationTokenSource())
using (var session = new DeviceSession("fake", new PresetStore(Path.Combine(Path.GetTempPath(), "katana-cancelled")),
    _ => throw new Exception("Cancelled connect opened a transport")))
{
    cancelled.Cancel();
    foreach (var operation in new Action[]
    {
        () => session.Connect(cancelled.Token),
        () => session.Set("gain", 10, cancelled.Token),
        () => session.SetListeningLevel(-10, false, cancelled.Token),
        () => session.SetUsbAudioLevel("processed", 100, cancelled.Token),
    })
    {
        try
        {
            operation();
            throw new Exception("Cancelled operation reached hardware");
        }
        catch (OperationCanceledException)
        {
            Check(true, "cancelled operation does not touch hardware");
        }
    }
}

var identity = Convert.FromHexString("F07E000602413303000004010000F7");
Check(KatanaProtocol.IsIdentity(identity), "observed identity");
Check(!KatanaProtocol.IsIdentity([]), "truncated identity");
var other = (byte[])identity.Clone();
other[6] = 0x34;
Check(!KatanaProtocol.IsIdentity(other), "other model rejected");
Check(KatanaProtocol.GainRequest(0).SequenceEqual(Convert.FromHexString("F04100000000331160000052000000014DF7")), "known read request");
Check(KatanaProtocol.GainWrite(0, 20).SequenceEqual(Convert.FromHexString("F04100000000331260000052143AF7")), "known write request");
var response = Convert.FromHexString("F04100000000331260000052143AF7");
Check(KatanaProtocol.ParseGain(response, 0) == 20, "observed gain response");
Reject(() => KatanaProtocol.ParseGain([], 0), "short response");
Reject(() => KatanaProtocol.ParseGain(response, 1), "wrong device");
response[13] ^= 1;
Reject(() => KatanaProtocol.ParseGain(response, 0), "bad checksum");
Reject(() => KatanaProtocol.GainWrite(0, 101), "gain range");
Reject(() => KatanaProtocol.GainRequest(128), "device range");
var framer = new SysExFramer();
byte[]? frame = null;
foreach (var value in new byte[] { 0x90, 0x01, 0xF0, 0x01, 0xF8, 0x02, 0xF7 })
{
    frame = framer.Feed(value) ?? frame;
}
Check(frame is not null && frame.SequenceEqual(new byte[] { 0xF0, 1, 2, 0xF7 }), "framing with noise and real-time byte");
framer.Feed(0xF0);
framer.Feed(1);
framer.Feed(0x90);
Check(framer.Feed(0xF7) is null, "invalid status aborts frame");
foreach (var original in new byte[] { 0, 20, 100 })
{
    using var fake = new FakeTransport(original);
    var amp = new KatanaDevice(fake);
    Reject(() => amp.ReadGain(), "identity required");
    amp.Identify();
    amp.TestGain(_ => { });
    Check(fake.Gain == original && fake.Writes.Count == 2, "test restores gain");
}
using (var fake = new FakeTransport(20) { FailAfterWrite = true })
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    var failed = false;
    try
    {
        amp.TestGain(_ => { });
    }
    catch (TimeoutException)
    {
        failed = true;
    }
    Check(failed && fake.Gain == 20 && fake.Writes.Count == 2, "restores after read timeout");
}
foreach (var sendFailure in new[] { false, true })
{
    using var fake = new FakeTransport(20) { FailRestorationSend = sendFailure, FailRestorationRead = !sendFailure, FailAfterWrite = true };
    var amp = new KatanaDevice(fake);
    amp.Identify();
    var labelled = false;
    try
    {
        amp.TestGain(_ => { });
    }
    catch (IOException error)
    {
        labelled = error.Message.Contains("gain may remain changed") && error.InnerException is AggregateException;
    }
    Check(labelled, "restoration failure labelled and both failures preserved");
}
using (var fake = new FakeTransport(20) { FailRestorationRead = true })
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    var labelled = false;
    try
    {
        amp.TestGain(_ => { });
    }
    catch (IOException error)
    {
        labelled = error.Message.Contains("gain may remain changed") && error.InnerException is TimeoutException;
    }
    Check(labelled, "standalone restoration timeout labelled");
}
Check(KatanaProtocol.ReadRequest(0, PatchProfile.BufferAddress, PatchProfile.Length)
    .SequenceEqual(Convert.FromHexString("F041000000003311600000000000121777F7")), "known full profile request");
Check(KatanaProtocol.WriteRequest(0, (0x7F << 21) | (1 << 7) | 4, [0, 1])
    .SequenceEqual(Convert.FromHexString("F0410000000033127F00010400017BF7")), "known A1 save request");
Reject(() => Parameters.Find("gain").Encode(101), "parameter range");
Reject(() => Parameters.Find("ampType").Encode(127), "unsupported amp type rejected");
Check(Parameters.Find("delayTime").Encode(2000).SequenceEqual(new byte[] { 15, 80 }), "two-byte parameter encoding");
// Metadata and edit checks use only in-memory protocol frames; no MIDI port is opened.
Check(Parameters.All.Select(parameter => parameter.Id).Distinct().Count() == Parameters.All.Count, "unique parameter IDs");
Check(Parameters.All.All(parameter => parameter.Offset >= 0 && parameter.Offset + parameter.Size <= PatchProfile.Length), "metadata within snapshot");
var gainMetadata = Parameters.Find("gain");
Check(gainMetadata.DisplayOffset == 0 && gainMetadata.DisplayScale == 1 && gainMetadata.DependsOn is null && gainMetadata.DependsOnValue is null,
    "optional display and condition defaults preserve existing parameters");
foreach (var id in new[] { "boostBottom", "boostTone", "modCompressorTone", "fxCompressorTone" })
{
    var parameter = Parameters.Find(id);
    Check(parameter.Min == 0 && parameter.Max == 100 && parameter.DisplayOffset == -50 && parameter.DisplayScale == 1, id + " signed display metadata");
    Check(parameter.Encode(50).SequenceEqual(new byte[] { 50 }), id + " keeps raw encoding");
}
var reverbTime = Parameters.Find("reverbTime");
Check((reverbTime.Min + reverbTime.DisplayOffset) * reverbTime.DisplayScale == 0.1 &&
    (reverbTime.Max + reverbTime.DisplayOffset) * reverbTime.DisplayScale == 10 && reverbTime.Unit == "s", "reverb time display 0.1 to 10 seconds");
var preDelay = Parameters.Find("reverbPreDelay");
Check(preDelay.Offset == 787 && preDelay.Size == 2 && preDelay.Encode(500).SequenceEqual(new byte[] { 3, 116 }), "pre-delay base-128 encoding");
Reject(() => preDelay.Encode(501), "pre-delay range");
var rawPatch = new byte[PatchProfile.Length];
preDelay.Encode(500).CopyTo(rawPatch, preDelay.Offset);
rawPatch[reverbTime.Offset] = 99;
Check(preDelay.Decode(rawPatch) == 500 && reverbTime.Decode(rawPatch) == 99, "decode returns raw not display values");
foreach (var (id, offset, values) in new (string, int, int[])[] {
    ("boostType", 49, [0, 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]),
    ("modType", 193, [0, 1, 2, 3, 4, 6, 7, 9, 10, 12, 14, 15, 16, 18, 19, 20, 21, 22, 23, 25, 26, 27, 28, 29, 31]),
    ("fxType", 461, [0, 1, 2, 3, 4, 6, 7, 9, 10, 12, 14, 15, 16, 18, 19, 20, 21, 22, 23, 25, 26, 27, 28, 29, 31]),
    ("delayType", 737, [0, 6, 7, 8, 9]), ("reverbType", 785, [1, 3, 4, 5, 6]) })
{
    var parameter = Parameters.Find(id);
    Check(parameter.Offset == offset && parameter.Options is not null && parameter.Options.Select(option => option.Value).SequenceEqual(values), id + " documented sparse options");
    rawPatch[offset] = 127;
    Check(parameter.Decode(rawPatch) == 127, id + " unknown existing type preserved on decode");
    Reject(() => parameter.Encode(127), id + " unknown type cannot be newly selected");
}
foreach (var parameter in Parameters.All.Where(parameter => parameter.DependsOn == "modType"))
{
    var fx = Parameters.Find("fx" + parameter.Id[3..]);
    Check(fx.Offset == parameter.Offset + (parameter.DependsOnValue == 31 ? 15 : 268) && fx.DependsOn == "fxType" && fx.DependsOnValue == parameter.DependsOnValue &&
        fx.Min == parameter.Min && fx.Max == parameter.Max && fx.DisplayOffset == parameter.DisplayOffset, parameter.Id + " mirrored FX definition");
}
foreach (var prefix in new[] { "mod", "fx" })
{
    var eq = Parameters.All.Where(parameter => parameter.DependsOn == prefix + "Type" && parameter.DependsOnValue == 6).ToArray();
    Check(eq.Length == 11 && eq.All(parameter => parameter.Min == 0 && parameter.Max == 40 && parameter.DisplayOffset == -20 && parameter.Unit == "dB"), prefix + " ten EQ bands and level");
}
foreach (var parameter in Parameters.All.Where(parameter => parameter.DependsOn is not null))
{
    using var fake = new PatchTransport();
    var amp = new KatanaDevice(fake);
    amp.Identify();
    var selector = Parameters.Find(parameter.DependsOn!);
    fake.Patch[selector.Offset] = 127;
    var before = (byte[])fake.Patch.Clone();
    Reject(() => amp.SetParameter(parameter.Id, parameter.Min), parameter.Id + " inactive write rejected");
    Check(fake.WriteAddresses.Count == 0 && fake.Patch.SequenceEqual(before), parameter.Id + " inactive edit has no writes");
    Check(fake.ReadAddresses.SequenceEqual(new[] { PatchProfile.BufferAddress + selector.Offset }), parameter.Id + " fresh selector read only");
    fake.Patch[selector.Offset] = checked((byte)parameter.DependsOnValue!.Value);
    before = (byte[])fake.Patch.Clone();
    amp.SetParameter(parameter.Id, parameter.Max);
    parameter.Encode(parameter.Max).CopyTo(before, parameter.Offset);
    Check(fake.Patch.SequenceEqual(before) && fake.WriteAddresses.SequenceEqual(new[] { PatchProfile.BufferAddress + parameter.Offset }) &&
        fake.WriteSizes.SequenceEqual(new[] { parameter.Size }), parameter.Id + " active edit isolates selected parameter");
    fake.Patch[selector.Offset] = 127;
    before = (byte[])fake.Patch.Clone();
    Reject(() => amp.SetParameter(parameter.Id, parameter.Min), parameter.Id + " changed selector revalidated");
    Check(fake.WriteAddresses.Count == 1 && fake.Patch.SequenceEqual(before), parameter.Id + " stale algorithm never written");
}
using (var fake = new PatchTransport())
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    foreach (var id in new[] { "boostType", "modType", "fxType", "delayType", "reverbType" })
    {
        fake.Patch[Parameters.Find(id).Offset] = 127;
    }
    var saved = amp.ReadPatch();
    amp.SetParameter("gain", 42);
    saved[Parameters.Find("gain").Offset] = 42;
    amp.ApplyPatch(saved);
    Check(fake.Patch.SequenceEqual(saved), "unrelated edit and snapshot apply preserve unknown effect types and all raw bytes");
    amp.SetParameter("reverbPreDelay", 500);
    Check(fake.WriteAddresses[^1] == PatchProfile.BufferAddress + 787 && fake.WriteSizes[^1] == 2 &&
        fake.Patch[787] == 3 && fake.Patch[788] == 116, "two-byte pre-delay write isolation");
}
using (var fake = new PatchTransport())
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    Check(amp.ReadPatch().SequenceEqual(fake.Patch), "multipart addressed snapshot including out-of-order blocks");
    amp.SetParameter("gain", 19);
    Check(amp.ReadPatch()[82] == 19, "parameter verified write");
    var saved = amp.ReadPatch();
    amp.ApplyPatch(saved);
    amp.CommitWorkingSlot();
    Check(fake.Slot == 1 && !fake.EditorMode && fake.Stored.SequenceEqual(saved), "A1 commit and recall verified");
    fake.OversizedFinalBlock = true;
    Check(amp.ReadPatch().SequenceEqual(fake.Patch), "recall notification extends past range but requested bytes fully verified");
    fake.OversizedFinalBlock = false;
    fake.CorruptResponse = true;
    Reject(() => amp.ReadPatch(), "bad multipart checksum rejected");
    fake.CorruptResponse = false;
    fake.OmitBlock = true;
    var incomplete = false;
    try
    {
        amp.ReadPatch();
    }
    catch (TimeoutException)
    {
        incomplete = true;
    }
    Check(incomplete, "incomplete snapshot fails rather than silently losing bytes");
}
using (var fake = new PatchTransport())
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    var target = (byte[])fake.Patch.Clone();
    for (var index = 50; index < 350; index++)
    {
        target[index] = (byte)((target[index] + 1) % 128);
    }
    target[1000] ^= 1;
    amp.ApplyPatch(target);
    Check(fake.Patch.SequenceEqual(target), "multi-run selective apply");
    Check(fake.WriteAddresses.SequenceEqual(fake.WriteAddresses.OrderDescending()), "changed runs sent in descending address order");
    Check(fake.WriteSizes.Max() <= 128 && fake.WriteSizes.Sum() == 301, "chunk limit and unchanged bytes excluded");
    Check(fake.WriteAddresses.Count == 4, "long run split into bounded chunks");
}
using (var fake = new PatchTransport { IgnoreWrites = true })
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    var target = (byte[])fake.Patch.Clone();
    target[82] ^= 1;
    var failed = false;
    try
    {
        amp.ApplyPatch(target);
        amp.CommitWorkingSlot();
    }
    catch (IOException)
    {
        failed = true;
    }
    Check(failed && fake.Stored.All(value => value == 0), "read-back mismatch prevents A1 commit");
}
using (var fake = new PatchTransport())
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    amp.CommitWorkingSlot();
    amp.SetParameter("ampType", 4);
    amp.SetParameter("gain", 19);
    amp.CommitWorkingSlot();
    Check(fake.Stored[81] == 4 && fake.Stored[82] == 19 && fake.Patch.SequenceEqual(fake.Stored), "edited sound really stored rather than historical no-op save");
    Check(fake.RecallCount == 1, "active A1 not unnecessarily recalled on subsequent commit");
    fake.IgnoreWrites = true;
    fake.Patch[82] = 18;
    var failed = false;
    try
    {
        amp.CommitWorkingSlot();
    }
    catch (IOException)
    {
        failed = true;
    }
    Check(failed && fake.Patch[82] == 18 && !fake.EditorMode, "failed stored write neither recalls stale slot nor leaves editor mode active");
}
using (var fake = new PatchTransport())
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    var patchBefore = fake.Patch.ToArray();
    var eqTone = fake.SystemEq[1..11];
    Check(amp.ReadListeningLevel() == new ListeningLevel(true, true, true, 0), "original Global EQ decode");
    Reject(() => amp.SetListeningLevel(-21, false), "listening level lower bound");
    Reject(() => amp.SetListeningLevel(1, false), "listening level never boosts");
    Check(fake.WriteAddresses.Count == 0, "invalid listening levels never write");
    amp.SetListeningLevel(-12, false);
    Check(amp.ReadListeningLevel().Decibels == -12 && fake.WriteAddresses.SequenceEqual(new[] { ListeningLevel.Address + 11 }), "only system EQ level written");
    Check(fake.Patch.SequenceEqual(patchBefore) && fake.SystemEq[1..11].SequenceEqual(eqTone), "patch and existing EQ tone untouched");
    amp.CommitWorkingSlot();
    amp.ApplyPatch(patchBefore);
    Check(amp.ReadListeningLevel().Decibels == -12, "listening level survives commit recall and patch apply");
    fake.SystemEq[0] = 0;
    fake.SystemEq[12] = 0;
    fake.WriteAddresses.Clear();
    Reject(() => amp.SetListeningLevel(-20, false), "EQ setup requires explicit confirmation");
    Check(fake.WriteAddresses.Count == 0, "unconfirmed setup never writes");
    amp.SetListeningLevel(-20, true);
    Check(fake.WriteAddresses.SequenceEqual(new[] { ListeningLevel.Address + 11, ListeningLevel.Address + 12, ListeningLevel.Address }), "attenuates before activating output EQ");
    Check(fake.SystemEq[1..11].SequenceEqual(eqTone), "setup preserves EQ filters");
    fake.IgnoreWrites = true;
    var failed = false;
    try
    {
        amp.SetListeningLevel(-5, false);
    }
    catch (IOException)
    {
        failed = true;
    }
    Check(failed, "unverified listening level fails");
}
var temp = Path.Combine(Path.GetTempPath(), "katana-store-test-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new PresetStore(Path.Combine(temp, "nested", "presets"));
    Check(Directory.Exists(Path.Combine(temp, "nested", "presets")), "nested library directories created durably");
    var patch = new byte[PatchProfile.Length];
    var summary = store.Create("  Original  ", Convert.ToHexStringLower(identity), patch);
    Check(summary.Name == "Original" && store.List().Length == 1, "preset capture and list");
    Check(new PresetStore(Path.Combine(temp, "nested", "presets")).Read(summary.Id).Patch.SequenceEqual(patch), "preset survives store restart");
    Check(store.Rename(summary.Id, "Renamed").Name == "Renamed", "preset rename");
    Reject(() => store.Create(" ", "identity", patch), "empty name rejected");
    Reject(() => store.Create("valid", "identity", [128]), "invalid snapshot rejected");
    using (var fake = new PatchTransport())
    using (var session = new DeviceSession("fake", store, _ => fake))
    {
        session.Connect();
        fake.CorruptResponse = true;
        Reject(() => session.Set("gain", 19), "malformed post-write response rejected");
        Check(!session.Read().Connected && fake.Disposed, "protocol failure disconnects session");
    }
    using (var fake = new PatchTransport())
    using (var session = new DeviceSession("fake", store, _ => fake))
    {
        Check(session.Monitor()?.Connected == false, "monitor never auto-connects");
        session.Connect();
        var writes = fake.WriteAddresses.Count;
        fake.Patch[Parameters.Find("gain").Offset] = 63;
        fake.Slot = 7;
        var observed = session.Monitor();
        Check(observed?.Values["gain"] == 63 && observed.Slot == 7, "monitor observes physical knob and footswitch state");
        Check(fake.WriteAddresses.Count == writes && !fake.EditorMode, "monitor sends no writes including editor mode");
        fake.PassiveSlotUnsupported = true;
        observed = session.Monitor();
        Check(observed is { Connected: true, Slot: null } && observed.Values["gain"] == 63, "unsupported passive slot does not block control monitoring");
        var reads = fake.ReadAddresses.Count(address => address == (1 << 14));
        session.Monitor();
        Check(fake.ReadAddresses.Count(address => address == (1 << 14)) == reads, "unsupported passive slot probe cached until reconnect");
        fake.Unavailable = true;
        Check(session.Monitor()?.Connected == false && fake.Disposed, "monitor disconnects quietly on power-off");
    }
    foreach (var address in new[] { ListeningLevel.Address, UsbAudioLevels.Address })
    {
        using var fake = new PatchTransport();
        using var session = new DeviceSession("fake", store, _ => fake);
        session.Connect();
        fake.FailReadAddress = address;
        Check(session.Monitor()?.Connected == false && fake.Disposed,
            "monitor disconnects on hard IO failure during optional system read");
    }
    using (var fake = new PatchTransport())
    using (var session = new DeviceSession("fake", store, _ => fake))
    {
        Check(!session.CheckConnection().Connected, "heartbeat does not open disconnected device");
        session.Connect();
        var writes = fake.WriteAddresses.Count;
        Check(session.CheckConnection().Connected && fake.WriteAddresses.Count == writes && !fake.EditorMode,
            "heartbeat proves liveness with no writes or editor mode");
        fake.Unavailable = true;
        Check(!session.CheckConnection().Connected && fake.Disposed && !session.Read().Connected,
            "powered-off amp heartbeat disconnects and reports offline without error");
    }
    using (var fake = new PatchTransport())
    using (var session = new DeviceSession("fake", store, _ => fake))
    {
        session.Connect();
        var writes = fake.WriteAddresses.Count;
        fake.Unavailable = true;
        var offline = session.Read();
        Check(!offline.Connected && offline.Identity is null && offline.Values.Count == 0 && offline.Slot is null,
            "page entry with stale powered-off MIDI session returns clean offline state");
        Check(fake.Disposed && fake.WriteAddresses.Count == writes, "stale page read disposes without writes");
        Check(!session.Read().Connected, "subsequent offline read does not retry stale transport");
    }
    using (var fake = new PatchTransport())
    using (var session = new DeviceSession("fake", store, _ => fake))
    {
        session.Connect();
        fake.FailSlotRead = true;
        var failed = false;
        try
        {
            session.Read();
        }
        catch (IOException)
        {
            failed = true;
        }
        Check(failed && fake.Disposed, "page slot/editor-mode failure remains visible and disconnects");
    }
    using (var fake = new PatchTransport())
    using (var session = new DeviceSession("fake", store, _ => fake))
    {
        session.Connect();
        fake.CorruptResponse = true;
        Reject(() => session.Read(), "malformed page liveness probe remains visible");
        Check(fake.Disposed && !session.Read().Connected, "malformed page probe disconnects session");
    }
    using (var fake = new PatchTransport())
    using (var session = new DeviceSession("fake", store, _ => fake))
    {
        Check(session.Connect().ListeningLevel?.Available == true, "system listening level included in state");
        session.SetListeningLevel(-10, false);
        var capture = session.Capture("Volume independent");
        Check(store.Read(capture.Id).Patch.SequenceEqual(fake.Patch), "capture contains unscaled patch only");
        Check(session.Apply(capture.Id).ListeningLevel?.Decibels == -10, "app apply preserves listening level");
        store.Delete(capture.Id);
        fake.IgnoreWrites = true;
        var failed = false;
        try
        {
            session.SetListeningLevel(-15, false);
        }
        catch (IOException)
        {
            failed = true;
        }
        Check(failed && !session.Read().Connected, "failed system write disconnects session");
    }
    using (var fake = new PatchTransport { UnsupportedEq = true })
    using (var session = new DeviceSession("fake", store, _ => fake))
    {
        Check(session.Connect().ListeningLevel?.Available == false, "unsupported global EQ does not block connection");
        var reads = fake.ReadAddresses.Count;
        Reject(() => session.SetListeningLevel(-10, true), "unsupported listening mutation rejected");
        Check(fake.ReadAddresses.Count == reads && !fake.Disposed, "unsupported mutation neither probes nor disconnects");
        Check(session.Set("gain", 15).Connected, "unsupported global EQ still allows patch editing");
    }
    store.Delete(summary.Id);
    Check(store.List().Length == 0 && !Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).Any(), "preset deletion and no temporary files");
}
finally
{
    Directory.Delete(temp, true);
}
using (var fake = new PatchTransport())
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    Check(amp.ReadUsbAudioLevels() == new UsbAudioLevels(true, 0, 0), "USB mute decoding");
    Reject(() => amp.SetUsbAudioLevel("processed", -2), "USB lower bound");
    Reject(() => amp.SetUsbAudioLevel("dry", 202), "USB upper bound");
    Reject(() => amp.SetUsbAudioLevel("dry", 101), "USB raw resolution");
    Reject(() => amp.SetUsbAudioLevel("invalid", 100), "USB output validation");
    Check(fake.WriteAddresses.Count == 0, "invalid USB input never writes");
    amp.SetUsbAudioLevel("processed", 100);
    amp.SetUsbAudioLevel("dry", 200);
    Check(amp.ReadUsbAudioLevels() == new UsbAudioLevels(true, 100, 200), "USB percent mapping");
    Check(fake.Usb.SequenceEqual(new byte[] { 20, 50, 0, 1, 0, 0, 100 }), "USB neighbouring settings preserved");
    Check(fake.WriteAddresses.SequenceEqual(new[] { 0x52, 0x57 }) && fake.WriteSizes.All(size => size == 1), "USB writes only selected output byte");
    amp.CommitWorkingSlot();
    amp.ApplyPatch(fake.Patch);
    Check(amp.ReadUsbAudioLevels().ProcessedPercent == 100, "USB levels independent of patches and commit");
    fake.IgnoreWrites = true;
    try
    {
        amp.SetUsbAudioLevel("dry", 100);
        throw new Exception("FAIL: ignored USB write accepted");
    }
    catch (IOException)
    {
        count++;
    }
    fake.Usb[1] = 101;
    Reject(() => amp.ReadUsbAudioLevels(), "USB unsupported encoding");
}
var usbTestDirectory = Path.Combine(Path.GetTempPath(), "katana-usb-" + Guid.NewGuid());
try
{
    using var fake = new PatchTransport();
    using var session = new DeviceSession("fake", new PresetStore(usbTestDirectory), _ => fake);
    Check(session.Connect().UsbAudioLevels?.Available == true, "USB included on connect");
    Check(session.SetUsbAudioLevel("processed", 100).UsbAudioLevels?.ProcessedPercent == 100, "USB session mutation state");
    fake.IgnoreWrites = true;
    try
    {
        session.SetUsbAudioLevel("processed", 200);
        throw new Exception("FAIL: session accepted unverified USB write");
    }
    catch (IOException)
    {
        Check(!session.Read().Connected && fake.Disposed, "USB failed verification disconnects session");
    }
    using var unsupported = new PatchTransport { UnsupportedUsb = true };
    using var optional = new DeviceSession("fake", new PresetStore(usbTestDirectory), _ => unsupported);
    Check(optional.Connect().UsbAudioLevels?.Available == false, "unsupported USB does not block patch connection");
    Reject(() => optional.SetUsbAudioLevel("dry", 100), "unsupported USB mutation rejected");
}
finally
{
    if (Directory.Exists(usbTestDirectory))
    {
        Directory.Delete(usbTestDirectory, true);
    }
}
Console.WriteLine($"PASS: {count} checks (no hardware access)");

foreach (var slot in Enumerable.Range(1, 8))
{
    Check(PatchProfile.StoredSlotAddress(slot) == ((0x10 << 21) | (slot << 14)), $"slot {slot} stored address");
    using var fake = new PatchTransport();
    var amp = new KatanaDevice(fake);
    amp.Identify();
    var original = fake.Patch.ToArray();
    amp.CommitWorkingSlot(slot);
    Check(fake.Slot == slot && fake.Slots[slot].SequenceEqual(original), $"save and recall slot {slot}");
    Check(Enumerable.Range(1, 8).Where(other => other != slot).All(other => fake.Slots[other].All(value => value == 0)), $"other slots untouched for {slot}");
    fake.Patch[82] = 0;
    amp.RecallSlot(slot);
    Check(fake.Patch.SequenceEqual(original) && !fake.EditorMode, $"recall stored slot {slot} restores sound");
    Check(fake.SystemEq[11] == 20 && fake.Usb[1] == 0, "global settings not changed by slot operation");
}
using (var fake = new PatchTransport())
{
    var amp = new KatanaDevice(fake);
    amp.Identify();
    var reads = fake.ReadAddresses.Count;
    var writes = fake.WriteAddresses.Count;
    foreach (var slot in new[] { -1, 0, 9 })
    {
        Reject(() => amp.CommitWorkingSlot(slot), "invalid save destination rejected before MIDI");
    }
    foreach (var slot in new[] { -1, 9 })
    {
        Reject(() => amp.RecallSlot(slot), "invalid recall destination rejected before MIDI");
    }
    Check(fake.ReadAddresses.Count == reads && fake.WriteAddresses.Count == writes && fake.RecallCount == 0, "invalid slots perform no IO");
    amp.RecallSlot(0);
    Check(fake.Slot == 0 && !fake.EditorMode, "Panel can be recalled but not saved");
    fake.Patch[82] = 42;
    fake.IgnoreWrites = true;
    var failed = false;
    try
    {
        amp.CommitWorkingSlot(8);
    }
    catch (IOException)
    {
        failed = true;
    }
    Check(failed && !fake.EditorMode, "B4 save failure verified and editor mode released");
}
Console.WriteLine("PASS: slot checks complete (no hardware access)");

sealed class FakeTransport(byte original) : IMidiTransport
{
    public byte Gain { get; private set; } = original;
    public List<byte> Writes { get; } = [];
    public bool FailAfterWrite { get; set; }
    public bool FailRestorationSend { get; set; }
    public bool FailRestorationRead { get; set; }
    public void Send(byte[] message)
    {
        if (FailRestorationSend && Writes.Count == 1)
        {
            throw new IOException("Simulated restoration write failure");
        }
        Gain = message[12];
        Writes.Add(Gain);
    }
    public byte[] Request(byte[] message, Func<byte[], bool> matches)
    {
        if (message.SequenceEqual(KatanaProtocol.IdentityRequest()))
        {
            // Identity and the initial gain probe must not consume fault injection.
            return Convert.FromHexString("F07E000602413303000004010000F7");
        }
        if (FailRestorationRead && Writes.Count == 2)
        {
            throw new TimeoutException("Simulated restoration timeout");
        }
        if (FailAfterWrite && Writes.Count == 1)
        {
            FailAfterWrite = false;
            throw new TimeoutException("Simulated read failure");
        }
        var response = message.SequenceEqual(KatanaProtocol.IdentityRequest())
            ? Convert.FromHexString("F07E000602413303000004010000F7")
            : KatanaProtocol.GainWrite(0, Gain);
        if (!matches(response))
        {
            throw new InvalidDataException("Unexpected fake request");
        }
        return response;
    }
    public void Dispose() { }
}

sealed class PatchTransport : IMidiTransport
{
    public byte[] Patch { get; } = Enumerable.Range(0, PatchProfile.Length).Select(index => (byte)(index % 128)).ToArray();
    public bool UnsupportedUsb { get; set; }
    public byte[] Usb { get; } = [20, 0, 0, 1, 0, 0, 0];
    public byte[] SystemEq { get; } = [1, 0, 20, 0, 0, 20, 0, 0, 20, 20, 0, 20, 1];
    public bool FailSlotRead { get; set; }
    public bool UnsupportedEq { get; set; }
    public byte[][] Slots { get; } = Enumerable.Range(0, 9).Select(_ => new byte[PatchProfile.Length]).ToArray();
    public byte[] Stored => Slots[1];
    public int Slot { get; set; }
    public bool PassiveSlotUnsupported { get; set; }
    public int? FailReadAddress { get; set; }
    public int RecallCount { get; private set; }
    public bool EditorMode { get; private set; }
    public bool CorruptResponse { get; set; }
    public bool OmitBlock { get; set; }
    public bool OversizedFinalBlock { get; set; }
    public bool IgnoreWrites { get; set; }
    public bool Unavailable { get; set; }
    public bool Disposed { get; private set; }
    public List<int> WriteAddresses { get; } = [];
    public List<int> WriteSizes { get; } = [];
    public List<int> ReadAddresses { get; } = [];
    public void Send(byte[] message)
    {
        var (address, data) = KatanaProtocol.ParseData(message, 0);
        if (address >= UsbAudioLevels.Address && address < UsbAudioLevels.Address + 7)
        {
            WriteAddresses.Add(address);
            WriteSizes.Add(data.Length);
            if (!IgnoreWrites)
            {
                data.CopyTo(Usb, address - UsbAudioLevels.Address);
            }
        }
        else if (address >= ListeningLevel.Address && address < ListeningLevel.Address + 13)
        {
            WriteAddresses.Add(address);
            WriteSizes.Add(data.Length);
            if (!IgnoreWrites)
            {
                data.CopyTo(SystemEq, address - ListeningLevel.Address);
            }
        }
        else if (address >= PatchProfile.BufferAddress && address < PatchProfile.BufferAddress + PatchProfile.Length)
        {
            WriteAddresses.Add(address);
            WriteSizes.Add(data.Length);
            if (!IgnoreWrites)
            {
                data.CopyTo(Patch, address - PatchProfile.BufferAddress);
            }
        }
        else if (address >= (0x10 << 21) && address < ((0x10 << 21) | (9 << 14)))
        {
            if (!EditorMode)
            {
                throw new InvalidOperationException("Stored writes require editor mode");
            }
            if (!IgnoreWrites)
            {
                var slot = (address - (0x10 << 21)) >> 14;
                data.CopyTo(Slots[slot], address - ((0x10 << 21) | (slot << 14)));
            }
        }
        else if (address == ((0x7F << 21) | 1))
        {
            EditorMode = data[0] == 1;
        }
        else if (address == ((0x7F << 21) | (1 << 7) | 4))
        {
            if (!EditorMode)
            {
                throw new InvalidOperationException("Save requires editor mode");
            }
            // The historical save command is ignored by the original Artist firmware.
        }
        else if (address == (1 << 14))
        {
            RecallCount++;
            Slot = data[1];
            Slots[Slot].CopyTo(Patch, 0);
        }
    }
    public byte[] Request(byte[] message, Func<byte[], bool> matches)
    {
        if (Unavailable)
        {
            throw new IOException("Amp powered off");
        }
        if (message.SequenceEqual(KatanaProtocol.IdentityRequest()))
        {
            var identity = Convert.FromHexString("F07E000602413303000004010000F7");
            if (matches(identity))
            {
                return identity;
            }
        }
        var address = (message[8] << 21) | (message[9] << 14) | (message[10] << 7) | message[11];
        ReadAddresses.Add(address);
        if (FailReadAddress == address)
        {
            throw new IOException("Simulated system read failure");
        }
        if (PassiveSlotUnsupported && !EditorMode && address == (1 << 14))
        {
            throw new TimeoutException("Slot unavailable outside editor mode");
        }
        if (FailSlotRead && address == (1 << 14))
        {
            throw new IOException("Slot read failed after entering editor mode");
        }
        var length = (message[12] << 21) | (message[13] << 14) | (message[14] << 7) | message[15];
        if (UnsupportedEq && address >= ListeningLevel.Address && address < ListeningLevel.Address + 13)
        {
            throw new TimeoutException("Global EQ unavailable");
        }
        if (UnsupportedUsb && address == UsbAudioLevels.Address)
        {
            throw new TimeoutException("USB levels unavailable");
        }
        var data = address >= UsbAudioLevels.Address && address < UsbAudioLevels.Address + 7
            ? Usb[(address - UsbAudioLevels.Address)..(address - UsbAudioLevels.Address + length)]
            : address >= ListeningLevel.Address && address < ListeningLevel.Address + 13
            ? SystemEq[(address - ListeningLevel.Address)..(address - ListeningLevel.Address + length)]
            : address == (1 << 14) ? new byte[] { 0, (byte)Slot }
            : address >= PatchProfile.BufferAddress ? Patch[(address - PatchProfile.BufferAddress)..(address - PatchProfile.BufferAddress + length)]
            : Slots[(address - (0x10 << 21)) >> 14][((address - (0x10 << 21)) % (1 << 14))..(((address - (0x10 << 21)) % (1 << 14)) + length)];
        var blocks = Enumerable.Range(0, (length + 240) / 241).Reverse();
        foreach (var block in blocks)
        {
            if (OmitBlock && block == 0)
            {
                continue;
            }
            var offset = block * 241;
            var payload = data[offset..Math.Min(offset + 241, length)];
            if (OversizedFinalBlock && length == PatchProfile.Length && offset + 241 > length)
            {
                Array.Resize(ref payload, 241);
            }
            var frame = KatanaProtocol.WriteRequest(0, address + offset, payload);
            if (CorruptResponse)
            {
                frame[^2] ^= 1;
            }
            if (matches(frame))
            {
                return frame;
            }
        }
        throw new TimeoutException("Missing patch response");
    }
    public void Dispose() => Disposed = true;
}

using System.Text;
using Katana.Core;

namespace Katana.Web;

public sealed record DeviceConnection(bool Connected);

public sealed record DeviceState(bool Connected, string? Identity, Dictionary<string, int> Values, string? PatchName, int? Slot, ListeningLevel? ListeningLevel = null, UsbAudioLevels? UsbAudioLevels = null);

// Every HTTP operation holds one lock through complete read/write verification and disk capture.
public sealed class DeviceSession(string port, PresetStore presets, Func<string, IMidiTransport>? transportFactory = null) : IDisposable
{
    private readonly object gate = new();
    private IMidiTransport? transport;
    private KatanaDevice? device;
    private string? identity;
    private bool listeningLevelUnavailable;
    private bool usbAudioUnavailable;
    private bool passiveSlotUnavailable;

    public DeviceState Connect(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Disconnect();
            try
            {
                transport = transportFactory is null ? new AlsaMidiTransport(port) : transportFactory(port);
                device = new KatanaDevice(transport);
                identity = Convert.ToHexStringLower(device.Identify());
                return State();
            }
            catch
            {
                Disconnect();
                throw;
            }
        }
    }

    public DeviceConnection CheckConnection(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (device is null)
            {
                return new(false);
            }
            try
            {
                // A one-byte read proves the amp is responding without editor-mode writes
                // or transferring an entire patch on every heartbeat.
                device.ReadGain();
                return new(true);
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
            {
                Disconnect();
                return new(false);
            }
        }
    }

    // Passive snapshots share the same lock as writes. Never enter editor mode or
    // save/recall a patch just to monitor physical knobs and footswitches.
    public DeviceState? Monitor(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (device is null)
            {
                return new(false, null, [], null, null);
            }
            try
            {
                var before = TryReadPassiveSlot();
                var patch = device.ReadPatch();
                var after = TryReadPassiveSlot();
                if (before != after)
                {
                    // A footswitch changed slots during transfer. Reconcile on the next
                    // poll rather than publish a known mixed snapshot or perform writes.
                    return null;
                }
                return new(true, identity, Parameters.All.ToDictionary(parameter => parameter.Id, parameter => parameter.Decode(patch)),
                    Encoding.ASCII.GetString(patch, 0, 16).TrimEnd(' ', '\0'), after, TryReadListeningLevel(propagateIoFailure: true), TryReadUsbAudioLevels(propagateIoFailure: true));
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
            {
                Disconnect();
                return new(false, null, [], null, null);
            }
        }
    }

    private int? TryReadPassiveSlot()
    {
        if (passiveSlotUnavailable)
        {
            return null;
        }
        try
        {
            return RequireDevice().ReadSlotPassive();
        }
        catch (TimeoutException)
        {
            // Some firmware may expose the slot only in editor mode. Continue
            // monitoring controls without inventing a current slot or repeated timeouts.
            passiveSlotUnavailable = true;
            return null;
        }
    }

    public DeviceState Read(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Probe without editor-mode writes before reading patch/slot state.
                device?.ReadGain();
            }
            catch (Exception error) when (error is IOException or TimeoutException)
            {
                Disconnect();
                return State();
            }
            catch (InvalidDataException)
            {
                Disconnect();
                throw;
            }
            // Slot reads enter/exit editor mode: failures here must remain visible.
            return Execute(() => State(), cancellationToken);
        }
    }
    public DeviceState Set(string id, int value, CancellationToken cancellationToken = default) => Execute(() =>
    {
        RequireDevice().SetParameter(id, value);
        return State();
    }, cancellationToken);
    // Preflight the complete batch before the first write. Hardware failures may still
    // leave partial changes; Execute disconnects on verification failure, with no retry.
    public DeviceState SetMany(IReadOnlyDictionary<string, int> values, CancellationToken cancellationToken = default) => Execute(() =>
    {
        if (values.Count is < 1 or > 64)
        {
            throw new ArgumentException("Supply between 1 and 64 parameter changes.");
        }
        var changes = values.Select(pair => (Parameter: Parameters.Find(pair.Key), pair.Value)).ToArray();
        foreach (var change in changes)
        {
            change.Parameter.Encode(change.Value);
        }
        var amp = RequireDevice();
        var patch = amp.ReadPatch();
        foreach (var change in changes)
        {
            change.Parameter.Encode(change.Value).CopyTo(patch, change.Parameter.Offset);
        }
        foreach (var change in changes)
        {
            if (change.Parameter.DependsOn is { } selector
                && Parameters.Find(selector).Decode(patch) != change.Parameter.DependsOnValue)
            {
                throw new ArgumentException($"{change.Parameter.Label} is inactive for the requested effect type.");
            }
        }
        // Select algorithms before their dependent controls, regardless of JSON order.
        foreach (var change in changes.OrderBy(change => change.Parameter.DependsOn is null ? 0 : 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            amp.SetParameter(change.Parameter.Id, change.Value);
        }
        return State();
    }, cancellationToken);
    public DeviceState SetListeningLevel(int decibels, bool configureOutputEq, CancellationToken cancellationToken = default) => Execute(() =>
    {
        if (listeningLevelUnavailable)
        {
            throw new InvalidOperationException("Listening level is unavailable on this firmware. Reconnect to probe again; patch editing remains available.");
        }
        RequireDevice().SetListeningLevel(decibels, configureOutputEq);
        return State();
    }, cancellationToken);
    public DeviceState SetUsbAudioLevel(string output, int percent, CancellationToken cancellationToken = default) => Execute(() =>
    {
        if (usbAudioUnavailable)
        {
            throw new InvalidOperationException("USB recording levels are unavailable on this firmware. Reconnect to probe again.");
        }
        RequireDevice().SetUsbAudioLevel(output, percent);
        return State();
    }, cancellationToken);
    public DeviceState Commit(CancellationToken cancellationToken = default) => Commit(1, cancellationToken);
    public DeviceState Recall(int slot, CancellationToken cancellationToken = default) => Execute(() =>
    {
        RequireDevice().RecallSlot(slot);
        return State();
    }, cancellationToken);
    public DeviceState Commit(int slot, CancellationToken cancellationToken = default) => Execute(() =>
    {
        RequireDevice().CommitWorkingSlot(slot);
        return State();
    }, cancellationToken);
    public PresetSummary Capture(string name, CancellationToken cancellationToken = default) => Execute(() =>
    {
        name = PresetStore.ValidateName(name);
        return presets.Create(name, identity!, RequireDevice().ReadPatch());
    }, cancellationToken);
    public DeviceState Apply(Guid id, CancellationToken cancellationToken = default) => Apply(id, 1, cancellationToken);
    public DeviceState Apply(Guid id, int slot, CancellationToken cancellationToken = default) => Execute(() =>
    {
        PatchProfile.ValidateSlot(slot, false);
        var connectedDevice = RequireDevice();
        var preset = presets.Read(id);
        if (preset.Identity != identity)
        {
            throw new ArgumentException("Preset identity/firmware differs from the connected amp.");
        }
        try
        {
            connectedDevice.ApplyPatch(preset.Patch);
            connectedDevice.CommitWorkingSlot(slot);
            return State();
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
        {
            throw new IOException($"Preset apply/save was not confirmed; the amp or {PatchProfile.SlotLabel(slot)} slot may be partially changed. Reconnect before continuing. {error.Message}", error);
        }
    }, cancellationToken);

    private T Execute<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
            {
                Disconnect();
                throw;
            }
        }
    }

    private DeviceState State()
    {
        if (device is null)
        {
            return new(false, null, [], null, null);
        }
        var patch = device.ReadPatch();
        return new(true, identity, Parameters.All.ToDictionary(parameter => parameter.Id, parameter => parameter.Decode(patch)),
            Encoding.ASCII.GetString(patch, 0, 16).TrimEnd(' ', '\0'), device.ReadSlot(), TryReadListeningLevel(), TryReadUsbAudioLevels());
    }

    private UsbAudioLevels TryReadUsbAudioLevels(bool propagateIoFailure = false)
    {
        if (usbAudioUnavailable)
        {
            return UsbAudioLevels.Unavailable;
        }
        try
        {
            return RequireDevice().ReadUsbAudioLevels();
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
        {
            if (propagateIoFailure && error is IOException)
            {
                throw;
            }
            usbAudioUnavailable = true;
            return UsbAudioLevels.Unavailable;
        }
    }

    private ListeningLevel TryReadListeningLevel(bool propagateIoFailure = false)
    {
        if (listeningLevelUnavailable)
        {
            return ListeningLevel.Unavailable;
        }
        try
        {
            return RequireDevice().ReadListeningLevel();
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException)
        {
            if (propagateIoFailure && error is IOException)
            {
                throw;
            }
            // Optional system capability: old/unsupported firmware must still allow patch editing.
            listeningLevelUnavailable = true;
            return ListeningLevel.Unavailable;
        }
    }

    private KatanaDevice RequireDevice() => device ?? throw new InvalidOperationException("Connect the amp first.");
    private void Disconnect()
    {
        transport?.Dispose();
        transport = null;
        device = null;
        identity = null;
        listeningLevelUnavailable = false;
        usbAudioUnavailable = false;
        passiveSlotUnavailable = false;
    }
    public void Dispose()
    {
        lock (gate)
        {
            Disconnect();
        }
    }
}

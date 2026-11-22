namespace Katana.Core;

public sealed class KatanaDevice(IMidiTransport transport)
{
    private byte? deviceId;

    public byte[] Identify()
    {
        var identity = transport.Request(KatanaProtocol.IdentityRequest(), KatanaProtocol.IsIdentity);
        deviceId = identity[2];
        return identity;
    }

    public byte ReadGain()
    {
        var id = RequireIdentity();
        var response = transport.Request(KatanaProtocol.GainRequest(id), frame => KatanaProtocol.IsGainResponse(frame, id));
        return KatanaProtocol.ParseGain(response, id);
    }

    public void TestGain(Action<string> report)
    {
        var original = ReadGain();
        var target = (byte)(original == 0 ? 1 : original - 1);
        report($"Original gain: {original}; temporary gain: {target}");
        Exception? operationFailure = null;
        try
        {
            transport.Send(KatanaProtocol.GainWrite(RequireIdentity(), target));
            var actual = ReadGain();
            if (actual != target)
            {
                throw new IOException($"Gain write verification failed: expected {target}, received {actual}.");
            }
            report($"Write/read-back verified: {actual}");
        }
        catch (Exception error)
        {
            operationFailure = error;
            throw;
        }
        finally
        {
            try
            {
                transport.Send(KatanaProtocol.GainWrite(RequireIdentity(), original));
                var restored = ReadGain();
                if (restored != original)
                {
                    throw new IOException($"Expected {original}, received {restored}.");
                }
                report($"Restored and verified gain: {restored}");
            }
            catch (Exception restorationFailure)
            {
                var details = operationFailure is null
                    ? restorationFailure.Message
                    : $"Original test failure: {operationFailure.Message}; restoration failure: {restorationFailure.Message}";
                var cause = operationFailure is null
                    ? restorationFailure
                    : new AggregateException(operationFailure, restorationFailure);
                throw new IOException($"GAIN RESTORATION FAILED OR UNVERIFIED; gain may remain changed. {details}", cause);
            }
        }
    }

    public byte[] ReadBlock(int address, int length)
    {
        var id = RequireIdentity();
        var result = new byte[length];
        var covered = new bool[length];
        var remaining = length;
        transport.Request(KatanaProtocol.ReadRequest(id, address, length), frame =>
        {
            if (!KatanaProtocol.IsDataResponse(frame, id))
            {
                return false;
            }
            var block = KatanaProtocol.ParseData(frame, id);
            var offset = block.Address - address;
            if (offset < 0 || offset >= length)
            {
                return false;
            }
            // Recall can emit a full-size final notification overlapping the requested
            // range. Validate its checksum, but copy only requested bytes; require full coverage.
            var count = Math.Min(block.Data.Length, length - offset);
            for (var index = 0; index < count; index++)
            {
                var position = offset + index;
                if (covered[position] && result[position] != block.Data[index])
                {
                    throw new InvalidDataException("Conflicting patch response blocks.");
                }
                if (!covered[position])
                {
                    covered[position] = true;
                    remaining--;
                }
                result[position] = block.Data[index];
            }
            return remaining == 0;
        });
        if (remaining != 0)
        {
            throw new InvalidDataException("Incomplete patch response; no preset was captured.");
        }
        return result;
    }

    public byte[] ReadPatch() => ReadBlock(PatchProfile.BufferAddress, PatchProfile.Length);

    public void SetParameter(string parameterId, int value)
    {
        var parameter = Parameters.Find(parameterId);
        var encoded = parameter.Encode(value);
        if (parameter.DependsOn is not null)
        {
            var selector = Parameters.Find(parameter.DependsOn);
            var current = ReadBlock(PatchProfile.BufferAddress + selector.Offset, selector.Size);
            var selected = selector.Size == 1 ? current[0] : current[0] * 128 + current[1];
            if (selected != parameter.DependsOnValue)
            {
                throw new ArgumentException($"{parameter.Label} is inactive for the current {selector.Label}.");
            }
        }
        WriteVerified(PatchProfile.BufferAddress + parameter.Offset, encoded);
    }

    // Original MkI system Global EQ: 00 00 04 32 switch, 3D level, 3E position.
    // This block is outside both the working patch and all stored channel snapshots.
    public UsbAudioLevels ReadUsbAudioLevels()
    {
        var data = ReadBlock(UsbAudioLevels.Address, 7);
        if (data[1] > 100 || data[6] > 100)
        {
            throw new InvalidDataException("Unsupported Katana USB recording levels.");
        }
        return new(true, data[1] * 2, data[6] * 2);
    }

    public void SetUsbAudioLevel(string output, int percent)
    {
        if (percent is < 0 or > 200 || percent % 2 != 0)
        {
            throw new ArgumentException("USB recording level must be 0–200% in steps of 2%.");
        }
        var offset = output switch
        {
            "processed" => 1,
            "dry" => 6,
            _ => throw new ArgumentException("Unknown USB recording output.")
        };
        // Probe before writing; never initialise or alter adjacent USB playback settings.
        ReadUsbAudioLevels();
        WriteVerified(UsbAudioLevels.Address + offset, [(byte)(percent / 2)]);
    }

    public ListeningLevel ReadListeningLevel()
    {
        var data = ReadBlock(ListeningLevel.Address, 13);
        if (data[0] > 1 || data[11] > 40 || data[12] > 1)
        {
            throw new InvalidDataException("Unsupported original Katana Global EQ response.");
        }
        return new(true, data[0] == 1, data[12] == 1, data[11] - 20);
    }

    public void SetListeningLevel(int decibels, bool configureOutputEq)
    {
        if (decibels is < -20 or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decibels), "Listening level must be between -20 and 0 dB; boost is not allowed.");
        }
        var current = ReadListeningLevel();
        if ((!current.Enabled || !current.OutputPosition) && !configureOutputEq)
        {
            throw new InvalidOperationException("Confirm enabling Global EQ at the output before adjusting listening level. Existing EQ settings are preserved but enabling or moving them can change the sound.");
        }
        // Attenuate first, before activating/moving EQ. Never write frequency or tone settings.
        WriteVerified(ListeningLevel.Address + 11, [(byte)(decibels + 20)]);
        if (!current.OutputPosition)
        {
            WriteVerified(ListeningLevel.Address + 12, [1]);
        }
        if (!current.Enabled)
        {
            WriteVerified(ListeningLevel.Address, [1]);
        }
        var actual = ReadListeningLevel();
        if (!actual.Enabled || !actual.OutputPosition || actual.Decibels != decibels)
        {
            throw new IOException("Listening level could not be verified; Global EQ may be partially changed. Reconnect before continuing.");
        }
    }

    public void ApplyPatch(byte[] patch)
    {
        ValidatePatch(patch);
        WriteChangedRuns(PatchProfile.BufferAddress, patch);
        if (!ReadPatch().SequenceEqual(patch))
        {
            throw new IOException("Patch read-back differs; the amp may be partially changed. Refresh before continuing.");
        }
    }

    public void CommitWorkingSlot(int slot = 1)
    {
        var address = PatchProfile.StoredSlotAddress(slot);
        var patch = ReadPatch();
        // Current original-Artist firmware uses direct stored-patch writes, as implemented
        // by Katana FxFloorBoard. The historical 7F000104 command leaves A1 unchanged.
        InEditorMode(() => WriteChangedRuns(address, patch));
        var stored = ReadBlock(address, PatchProfile.Length);
        if (!stored.SequenceEqual(patch))
        {
            var differences = Enumerable.Range(0, patch.Length).Where(index => stored[index] != patch[index]);
            throw new IOException($"{PatchProfile.SlotLabel(slot)} save could not be verified; the slot may have changed. Differences: {string.Join(", ", differences.Take(30).Select(index => $"{index}: {patch[index]} -> {stored[index]}"))}");
        }
        // The target may already be active. Recalling it unnecessarily produces a burst of
        // asynchronous patch notifications that can overlap subsequent MIDI requests.
        if (ReadSlot() != slot)
        {
            InEditorMode(() =>
            {
                transport.Send(KatanaProtocol.WriteRequest(RequireIdentity(), 1 << 14, [0, (byte)slot]));
                Thread.Sleep(500);
            });
        }
        if (!ReadPatch().SequenceEqual(patch) || ReadSlot() != slot)
        {
            throw new IOException($"{PatchProfile.SlotLabel(slot)} recall could not be verified; refresh the amp.");
        }
    }

    public void RecallSlot(int slot)
    {
        PatchProfile.ValidateSlot(slot, true);
        // Read the destination first, before discarding live edits. Never write stored data.
        var expected = slot == 0 ? null : ReadBlock(PatchProfile.StoredSlotAddress(slot), PatchProfile.Length);
        InEditorMode(() =>
        {
            transport.Send(KatanaProtocol.WriteRequest(RequireIdentity(), 1 << 14, [0, (byte)slot]));
            Thread.Sleep(500);
        });
        if (ReadSlot() != slot || (expected is not null && !ReadPatch().SequenceEqual(expected)))
        {
            throw new IOException($"{PatchProfile.SlotLabel(slot)} recall could not be verified; reconnect before continuing.");
        }
    }

    public int ReadSlot()
    {
        byte[] slot = [];
        InEditorMode(() => slot = ReadBlock(1 << 14, 2));
        return DecodeSlot(slot);
    }

    // RQ1 only: monitoring must not repeatedly enter/exit editor mode.
    public int ReadSlotPassive() => DecodeSlot(ReadBlock(1 << 14, 2));

    private static int DecodeSlot(byte[] slot)
    {
        if (slot[0] != 0 || slot[1] > 8)
        {
            throw new InvalidDataException("Unexpected amp slot response.");
        }
        return slot[1];
    }

    public static void ValidatePatch(byte[] patch)
    {
        if (patch.Length != PatchProfile.Length || patch.Any(value => value > 127))
        {
            throw new ArgumentException("Invalid original-generation patch snapshot.");
        }
    }

    private void WriteChangedRuns(int address, byte[] patch)
    {
        var current = ReadBlock(address, patch.Length);
        // Do not rewrite unchanged data: broad writes trigger firmware-derived routing and
        // pedal assignments. Apply changed contiguous runs, later control selectors first.
        for (var end = patch.Length; end > 0;)
        {
            if (patch[end - 1] == current[end - 1])
            {
                end--;
                continue;
            }
            var start = end - 1;
            while (start > 0 && end - start < 128 && patch[start - 1] != current[start - 1])
            {
                start--;
            }
            transport.Send(KatanaProtocol.WriteRequest(RequireIdentity(), address + start, patch[start..end]));
            Thread.Sleep(10);
            end = start;
        }
    }

    private void WriteVerified(int address, byte[] data)
    {
        transport.Send(KatanaProtocol.WriteRequest(RequireIdentity(), address, data));
        if (!ReadBlock(address, data.Length).SequenceEqual(data))
        {
            throw new IOException("Parameter write was not confirmed; refresh before continuing.");
        }
    }

    private void InEditorMode(Action action)
    {
        var address = (0x7F << 21) | 1;
        try
        {
            transport.Send(KatanaProtocol.WriteRequest(RequireIdentity(), address, [1]));
            Thread.Sleep(100);
            action();
        }
        finally
        {
            transport.Send(KatanaProtocol.WriteRequest(RequireIdentity(), address, [0]));
        }
    }

    private byte RequireIdentity() => deviceId ?? throw new InvalidOperationException("Identify the amp before accessing parameters.");
}

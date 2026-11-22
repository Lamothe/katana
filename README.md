# Katana Artist local editor

Katana includes an Electron desktop app, a browser editor, and a CLI.

A local-first .NET 10 + Vue/TypeScript editor for an **original-generation BOSS Katana Artist** on Linux. USB MIDI uses ALSA directly; there is no cloud service or browser MIDI dependency.

## Requirements

- .NET 10 SDK, Node.js 22.12+ (or Node.js 24), npm.
- Linux ALSA (`libasound.so.2`), access to the amp MIDI device.
- Close Tone Studio, other MIDI editors and this project's CLI before connecting from the web editor.

## Build, test and run

```sh
dotnet build Katana.Web/Katana.Web.csproj
dotnet run --project Katana.Tests -p:SkipUIBuild=true
python Katana.Tests/verify-host.py  # needs ss; isolated host, never connects amp
(cd Katana.Web/UI && npm test)
dotnet run --project Katana.Web --no-build
```

Optional `Katana:Port` selects a different port (default 5025); the listener remains loopback-only.

Open **http://127.0.0.1:5025** and press **Connect amp**. The web app binds to loopback only. Mutations require same-origin JSON requests; LAN access is deliberately not enabled.

Frontend source is nested in `Katana.Web/UI`. `dotnet build` builds and type-checks it into `wwwroot`. First build installs locked npm dependencies if absent. After changing package dependencies run `npm ci` in UI. `dotnet watch --project Katana.Web` watches UI source and rebuilds it. Optional Vite HMR: `npm run dev` inside UI proxies `/api` to port 5025.

Publish:

```sh
dotnet publish Katana.Web/Katana.Web.csproj -c Release -o artifacts/editor
(cd artifacts/editor && dotnet Katana.Web.dll)
```

## Electron desktop app

From this directory, install Electron and publish the editor:

```sh
cd Katana.Desktop
npm ci
npm run setup:electron
npm run build
npm start
```

The desktop window starts the .NET 10 Katana backend automatically. It uses the
same default local preset directory as the Katana module in Atomic.
Use one editor to connect to the amp at a time. From Rosella, the Katana launcher
opens the published build over Wayland. Run `npm run build` again after changing
the editor or backend source.

## How patches work

- Sliders change the **current editing buffer**, with serialised writes and read-back verification. Leaving the editor discards unsent edits and cancels requests still waiting for the device lock; already-started hardware writes finish verification and are never automatically retried. Channel volume is not physical master volume.
- **Save app preset** captures amp data to a named local library entry. It does not write flash or overwrite an existing library entry.
- **Apply preset** writes changed bytes, verifies the captured range, **overwrites the selected A1–B4 slot**, verifies stored bytes and recalls that slot.
- **Save working sound** saves to the selected A1–B4 slot. Panel can be recalled, but not overwritten. Recall/save require in-app confirmation. Flash is not written on every slider movement.
- App names can be renamed independently of the 16-character amp patch name. Presets can be deleted after confirmation.
- While connected, visible and idle, a read-only live snapshot poll runs every 2 seconds. Controls, effect enable/type, patch name, USB levels and the reported slot update without pressing Refresh. Monitoring uses RQ1 requests only, never editor-mode toggles, patch recall, saves or automatic reconnect. In-flight snapshots are ignored after user actions; polling pauses during active input, writes and saves. Power-off/unplug disables controls and shows offline after the next check (allow a few seconds for MIDI/HTTP timeout). Turn the amp back on and select **Connect amp**; no automatic patch writes or reconnects occur.
- Physical control/footswitch changes reconcile on the next completed poll, not instant unsolicited MIDI events. A slot switch detected during snapshot transfer discards that snapshot. Firmware which times out on passive slot reads continues updating controls with an unknown slot until reconnect. Passive slot support and live hardware behaviour still need verification on the original Artist. Do not adjust physical controls during preset capture/apply/save.
- Disconnects or verification failures stop editing; reconnect before continuing. Failed apply may leave partial changes. There is no promise of rollback when hardware is disconnected.

### Patch-independent listening level

The shared runtime retains an optional system Global EQ attenuation API (−20 to 0 dB),
but the editor does not expose a Listening level control. This API is not a remote physical MASTER knob or mute. Channel volume, patch master/foot volume and USB
levels are not substitutes for speaker master volume. The physical MASTER and POWER CONTROL
remain the maximum-loudness controls. Patches can still differ in loudness; this applies a common
attenuation rather than loudness normalisation.

Original MkI mapping: `00 00 04 32` EQ switch, `00 00 04 3D` LEVEL (raw 0–40,
−20…+20 dB; the attenuation API never requests boost), `00 00 04 3E` position (0 input, 1 output).
Evidence: [original FloorBoard system page](https://svn.code.sf.net/p/fxfloorboard/svn/branches/Katana/Katana/menuPage_system.cpp)
and [parameter table](https://svn.code.sf.net/p/fxfloorboard/svn/branches/Katana/Katana/midi.xml),
System / page `04` / parameters `32`, `3D`, `3E`. System settings are automatically stored by
the amp, independent of the `60 00` patch snapshots, apply and A1 commit. No app-side scaling,
restoration on patch load, or extra preset writes are involved.

Connection/refresh only read this optional capability. Unsupported responses disable it without
blocking patch editing; unsupported probing is cached until reconnect. Changing listening level
verifies read-back and only touches LEVEL, plus switch/position when explicitly confirmed.
Existing EQ filters are preserved. Enabling or moving a non-flat EQ can change the tone and
potentially other outputs; this is not a transparent hardware master attenuator. The confirmation
warns before setup. Partial/failed writes require recovery; there is no automatic rollback or
volume increase. No hardware write was performed during development. Speaker attenuation,
firmware support and power-cycle persistence still need verification on the user's Artist.

### USB recording outputs

The editor exposes processed and dry USB recording levels (0–200%, in 2% steps); 100% is the normal level and 0% is muted. These are persistent amp-wide settings, independent of tone presets and physical speaker volume. Connect/Refresh reads the current values; changes write only the selected byte and verify read-back. Unsupported firmware leaves the controls unavailable without blocking patch editing.

Original Artist mapping: system `00 00 00 52` Effect Out and `00 00 00 57` Dry Out. Raw 0–100 maps to 0–200%, so raw 50 is 100%. See the original FloorBoard `midi.xml` above. Both levels were verified against this Artist hardware when resolving silent USB capture; the editor changes themselves are covered by hardware-free tests. No USB playback/mix or loopback controls are exposed.

### Snapshot coverage

Presets preserve **all 2327 bytes** of the documented original-generation `60 00` patch range, including effect settings not yet exposed in the UI. Current Artist hardware returns this entire range in ten addressed packets. Missing bytes, conflicting packets or invalid checksums fail capture instead of saving an incomplete preset.

This profile (`original-2327-v1`) is **not a guarantee of all firmware-specific Artist settings outside that range**. System-wide amp settings and USB audio routing are not captured. Presets retain identity/firmware bytes; applying a different identity is blocked. There is no Tone Studio `.tsl` import/export yet. Effects expose documented type selectors and Booster, Delay and Reverb controls. Modulation and FX have algorithm-specific editors for Compressor, Graphic EQ, Slow Gear, Phaser, Flanger, Tremolo, Uni-V and Vibrato; other algorithms retain their existing settings and show a details-unavailable message. Signed tone/EQ and reverb seconds are display conversions only: snapshots keep raw bytes. Conditional writes verify the current algorithm before editing, and changing type never initialises defaults. These additions are verified with fake MIDI transports, not yet on this Artist hardware. Amp types include documented original DSP types (including Clean Twin); later unknown values are preserved and displayed rather than silently changed.

### Storage

Default: `~/.local/share/Katana/presets` (platform local application data folder). One versioned JSON file per preset UUID; raw bytes are base64. Writes use a temporary file, flush file contents, atomically rename and fsync the containing Linux directory. Rename/delete synchronise directory metadata too. Back up this folder to protect against deletion or disk failure.

Configuration overrides:

```sh
Katana__MidiPort=hw:KATANA,0,0 Katana__PresetDirectory=/path/to/presets dotnet run --project Katana.Web --no-build
```

## CLI hardware milestone

```sh
dotnet build Katana.Cli/Katana.Cli.csproj
dotnet run --project Katana.Cli -- identity
dotnet run --project Katana.Cli -- gain
dotnet run --project Katana.Cli -- test-gain
```

The CLI accepts a second argument for the ALSA port. `test-gain` decreases gain by one (or increases zero to one), verifies it, then restores and verifies the original in a `finally` block. It does not save a patch. Killing the process or disconnecting the amp can prevent restoration.

## Protocol sources

- https://github.com/katana-dev/docs (CC-BY-SA-4.0 documentation; amp types and TSL parameter map)
- https://github.com/katana-dev/lib-katana (GPL-3.0 reference; original patch range)
- https://github.com/sandrain/katana-editor/blob/master/floorBoardDisplay.cpp (`writeToMemory`: direct stored-patch writes; verified on this Artist)
- https://github.com/snhirsch/katana-midi-bridge/blob/2125ce90a2fb937b554427f0c993444191eb266e/doc/katana_sysex.txt (historical save and original recall/editor-mode sequence; the historical save command is not effective on this Artist firmware)

The protocol implementation is independently written. Roland manufacturer `41`, model `00 00 00 33`, RQ1 `11`, DT1 `12`, 7-bit checksum. USB ID `0582:01d8`, ALSA `hw:KATANA,0,0`; identity observed on 02/10/2026: `F0 7E 00 06 02 41 33 03 00 00 04 01 00 00 F7`.

## Slot selection

Recall accepts Panel (0), A1–A4 (1–4) and B1–B4 (5–8). Commit/apply accept only 1–8 and use direct stored-patch addresses `10 ss 00 00`. Stored bytes and recalled working data are verified. Selecting a destination writes nothing; UI operations require confirmation. Legacy API routes retain their A1 defaults for compatibility.

New effect and slot controls still require verification on actual original Artist hardware. Remote Master knob, `.tsl` import/export are not implemented; automatic physical-change synchronisation uses read-only polling rather than unsolicited MIDI events; full firmware/Tone Studio parity is not claimed.

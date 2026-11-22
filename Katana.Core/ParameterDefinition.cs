namespace Katana.Core;

public sealed record ParameterOption(int Value, string Label);
public sealed record ParameterDefinition(string Id, string Label, string Group, int Offset, int Size, int Min, int Max, string Unit, ParameterOption[]? Options = null, double DisplayOffset = 0, double DisplayScale = 1, string? DependsOn = null, int? DependsOnValue = null)
{
    // Display values are (raw + DisplayOffset) * DisplayScale; Decode/Encode remain raw.
    // DependsOnValue must match the raw selector identified by DependsOn.
    public int Decode(byte[] patch) => Size == 1 ? patch[Offset] : patch[Offset] * 128 + patch[Offset + 1];
    public byte[] Encode(int value)
    {
        if (value < Min || value > Max || (Options is not null && !Options.Any(option => option.Value == value)))
        {
            throw new ArgumentException($"Invalid value for {Label}.");
        }
        return Size == 1 ? [(byte)value] : [(byte)(value >> 7), (byte)(value & 127)];
    }
}

public static class Parameters
{
    // Original-generation protocol metadata, corroborated against:
    // https://raw.githubusercontent.com/katana-dev/docs/ed018f335bbb16a50afd62357b9423e69efda2cf/data/tsl-map-1.0.0.csv
    // https://raw.githubusercontent.com/snhirsch/katana-midi-bridge/2125ce90a2fb937b554427f0c993444191eb266e/doc/katana_sysex.txt
    // https://svn.code.sf.net/p/fxfloorboard/svn/branches/Katana/Katana/midi.xml
    // Original MkI FloorBoard table supplies enumerations and corroborates TSL addresses.
    // Where Hirsch has typos (T Wah polarity/direct mix, Chorus direct mix), use TSL + FloorBoard.
    // Offsets are linear base-128 offsets, not hexadecimal address byte pairs.
    private static readonly ParameterOption[] EffectTypes = [
        new(0, "T Wah"), new(1, "Auto Wah"), new(2, "Pedal Wah"), new(3, "Compressor"), new(4, "Limiter"),
        new(6, "Graphic EQ"), new(7, "Parametric EQ"), new(9, "Guitar Simulator"), new(10, "Slow Gear"),
        new(12, "Wave Synth"), new(14, "Octave"), new(15, "Pitch Shifter"), new(16, "Harmonist"),
        new(18, "Acoustic Processor"), new(19, "Phaser"), new(20, "Flanger"), new(21, "Tremolo"),
        new(22, "Rotary"), new(23, "Uni-V"), new(25, "Slicer"), new(26, "Vibrato"), new(27, "Ring Mod"),
        new(28, "Humanizer"), new(29, "Chorus"), new(31, "Acoustic Guitar Simulator")
    ];
    private static readonly ParameterOption[] HighCut = [
        new(0, "630 Hz"), new(1, "800 Hz"), new(2, "1000 Hz"), new(3, "1250 Hz"), new(4, "1600 Hz"),
        new(5, "2000 Hz"), new(6, "2500 Hz"), new(7, "3150 Hz"), new(8, "4000 Hz"), new(9, "5000 Hz"),
        new(10, "6300 Hz"), new(11, "8000 Hz"), new(12, "10000 Hz"), new(13, "12500 Hz"), new(14, "Flat")
    ];
    private static readonly ParameterOption[] LowCut = [
        new(0, "Flat"), new(1, "20 Hz"), new(2, "25 Hz"), new(3, "31.5 Hz"), new(4, "40 Hz"), new(5, "50 Hz"),
        new(6, "63 Hz"), new(7, "80 Hz"), new(8, "100 Hz"), new(9, "125 Hz"), new(10, "160 Hz"),
        new(11, "200 Hz"), new(12, "250 Hz"), new(13, "315 Hz"), new(14, "400 Hz"), new(15, "500 Hz"),
        new(16, "630 Hz"), new(17, "800 Hz")
    ];

    private static ParameterOption[] Choices(params string[] labels) =>
        labels.Select((label, value) => new ParameterOption(value, label)).ToArray();

    private static readonly ParameterOption[] EqFrequency = Choices(
        "20 Hz", "25 Hz", "31.5 Hz", "40 Hz", "50 Hz", "63 Hz", "80 Hz", "100 Hz",
        "125 Hz", "160 Hz", "200 Hz", "250 Hz", "315 Hz", "400 Hz", "500 Hz", "630 Hz",
        "800 Hz", "1000 Hz", "1250 Hz", "1600 Hz", "2000 Hz", "2500 Hz", "3150 Hz",
        "4000 Hz", "5000 Hz", "6300 Hz", "8000 Hz", "10000 Hz");
    private static readonly ParameterOption[] EqQ = Choices("0.5", "1", "2", "4", "8", "16");
    private static readonly ParameterOption[] Harmony = Choices(
        "−2 oct", "−14th", "−13th", "−12th", "−11th", "−10th", "−9th", "−1 oct",
        "−7th", "−6th", "−5th", "−4th", "−3rd", "−2nd", "Unison", "+2nd", "+3rd",
        "+4th", "+5th", "+6th", "+7th", "+1 oct", "+9th", "+10th", "+11th", "+12th",
        "+13th", "+14th", "+2 oct", "User");

    public static IReadOnlyList<ParameterDefinition> All { get; } = Array.AsReadOnly<ParameterDefinition>([
        new("ampType", "Amp type", "Amplifier", 0x51, 1, 0, 127, "", [new(1, "Acoustic"), new(8, "Clean · JC-120"), new(11, "Crunch · Tweed"), new(24, "Lead · 5150"), new(23, "Brown · SLDN"), new(0, "Natural Clean"), new(9, "Clean Twin"), new(2, "Combo Crunch"), new(3, "Stack Crunch"), new(10, "Pro Crunch"), new(12, "Deluxe Crunch"), new(13, "VO Drive"), new(14, "VO Lead"), new(15, "Match Drive"), new(4, "HiGain Stack"), new(6, "Extreme Lead"), new(7, "Core Metal"), new(16, "BG Lead"), new(17, "BG Drive"), new(18, "MS1959 I"), new(19, "MS1959 I+II"), new(20, "R-Fier Vintage"), new(21, "R-Fier Modern"), new(22, "T-Amp Lead"), new(26, "BGNR UB"), new(5, "Power Drive"), new(27, "ORNG RB"), new(25, "Custom")]),
        new("gain", "Gain", "Amplifier", 0x52, 1, 0, 100, ""),
        new("volume", "Channel volume", "Amplifier", 0x58, 1, 0, 100, ""),
        new("bass", "Bass", "Amplifier", 0x54, 1, 0, 100, ""),
        new("middle", "Middle", "Amplifier", 0x55, 1, 0, 100, ""),
        new("treble", "Treble", "Amplifier", 0x56, 1, 0, 100, ""),
        new("presence", "Presence", "Amplifier", 0x57, 1, 0, 100, ""),
        new("boostEnabled", "Enabled", "Booster", 0x30, 1, 0, 1, "", [new(0, "Off"), new(1, "On")]),
        new("boostType", "Type", "Booster", 49, 1, 0, 127, "", [
            new(0, "Mid Boost"), new(1, "Clean Boost"), new(2, "Treble Boost"), new(3, "Crunch OD"),
            new(4, "Natural OD"), new(5, "Warm OD"), new(6, "Fat DS"), new(8, "Metal DS"), new(9, "Oct Fuzz"),
            new(10, "Blues Drive"), new(11, "Overdrive"), new(12, "T-Scream"), new(13, "Turbo OD"),
            new(14, "Distortion"), new(15, "RAT"), new(16, "Guv DS"), new(17, "DST+"), new(18, "Metal Zone"),
            new(19, "'60s Fuzz"), new(20, "Muff Fuzz")]),
        new("boostBottom", "Bottom", "Booster", 51, 1, 0, 100, "", DisplayOffset: -50),
        new("boostSoloEnabled", "Solo enabled", "Booster", 53, 1, 0, 1, "", [new(0, "Off"), new(1, "On")]),
        new("boostSoloLevel", "Solo level", "Booster", 54, 1, 0, 100, ""),
        new("boostDirectMix", "Direct mix", "Booster", 56, 1, 0, 100, ""),
        new("boostDrive", "Drive", "Booster", 0x32, 1, 0, 100, ""),
        new("boostTone", "Tone", "Booster", 0x34, 1, 0, 100, "", DisplayOffset: -50),
        new("boostLevel", "Level", "Booster", 0x37, 1, 0, 100, ""),
        new("modEnabled", "Enabled", "Modulation", 192, 1, 0, 1, "", [new(0, "Off"), new(1, "On")]),
        new("modType", "Type", "Modulation", 193, 1, 0, 127, "", EffectTypes),
        .. EffectPanel("mod", "Modulation", 0),
        new("fxEnabled", "Enabled", "FX", 460, 1, 0, 1, "", [new(0, "Off"), new(1, "On")]),
        new("fxType", "Type", "FX", 461, 1, 0, 127, "", EffectTypes),
        .. EffectPanel("fx", "FX", 268),
        new("delayEnabled", "Enabled", "Delay", 736, 1, 0, 1, "", [new(0, "Off"), new(1, "On")]),
        new("delayType", "Type", "Delay", 737, 1, 0, 127, "", [new(0, "Digital"), new(6, "Reverse"), new(7, "Analogue"), new(8, "Tape Echo"), new(9, "Modulate")]),
        new("delayHighCut", "High cut", "Delay", 741, 1, 0, 14, "", HighCut),
        new("delayDirectMix", "Direct mix", "Delay", 743, 1, 0, 100, ""),
        new("delayTime", "Time", "Delay", 738, 2, 1, 2000, "ms"),
        new("delayFeedback", "Feedback", "Delay", 740, 1, 0, 100, ""),
        new("delayLevel", "Level", "Delay", 742, 1, 0, 120, ""),
        new("reverbEnabled", "Enabled", "Reverb", 784, 1, 0, 1, "", [new(0, "Off"), new(1, "On")]),
        new("reverbType", "Type", "Reverb", 785, 1, 0, 127, "", [new(1, "Room"), new(3, "Hall"), new(4, "Plate"), new(5, "Spring"), new(6, "Modulate")]),
        new("reverbTime", "Time", "Reverb", 786, 1, 0, 99, "s", DisplayOffset: 1, DisplayScale: 0.1),
        new("reverbPreDelay", "Pre-delay", "Reverb", 787, 2, 0, 500, "ms"),
        new("reverbLowCut", "Low cut", "Reverb", 789, 1, 0, 17, "", LowCut),
        new("reverbHighCut", "High cut", "Reverb", 790, 1, 0, 14, "", HighCut),
        new("reverbDensity", "Density", "Reverb", 791, 1, 0, 10, ""),
        new("reverbDirectMix", "Direct mix", "Reverb", 793, 1, 0, 100, ""),
        new("reverbSpringSensitivity", "Spring sensitivity", "Reverb", 794, 1, 0, 100, "", DependsOn: "reverbType", DependsOnValue: 5),
        new("reverbLevel", "Level", "Reverb", 792, 1, 0, 100, ""),
        new("gateEnabled", "Enabled", "Noise gate", 867, 1, 0, 1, "", [new(0, "Off"), new(1, "On")]),
        new("gateThreshold", "Threshold", "Noise gate", 868, 1, 0, 100, ""),
        new("gateRelease", "Release", "Noise gate", 869, 1, 0, 100, ""),
        // Both Harmonists use this shared key; it is not part of either mirrored block.
        new("harmonistKey", "Harmonist key (shared Mod/FX)", "Routing", 920, 1, 0, 11, "", Choices(
            "C (Am)", "C♯ (A♯m)", "D (Bm)", "D♯ (Cm)", "E (C♯m)", "F (Dm)",
            "F♯ (D♯m)", "G (Em)", "G♯ (Fm)", "A (F♯m)", "A♯ (Gm)", "B (G♯m)")),
        new("chain", "Effect chain", "Routing", 2304, 1, 0, 2, "", [new(0, "Chain 1"), new(1, "Chain 2"), new(2, "Chain 3")])
    ]);

    // FX repeats the main Mod block at +268, but the extension AC Sim block at +15.
    // Dependencies intentionally guard the primary algorithm only. Subtype-only controls
    // edit retained settings (voice 2, User harmony, Humanizer modes, Guitar Sim body);
    // their labels indicate applicability, and selecting types never initialises defaults.
    private static IEnumerable<ParameterDefinition> EffectPanel(string prefix, string group, int displacement)
    {
        ParameterDefinition Control(string suffix, string label, int offset, int type, int max = 100,
            double bias = 0, string unit = "", ParameterOption[]? options = null, int size = 1,
            double scale = 1) =>
            new(prefix + suffix, label, group, offset + (type == 31 && displacement != 0 ? 15 : displacement), size, 0, max, unit, options,
                DisplayOffset: bias, DisplayScale: scale, DependsOn: prefix + "Type", DependsOnValue: type);

        // 01 4C–01 61: envelope, automatic and pedal wah.
        // FloorBoard identifies BPF (the historical text incorrectly calls it HPF).
        yield return Control("TWahMode", "Filter mode", 204, 0, 1, options: Choices("LPF", "BPF"));
        yield return Control("TWahPolarity", "Polarity", 205, 0, 1, options: Choices("Down", "Up"));
        yield return Control("TWahSensitivity", "Sensitivity", 206, 0);
        yield return Control("TWahFrequency", "Frequency", 207, 0);
        yield return Control("TWahPeak", "Peak", 208, 0);
        yield return Control("TWahDirectMix", "Direct mix", 209, 0);
        yield return Control("TWahLevel", "Level", 210, 0);
        yield return Control("AutoWahMode", "Filter mode", 212, 1, 1, options: Choices("LPF", "BPF"));
        yield return Control("AutoWahFrequency", "Frequency", 213, 1);
        yield return Control("AutoWahPeak", "Peak", 214, 1);
        yield return Control("AutoWahRate", "Rate", 215, 1);
        yield return Control("AutoWahDepth", "Depth", 216, 1);
        yield return Control("AutoWahDirectMix", "Direct mix", 217, 1);
        yield return Control("AutoWahLevel", "Level", 218, 1);
        yield return Control("PedalWahModel", "Wah model", 220, 2, 5,
            options: Choices("Cry Wah", "Vox Wah", "Fat Wah", "Light Wah", "7 String Wah", "Reso Wah"));
        yield return Control("PedalWahPosition", "Pedal position", 221, 2);
        yield return Control("PedalWahMinimum", "Pedal minimum", 222, 2);
        yield return Control("PedalWahMaximum", "Pedal maximum", 223, 2);
        yield return Control("PedalWahLevel", "Level", 224, 2);
        yield return Control("PedalWahDirectMix", "Direct mix", 225, 2);
        yield return Control("LimiterModel", "Limiter model", 233, 4, 2,
            options: Choices("BOSS Limiter", "Rack 160D", "VTG Rack U"));
        yield return Control("LimiterAttack", "Attack", 234, 4);
        yield return Control("LimiterThreshold", "Threshold", 235, 4);
        yield return Control("LimiterRatio", "Ratio", 236, 4, 17, options: Choices(
            "1:1", "1.2:1", "1.4:1", "1.6:1", "1.8:1", "2:1", "2.3:1", "2.6:1", "3:1",
            "3.5:1", "4:1", "5:1", "6:1", "8:1", "10:1", "12:1", "20:1", "∞:1"));
        yield return Control("LimiterRelease", "Release", 237, 4);
        yield return Control("LimiterLevel", "Level", 238, 4);
        yield return Control("ParametricEqLowCut", "Low cut", 252, 7, 17, options: LowCut);
        yield return Control("ParametricEqLowGain", "Low gain", 253, 7, 40, -20, "dB");
        yield return Control("ParametricEqLowMidFrequency", "Low-mid frequency", 254, 7, 27, options: EqFrequency);
        yield return Control("ParametricEqLowMidQ", "Low-mid Q", 255, 7, 5, options: EqQ);
        yield return Control("ParametricEqLowMidGain", "Low-mid gain", 256, 7, 40, -20, "dB");
        yield return Control("ParametricEqHighMidFrequency", "High-mid frequency", 257, 7, 27, options: EqFrequency);
        yield return Control("ParametricEqHighMidQ", "High-mid Q", 258, 7, 5, options: EqQ);
        yield return Control("ParametricEqHighMidGain", "High-mid gain", 259, 7, 40, -20, "dB");
        yield return Control("ParametricEqHighGain", "High gain", 260, 7, 40, -20, "dB");
        yield return Control("ParametricEqHighCut", "High cut", 261, 7, 14, options: HighCut);
        yield return Control("ParametricEqLevel", "Level", 262, 7, 40, -20, "dB");
        yield return Control("GuitarSimulatorModel", "Guitar simulation", 270, 9, 7, options: Choices(
            "S → H", "H → S", "H → HF", "S → Hollow", "H → Hollow", "S → AC", "H → AC", "P → AC"));
        yield return Control("GuitarSimulatorLow", "Low", 271, 9, bias: -50);
        yield return Control("GuitarSimulatorHigh", "High", 272, 9, bias: -50);
        yield return Control("GuitarSimulatorLevel", "Level", 273, 9);
        yield return Control("GuitarSimulatorBody", "Body (not S → H / H → S)", 274, 9);
        yield return Control("WaveSynthWave", "Wave", 288, 12, 1, options: Choices("Saw", "Square"));
        yield return Control("WaveSynthCutoff", "Cutoff", 289, 12);
        yield return Control("WaveSynthResonance", "Resonance", 290, 12);
        yield return Control("WaveSynthFilterSensitivity", "Filter sensitivity", 291, 12);
        yield return Control("WaveSynthFilterDecay", "Filter decay", 292, 12);
        yield return Control("WaveSynthFilterDepth", "Filter depth", 293, 12);
        yield return Control("WaveSynthLevel", "Synth level", 294, 12);
        yield return Control("WaveSynthDirectMix", "Direct mix", 295, 12);
        yield return Control("OctaveRange", "Range", 305, 14, 3, options: Choices("1", "2", "3", "4"));
        yield return Control("OctaveLevel", "Octave level", 306, 14);
        yield return Control("OctaveDirectMix", "Direct mix", 307, 14);
        yield return Control("PitchShifterVoices", "Voices", 309, 15, 1, options: Choices("1 Voice", "2 Voices"));
        for (var voice = 1; voice <= 2; voice++)
        {
            var offset = voice == 1 ? 310 : 316;
            var label = voice == 1 ? "Voice 1 " : "Voice 2 (2 Voices only) ";
            yield return Control("PitchShifterVoice" + voice + "Mode", label + "mode", offset, 15, 3,
                options: Choices("Fast", "Medium", "Slow", "Mono"));
            yield return Control("PitchShifterVoice" + voice + "Pitch", label + "pitch", offset + 1, 15, 48, -24, "semitones");
            yield return Control("PitchShifterVoice" + voice + "Fine", label + "fine", offset + 2, 15, bias: -50, unit: "cents");
            yield return Control("PitchShifterVoice" + voice + "PreDelay", label + "pre-delay", offset + 3, 15, 300, unit: "ms", size: 2);
            yield return Control("PitchShifterVoice" + voice + "Level", label + "level", offset + 5, 15);
        }
        yield return Control("PitchShifterFeedback", "Voice 1 feedback", 322, 15);
        yield return Control("PitchShifterDirectMix", "Direct mix", 323, 15);
        yield return Control("HarmonistVoices", "Voices", 325, 16, 1, options: Choices("1 Voice", "2 Voices"));
        var notes = new[] { "C", "D♭", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B" };
        for (var voice = 1; voice <= 2; voice++)
        {
            var offset = voice == 1 ? 326 : 330;
            var label = voice == 1 ? "Voice 1 " : "Voice 2 (2 Voices only) ";
            yield return Control("HarmonistVoice" + voice + "Harmony", label + "harmony", offset, 16, 29, options: Harmony);
            yield return Control("HarmonistVoice" + voice + "PreDelay", label + "pre-delay", offset + 1, 16, 300, unit: "ms", size: 2);
            yield return Control("HarmonistVoice" + voice + "Level", label + "level", offset + 3, 16);
            for (var note = 0; note < notes.Length; note++)
            {
                yield return Control("HarmonistVoice" + voice + "UserNote" + note,
                    label + notes[note] + " (User harmony)", 336 + (voice - 1) * 12 + note, 16, 48, -24, "semitones");
            }
        }
        yield return Control("HarmonistFeedback", "Voice 1 feedback", 334, 16);
        yield return Control("HarmonistDirectMix", "Direct mix", 335, 16);
        yield return Control("AcousticProcessorModel", "Acoustic processor model", 365, 18, 3,
            options: Choices("Small", "Medium", "Bright", "Power"));
        yield return Control("AcousticProcessorBass", "Bass", 366, 18, bias: -50);
        yield return Control("AcousticProcessorMiddle", "Middle", 367, 18, bias: -50);
        yield return Control("AcousticProcessorMiddleFrequency", "Middle frequency", 368, 18, 27, options: EqFrequency);
        yield return Control("AcousticProcessorTreble", "Treble", 369, 18, bias: -50);
        yield return Control("AcousticProcessorPresence", "Presence", 370, 18, bias: -50);
        yield return Control("AcousticProcessorLevel", "Level", 371, 18);
        yield return Control("RotarySpeed", "Speed", 396, 22, 1, options: Choices("Slow", "Fast"));
        yield return Control("RotarySlowRate", "Slow rate", 397, 22);
        yield return Control("RotaryFastRate", "Fast rate", 398, 22);
        yield return Control("RotaryRise", "Rise time", 399, 22);
        yield return Control("RotaryFall", "Fall time", 400, 22);
        yield return Control("RotaryDepth", "Depth", 401, 22);
        yield return Control("RotaryLevel", "Level", 402, 22);
        yield return Control("SlicerPattern", "Pattern", 415, 25, 19,
            options: Enumerable.Range(1, 20).Select(value => new ParameterOption(value - 1, "P" + value)).ToArray());
        yield return Control("SlicerRate", "Rate", 416, 25);
        yield return Control("SlicerTriggerSensitivity", "Trigger sensitivity", 417, 25);
        yield return Control("SlicerLevel", "Level", 418, 25);
        yield return Control("SlicerDirectMix", "Direct mix", 419, 25);
        yield return Control("RingModMode", "Mode", 427, 27, 1, options: Choices("Normal", "Intelligent"));
        yield return Control("RingModFrequency", "Frequency", 428, 27);
        yield return Control("RingModLevel", "Level", 429, 27);
        yield return Control("RingModDirectMix", "Direct mix", 430, 27);
        yield return Control("HumanizerMode", "Mode", 432, 28, 1, options: Choices("Picking", "Auto"));
        yield return Control("HumanizerVowel1", "Vowel 1", 433, 28, 4, options: Choices("a", "e", "i", "o", "u"));
        yield return Control("HumanizerVowel2", "Vowel 2", 434, 28, 4, options: Choices("a", "e", "i", "o", "u"));
        yield return Control("HumanizerSensitivity", "Sensitivity (Picking mode)", 435, 28);
        yield return Control("HumanizerRate", "Rate", 436, 28);
        yield return Control("HumanizerDepth", "Depth", 437, 28);
        yield return Control("HumanizerManual", "Manual (Auto mode)", 438, 28);
        yield return Control("HumanizerLevel", "Level", 439, 28);
        yield return Control("ChorusCrossover", "Crossover frequency", 441, 29, 16, options: Choices(
            "100 Hz", "125 Hz", "160 Hz", "200 Hz", "250 Hz", "315 Hz", "400 Hz", "500 Hz", "630 Hz",
            "800 Hz", "1000 Hz", "1250 Hz", "1600 Hz", "2000 Hz", "2500 Hz", "3150 Hz", "4000 Hz"));
        yield return Control("ChorusLowRate", "Low rate", 442, 29);
        yield return Control("ChorusLowDepth", "Low depth", 443, 29);
        yield return Control("ChorusLowPreDelay", "Low pre-delay", 444, 29, 80, unit: "ms", scale: 0.5);
        yield return Control("ChorusLowLevel", "Low level", 445, 29);
        yield return Control("ChorusHighRate", "High rate", 446, 29);
        yield return Control("ChorusHighDepth", "High depth", 447, 29);
        yield return Control("ChorusHighPreDelay", "High pre-delay", 448, 29, 80, unit: "ms", scale: 0.5);
        yield return Control("ChorusHighLevel", "High level", 449, 29);
        yield return Control("ChorusDirectMix", "Direct mix", 450, 29);
        yield return Control("AcousticSimulatorHigh", "High", 2064, 31, bias: -50);
        yield return Control("AcousticSimulatorBody", "Body", 2065, 31);
        yield return Control("AcousticSimulatorLow", "Low", 2066, 31, bias: -50);
        yield return Control("AcousticSimulatorLevel", "Level", 2068, 31);

        yield return Control("CompressorModel", "Compressor model", 227, 3, 6, options: [
            new(0, "BOSS Comp"), new(1, "Hi-Band"), new(2, "Light"), new(3, "D-Comp"),
            new(4, "Orange"), new(5, "Fat"), new(6, "Mild")]);
        yield return Control("CompressorSustain", "Sustain", 228, 3);
        yield return Control("CompressorAttack", "Attack", 229, 3);
        yield return Control("CompressorTone", "Tone", 230, 3, bias: -50);
        yield return Control("CompressorLevel", "Level", 231, 3);
        yield return Control("PhaserSubtype", "Phaser subtype", 373, 19, 3, options: [
            new(0, "4 Stage"), new(1, "8 Stage"), new(2, "12 Stage"), new(3, "Bi-Phase")]);
        yield return Control("PhaserRate", "Rate", 374, 19);
        yield return Control("PhaserDepth", "Depth", 375, 19);
        yield return Control("PhaserManual", "Manual", 376, 19);
        yield return Control("PhaserResonance", "Resonance", 377, 19);
        yield return Control("PhaserStepRate", "Step rate", 378, 19, 101,
            options: Enumerable.Range(0, 102).Select(value => new ParameterOption(value,
                value == 0 ? "Off" : (value - 1).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());
        yield return Control("PhaserLevel", "Level", 379, 19);
        yield return Control("PhaserDirectMix", "Direct mix", 380, 19);
        yield return Control("TremoloWave", "Wave shape", 391, 21);
        yield return Control("TremoloRate", "Rate", 392, 21);
        yield return Control("TremoloDepth", "Depth", 393, 21);
        yield return Control("TremoloLevel", "Level", 394, 21);
        yield return Control("UniVRate", "Rate", 404, 23);
        yield return Control("UniVDepth", "Depth", 405, 23);
        yield return Control("UniVLevel", "Level", 406, 23);
        yield return Control("FlangerRate", "Rate", 382, 20);
        yield return Control("FlangerDepth", "Depth", 383, 20);
        yield return Control("FlangerManual", "Manual", 384, 20);
        yield return Control("FlangerResonance", "Resonance", 385, 20);
        yield return Control("FlangerSeparation", "Separation", 386, 20);
        yield return Control("FlangerLowCut", "Low cut", 387, 20, 10, options: Choices(
            "Flat", "55 Hz", "110 Hz", "165 Hz", "200 Hz", "280 Hz", "340 Hz", "400 Hz", "500 Hz", "630 Hz", "800 Hz"));
        yield return Control("FlangerLevel", "Level", 388, 20);
        yield return Control("FlangerDirectMix", "Direct mix", 389, 20);
        yield return Control("VibratoRate", "Rate", 421, 26);
        yield return Control("VibratoDepth", "Depth", 422, 26);
        yield return Control("VibratoTrigger", "Trigger", 423, 26, 1, options: Choices("Off", "On"));
        yield return Control("VibratoRise", "Rise time", 424, 26);
        yield return Control("VibratoLevel", "Level", 425, 26);
        yield return Control("SlowGearSensitivity", "Sensitivity", 276, 10);
        yield return Control("SlowGearRise", "Rise time", 277, 10);
        yield return Control("SlowGearLevel", "Level", 278, 10);
        var bands = new[] {"31 Hz", "62 Hz", "125 Hz", "250 Hz", "500 Hz", "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz" };
        for (var index = 0; index < bands.Length; index++)
        {
            yield return Control("GraphicEqBand" + (index + 1), bands[index], 240 + index, 6, 40, -20, "dB");
        }
        yield return Control("GraphicEqLevel", "Level", 250, 6, 40, -20, "dB");
    }

    public static ParameterDefinition Find(string id) => All.FirstOrDefault(parameter => parameter.Id == id)
        ?? throw new ArgumentException("Unknown parameter.");
}

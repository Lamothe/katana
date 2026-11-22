using Katana.Core;

namespace Katana.Tests;

internal static class EffectMetadataChecks
{
    // Independent address fixtures from the pinned tsl-map-1.0.0.csv and original
    // FloorBoard midi.xml linked in ParameterDefinition.cs. No hardware is opened.
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var algorithms = new (int Type, int[] Offsets)[]
        {
            (0, [204, 205, 206, 207, 208, 209, 210]),
            (1, [212, 213, 214, 215, 216, 217, 218]),
            (2, [220, 221, 222, 223, 224, 225]),
            (3, [227, 228, 229, 230, 231]),
            (4, [233, 234, 235, 236, 237, 238]),
            (6, Enumerable.Range(240, 11).ToArray()),
            (7, Enumerable.Range(252, 11).ToArray()),
            (9, [270, 271, 272, 273, 274]),
            (10, [276, 277, 278]),
            (12, Enumerable.Range(288, 8).ToArray()),
            (14, [305, 306, 307]),
            (15, [309, 310, 311, 312, 313, 315, 316, 317, 318, 319, 321, 322, 323]),
            (16, new[] { 325, 326, 327, 329, 330, 331, 333, 334, 335 }.Concat(Enumerable.Range(336, 24)).ToArray()),
            (18, Enumerable.Range(365, 7).ToArray()),
            (19, Enumerable.Range(373, 8).ToArray()),
            (20, Enumerable.Range(382, 8).ToArray()),
            (21, [391, 392, 393, 394]),
            (22, Enumerable.Range(396, 7).ToArray()),
            (23, [404, 405, 406]),
            (25, [415, 416, 417, 418, 419]),
            (26, [421, 422, 423, 424, 425]),
            (27, [427, 428, 429, 430]),
            (28, Enumerable.Range(432, 8).ToArray()),
            (29, Enumerable.Range(441, 10).ToArray()),
            (31, [2064, 2065, 2066, 2068])
        };
        foreach (var prefix in new[] { "mod", "fx" })
        {
            check(Parameters.Find(prefix + "Type").Options!.Select(option => option.Value)
                .SequenceEqual(algorithms.Select(algorithm => algorithm.Type)), prefix + " complete original algorithm catalogue");
            foreach (var (type, offsets) in algorithms)
            {
                var displacement = prefix == "mod" ? 0 : type == 31 ? 15 : 268;
                var controls = Parameters.All.Where(parameter => parameter.DependsOn == prefix + "Type" && parameter.DependsOnValue == type).ToArray();
                check(controls.Select(parameter => parameter.Offset).Order().SequenceEqual(offsets.Select(offset => offset + displacement).Order()),
                    prefix + " algorithm " + type + " exact documented addresses");
                foreach (var parameter in controls)
                {
                    var modOffset = parameter.Offset - displacement;
                    var expectedSize = modOffset is 313 or 319 or 327 or 331 ? 2 : 1;
                    check(parameter.Size == expectedSize, parameter.Id + " documented byte width");
                    foreach (var raw in new[] { parameter.Min, parameter.Max })
                    {
                        var patch = new byte[PatchProfile.Length];
                        var bytes = parameter.Encode(raw);
                        bytes.CopyTo(patch, parameter.Offset);
                        check(bytes.All(value => value < 128) && parameter.Decode(patch) == raw, parameter.Id + " raw endpoint round-trip " + raw);
                    }
                    reject(() => parameter.Encode(parameter.Min - 1), parameter.Id + " below minimum rejected");
                    reject(() => parameter.Encode(parameter.Max + 1), parameter.Id + " above maximum rejected");
                    if (parameter.Options is not null)
                    {
                        check(parameter.Options.Select(option => option.Value).SequenceEqual(Enumerable.Range(parameter.Min, parameter.Max - parameter.Min + 1)),
                            parameter.Id + " complete enumeration");
                    }
                    var fx = Parameters.Find("fx" + parameter.Id[prefix.Length..]);
                    var mod = Parameters.Find("mod" + parameter.Id[prefix.Length..]);
                    check(fx.Offset - mod.Offset == (type == 31 ? 15 : 268) &&
                        fx.Size == mod.Size && fx.Min == mod.Min && fx.Max == mod.Max &&
                        fx.DisplayScale == mod.DisplayScale && fx.DisplayOffset == mod.DisplayOffset &&
                        (fx.Options ?? []).SequenceEqual(mod.Options ?? []), parameter.Id + " mirrored raw/display metadata");
                }
            }
            foreach (var suffix in new[] { "PitchShifterVoice1PreDelay", "PitchShifterVoice2PreDelay", "HarmonistVoice1PreDelay", "HarmonistVoice2PreDelay" })
            {
                var parameter = Parameters.Find(prefix + suffix);
                check(parameter.Max == 300 && parameter.Size == 2 && parameter.Unit == "ms" &&
                    parameter.Encode(300).SequenceEqual(new byte[] { 2, 44 }), parameter.Id + " 300 ms base-128 encoding");
            }
            foreach (var suffix in new[] { "ChorusLowPreDelay", "ChorusHighPreDelay" })
            {
                var parameter = Parameters.Find(prefix + suffix);
                check(parameter.Max == 80 && parameter.Size == 1 && parameter.DisplayScale == 0.5 && parameter.Unit == "ms",
                    parameter.Id + " 0–40 ms in 0.5 ms steps");
            }
            check(Parameters.Find(prefix + "LimiterRatio").Options![17].Label == "∞:1", prefix + " limiter infinite ratio");
            check(Parameters.Find(prefix + "PhaserStepRate").Options![0].Label == "Off" &&
                Parameters.Find(prefix + "PhaserStepRate").Options![101].Label == "100", prefix + " phaser off and 0–100 steps");
            check(Parameters.Find(prefix + "HarmonistVoice1Harmony").Options![14].Label == "Unison" &&
                Parameters.Find(prefix + "HarmonistVoice1Harmony").Options![29].Label == "User", prefix + " harmony enumeration");
            check(Parameters.Find(prefix + "TWahMode").Options![1].Label == "BPF" &&
                Parameters.Find(prefix + "AutoWahMode").Options![1].Label == "BPF", prefix + " wah band-pass mode from original FloorBoard");
        }
        var key = Parameters.Find("harmonistKey");
        check(key.Offset == 920 && key.Options!.Length == 12 && key.Max == 11 && key.DependsOn is null,
            "shared Harmonist key at 07 18, outside mirrored algorithm blocks");
    }
}

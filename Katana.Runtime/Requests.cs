namespace Katana.Web;

public sealed record ParameterChange(int? Value);
public sealed record PresetName(string Name);

public sealed record ListeningLevelChange(int? Decibels, bool ConfigureOutputEq = false);

public sealed record UsbAudioLevelChange(int? Percent);

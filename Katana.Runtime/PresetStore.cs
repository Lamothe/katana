using System.Text.Json;
using Katana.Core;

namespace Katana.Web;

public sealed record PresetSummary(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record PresetDocument(int Version, string Profile, string Identity, PresetSummary Summary, byte[] Patch);

public sealed class PresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string directory;
    private readonly object gate = new();

    public PresetStore(string directory)
    {
        this.directory = directory;
        DirectoryDurability.Create(directory);
    }

    public PresetSummary[] List()
    {
        lock (gate)
        {
            return Directory.EnumerateFiles(directory, "*.json").Select(file => Read(Guid.Parse(Path.GetFileNameWithoutExtension(file))).Summary)
                .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public PresetDocument Read(Guid id)
    {
        lock (gate)
        {
            var file = FileName(id);
            if (!File.Exists(file))
            {
                throw new KeyNotFoundException("Preset not found.");
            }
            var document = JsonSerializer.Deserialize<PresetDocument>(File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidDataException("Empty preset document.");
            if (document.Version != 1 || document.Profile != PatchProfile.Id || document.Summary is null || document.Summary.Id != id
                || document.Patch is null || string.IsNullOrWhiteSpace(document.Identity))
            {
                throw new InvalidDataException("Incompatible preset document.");
            }
            KatanaDevice.ValidatePatch(document.Patch);
            ValidateName(document.Summary.Name);
            return document;
        }
    }

    public PresetSummary Create(string name, string identity, byte[] patch)
    {
        lock (gate)
        {
            name = ValidateName(name);
            KatanaDevice.ValidatePatch(patch);
            var now = DateTimeOffset.UtcNow;
            var summary = new PresetSummary(Guid.NewGuid(), name, now, now);
            Write(new(1, PatchProfile.Id, identity, summary, patch));
            return summary;
        }
    }

    public PresetSummary Rename(Guid id, string name)
    {
        lock (gate)
        {
            name = ValidateName(name);
            var document = Read(id);
            var summary = document.Summary with { Name = name, UpdatedAt = DateTimeOffset.UtcNow };
            Write(document with { Summary = summary });
            return summary;
        }
    }

    public void Delete(Guid id)
    {
        lock (gate)
        {
            Read(id);
            File.Delete(FileName(id));
            DirectoryDurability.Sync(directory);
        }
    }

    public static string ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Preset names must contain 1–80 characters and no control characters.");
        }
        return name.Trim();
    }

    private string FileName(Guid id) => Path.Combine(directory, $"{id:D}.json");

    private void Write(PresetDocument document)
    {
        var destination = FileName(document.Summary.Id);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
            DirectoryDurability.Sync(directory);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}

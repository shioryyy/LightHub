using System.Text.Json;
using LightHub.Core;

namespace LightHub.Application;

public sealed record PresetBinding(string Control, byte[] Action);
public sealed record LocalPreset(int SchemaVersion, string Id, string Name, string ModelId, int Rate, int[] Dpi, int DefaultIndex, int ShiftIndex, PresetBinding[] Bindings)
{
    public override string ToString() => Name;
}



public sealed class LocalAssets(string root)
{
    private readonly object fileGate = new();
    private static readonly string[] GpwControls = ["left", "right", "wheel", "left-rear", "left-front", "underside", "right-rear", "right-front"];
    private string DirectoryPath => Path.Combine(root, "presets");
    public static LocalPreset FromProfile(string name, string modelId, MouseProfile profile) => new(1, Guid.NewGuid().ToString("N"), name, modelId,
        profile.Rate, (int[])profile.Dpi.Clone(), profile.DefaultIndex, profile.ShiftIndex,
        profile.Bindings.Select((b, i) => new PresetBinding(modelId == "g-pro-wireless" && i < GpwControls.Length ? GpwControls[i] : "unmapped-" + i, (byte[])b.Clone())).ToArray());
    public static void Validate(LocalPreset preset)
    {
        if (preset.SchemaVersion != 1 || !Guid.TryParseExact(preset.Id, "N", out _) || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 120 ||
            string.IsNullOrWhiteSpace(preset.ModelId) || preset.Rate is not (125 or 250 or 500 or 1000) || preset.Dpi is not { Length: 5 } || preset.Bindings is null || preset.Bindings.Length is < 1 or > 16 ||
            preset.Bindings.Any(b => b is null || string.IsNullOrWhiteSpace(b.Control) || b.Action is not { Length: 4 }) || preset.Bindings.Select(b => b.Control).Distinct().Count() != preset.Bindings.Length)
            throw new InvalidDataException("Invalid or unsupported preset schema.");
        if (preset.Dpi.Any(d => d < 0 || d > 65535) || preset.DefaultIndex is < 0 or > 4 || preset.ShiftIndex is not (255 or >= 0 and <= 4)) throw new InvalidDataException("Invalid preset values.");
        if (preset.Bindings.Any(b => !MouseProfile.IsStandardBinding(b.Action))) throw new InvalidDataException("This preset contains non-portable macro pointers or unknown actions. Use a same-device backup to preserve them.");
    }
    public static MouseProfile Map(LocalPreset preset, string targetModel, MouseProfile target)
    {
        Validate(preset);
        if (targetModel != "g-pro-wireless" || preset.ModelId != targetModel || target.Bindings.Length != 8)
            throw new InvalidDataException("No verified physical-control mapping for this preset and target. Nothing was imported.");
        if (preset.Bindings.Length != GpwControls.Length || preset.Bindings.Any(b => !GpwControls.Contains(b.Control)))
            throw new InvalidDataException("Preset contains incompatible controls; nothing was imported.");
        return new(preset.Rate, (int[])preset.Dpi.Clone(), preset.DefaultIndex, preset.ShiftIndex,
            GpwControls.Select(c => (byte[])preset.Bindings.Single(b => b.Control == c).Action.Clone()).ToArray());
    }
    public static LocalPreset Read(string path)
    {
        if (new FileInfo(path).Length > 2_097_152) throw new InvalidDataException("Preset exceeds 2 MiB.");
        using var document = JsonDocument.Parse(File.ReadAllText(path), new() { MaxDepth = 32 });
        string[] allowed = ["schemaVersion", "id", "name", "modelId", "rate", "dpi", "defaultIndex", "shiftIndex", "bindings"];
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new InvalidDataException("Unknown preset fields; preserve the original file.");
        var fields = document.RootElement.EnumerateObject().ToArray();
        if (fields.Select(p => p.Name).Distinct().Count() != fields.Length) throw new InvalidDataException("Duplicate preset fields.");
        if (document.RootElement.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Array)
            foreach (var binding in bindings.EnumerateArray())
                if (binding.ValueKind != JsonValueKind.Object || binding.EnumerateObject().Any(p => p.Name is not ("control" or "action")) || binding.EnumerateObject().Select(p => p.Name).Distinct().Count() != 2)
                    throw new InvalidDataException("Unknown binding fields; preserve the original file.");
        var preset = document.Deserialize<LocalPreset>(Json.Options) ?? throw new InvalidDataException("Empty preset."); Validate(preset); return preset;
    }
    public IReadOnlyList<LocalPreset> List() => Directory.Exists(DirectoryPath) ? Directory.GetFiles(DirectoryPath, "*.lhpreset").Select(Read).OrderBy(p => p.Name).ToArray() : [];
    public void Save(LocalPreset preset)
    {
        Validate(preset); string path = Path.Combine(DirectoryPath, preset.Id + ".lhpreset");
        if (File.Exists(path)) _ = Read(path);
        AtomicFile.Write(path, JsonSerializer.Serialize(preset, Json.Options));
    }
    public void Delete(LocalPreset preset)
    {
        Validate(preset); string path = Path.Combine(DirectoryPath, preset.Id + ".lhpreset");
        _ = Read(path); string trash = Path.Combine(root, "preset-trash", preset.Id + "-" + Guid.NewGuid().ToString("N") + ".lhpreset");
        Directory.CreateDirectory(Path.GetDirectoryName(trash)!); File.Move(path, trash);
    }
    public static void Export(LocalPreset preset, string path) { Validate(preset); AtomicFile.Write(path, JsonSerializer.Serialize(preset, Json.Options)); }
    public void SaveDraft(LocalPreset preset) { lock (fileGate) { Validate(preset); AtomicFile.Write(Path.Combine(root, "drafts", "last.lhpreset"), JsonSerializer.Serialize(preset, Json.Options)); } }
    public LocalPreset? LoadDraft() { string path = Path.Combine(root, "drafts", "last.lhpreset"); return File.Exists(path) ? Read(path) : null; }
    public static void ValidateMacro(LocalMacro macro)
    {
        try { MacroValidator.Validate(macro); }
        catch (MacroLibraryException ex) { throw new InvalidDataException(ex.Message, ex); }
    }
}
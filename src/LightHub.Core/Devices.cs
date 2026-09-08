using System.Reflection;
using System.Text.Json;

namespace LightHub.Core;

public sealed record DeviceIdentity(string Name, string UnitId, string[] ProductIds, string Firmware, int DeviceType);
public sealed record MemoryLayout(string Raw, int MemoryModel, int ProfileFormat, int MacroFormat, int ProfileCount, int ButtonCount, int SectorCount, int SectorSize)
{
    public static MemoryLayout Parse(byte[] raw)
    {
        if (raw.Length != 16) throw new DeviceException(FailureKind.InvalidData, "Truncated memory description.");
        var m = new MemoryLayout(Convert.ToHexString(raw), raw[0], raw[1], raw[2], raw[3], raw[5], raw[6], Wire.Be(raw.AsSpan(7, 2)));
        if (m.ProfileCount is < 1 or > 16 || m.ButtonCount is < 1 or > 16 || m.SectorCount <= m.ProfileCount || m.SectorCount > 128 || m.SectorSize is < 162 or > 4096)
            throw new DeviceException(FailureKind.Unsupported, "Memory geometry exceeds supported bounds.");
        return m;
    }
}
public sealed record DeviceRule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] ProductIds { get; init; } = [];
    public string[] PlatformsWithWriteEvidence { get; init; } = [];
    public int MemoryModel { get; init; }
    public int ProfileFormat { get; init; }
    public int MacroFormat { get; init; }
    public int Profiles { get; init; }
    public int Buttons { get; init; }
    public int SectorSize { get; init; }
    public int SectorCount { get; init; }
    public string Evidence { get; init; } = "";
    public string[] FirmwareWithEvidence { get; init; } = [];
    public string[] ConnectionsWithEvidence { get; init; } = [];
    public string[] VerifiedOperations { get; init; } = [];
}
public enum DeviceOperation { ReadProfile, RuntimeDpi, WriteProfile, Activate, RestoreProfile, WriteMacro, WriteGShift, WriteLighting }
public sealed record SupportDecision(bool CanWrite, string ModelId, string Reason);
public sealed class DeviceCatalog
{
    public IReadOnlyList<DeviceRule> Rules { get; }
    public DeviceCatalog(IEnumerable<DeviceRule> rules) => Rules = rules.ToArray();
    public static DeviceCatalog Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LightHub.Core.devices.json")!;
        return new(JsonSerializer.Deserialize<DeviceRule[]>(s, Json.Options)!);
    }
    public SupportDecision Evaluate(DeviceIdentity identity, MemoryLayout layout, string platform, string connection = "unknown")
        => EvaluateOperation(identity, layout, platform, connection, DeviceOperation.WriteProfile);
    public SupportDecision EvaluateOperation(DeviceIdentity identity, MemoryLayout layout, string platform, string connection, DeviceOperation operation)
    {
        var rule = Rules.FirstOrDefault(r => r.ProductIds.Intersect(identity.ProductIds, StringComparer.OrdinalIgnoreCase).Any());
        if (rule is null) return new(false, "unknown", "Unverified model: diagnostic access only.");
        if (identity.DeviceType != 3) return new(false, rule.Id, "This driver supports mouse profiles only.");
        if (string.IsNullOrWhiteSpace(identity.UnitId) || identity.UnitId == "00000000") return new(false, rule.Id, "A stable physical device identity is required.");
        if (operation == DeviceOperation.ReadProfile) return new(true, rule.Id, "Read-only inspection.");
        if (!rule.PlatformsWithWriteEvidence.Contains(platform)) return new(false, rule.Id, "No write evidence for this model on this platform.");
        if (!rule.FirmwareWithEvidence.Contains(identity.Firmware) || !rule.ConnectionsWithEvidence.Contains(connection))
            return new(false, rule.Id, "Firmware or connection has no matching operation evidence.");
        if (!rule.VerifiedOperations.Contains(operation.ToString())) return new(false, rule.Id, "This operation has no validated driver.");
        bool match = layout.MemoryModel == rule.MemoryModel && layout.ProfileFormat == rule.ProfileFormat && layout.MacroFormat == rule.MacroFormat && layout.ProfileCount == rule.Profiles && layout.ButtonCount == rule.Buttons && layout.SectorSize == rule.SectorSize && layout.SectorCount == rule.SectorCount;
        return new(match, rule.Id, match ? "Memory layout matches the write rule. See the hardware validation report." : "Memory layout differs from recorded evidence; writes disabled.");
    }
    public static string Platform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unknown";
}

public sealed record DpiCapabilities(int[] Discrete, int Minimum, int Maximum, int Step)
{
    public bool Contains(int value) => Discrete.Length > 0 ? Discrete.Contains(value) : Step > 0 && value >= Minimum && value <= Maximum && (value - Minimum) % Step == 0;
    public static DpiCapabilities Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 3) throw new InvalidDataException("Truncated DPI list.");
        var values = new List<int>(); int step = 0;
        for (int i = 1; i + 1 < raw.Length; i += 2)
        {
            int v = Wire.Be(raw.Slice(i, 2)); if (v == 0) break;
            if ((v & 0xe000) == 0xe000) { if (step != 0) throw new InvalidDataException("Multiple DPI step markers."); step = v & 0x1fff; }
            else values.Add(v);
        }
        if (values.Count == 0 || values.Any(v => v is < 50 or > 65535)) throw new InvalidDataException("Empty DPI capabilities.");
        if (step > 0 && (values.Count != 2 || values[0] >= values[1])) throw new InvalidDataException("Unsupported DPI range encoding.");
        return new(step > 0 ? [] : values.ToArray(), values.Min(), values.Max(), step);
    }
}

public static class Json
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true, MaxDepth = 32 };
}

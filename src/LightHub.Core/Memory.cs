using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightHub.Core;

public sealed record DirectoryEntry(int Slot, int Sector, bool Enabled);
public sealed record DeviceSnapshot(int Version, DeviceIdentity Identity, MemoryLayout Layout, int Mode, int ActiveSector, int DpiIndex, SortedDictionary<int, byte[]> Sectors)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? SensorDpi { get; init; }
    public DeviceSnapshot Copy() => this with { Sectors = new(Sectors.ToDictionary(x => x.Key, x => (byte[])x.Value.Clone())) };
    public IReadOnlyList<DirectoryEntry> Directory()
    {
        var raw = Sectors[0]; var entries = new List<DirectoryEntry>(); var seen = new HashSet<int>();
        for (int i = 0; i < Layout.ProfileCount; i++)
        {
            int sector = Wire.Be(raw.AsSpan(i * 4, 2));
            if (sector == 65535) break;
            if (sector <= 0 || sector >= Layout.SectorCount || !seen.Add(sector) || raw[i * 4 + 2] > 1)
                throw new InvalidDataException("Invalid profile directory entry.");
            entries.Add(new(i + 1, sector, raw[i * 4 + 2] != 0));
        }
        if (entries.Count == 0 || Wire.Be(raw.AsSpan(entries.Count * 4, 2)) != 65535) throw new InvalidDataException("Invalid profile directory terminator.");
        return entries;
    }
    public void Validate()
    {
        if (Version != 2 || MemoryLayout.Parse(Convert.FromHexString(Layout.Raw)) != Layout) throw new InvalidDataException("Invalid snapshot schema or layout.");
        if (Identity.UnitId.Length != 8 || !Identity.UnitId.All(Uri.IsHexDigit) || Identity.ProductIds.Length > 8) throw new InvalidDataException("Invalid device identity.");
        if (Mode is not (1 or 2) || DpiIndex is < 0 or > 4) throw new InvalidDataException("Invalid active state.");
        if (SensorDpi is < 50 or > 65535) throw new InvalidDataException("Invalid sensor DPI.");
        if (Sectors.Count != Layout.SectorCount || !Enumerable.Range(0, Layout.SectorCount).All(Sectors.ContainsKey)) throw new InvalidDataException("Incomplete memory snapshot.");
        foreach (var sector in Sectors.Values) if (sector.Length != Layout.SectorSize || !Wire.Intact(sector)) throw new InvalidDataException("Sector integrity check failed.");
        if (!Wire.ValidCrc(Sectors[0]) || !Directory().Any(e => e.Sector == ActiveSector && e.Enabled)) throw new InvalidDataException("Active profile is absent or disabled.");
    }
    public bool SameMemory(DeviceSnapshot other) => Layout == other.Layout && Identity.UnitId == other.Identity.UnitId && Identity.ProductIds.SequenceEqual(other.Identity.ProductIds) && Sectors.Count == other.Sectors.Count && Sectors.All(x => other.Sectors.TryGetValue(x.Key, out var b) && x.Value.SequenceEqual(b));
    public bool SameState(DeviceSnapshot other) => SameMemory(other) && Mode == other.Mode && ActiveSector == other.ActiveSector && DpiIndex == other.DpiIndex;
}

public sealed record MouseProfile(int Rate, int[] Dpi, int DefaultIndex, int ShiftIndex, byte[][] Bindings)
{
    public static bool IsStandardBinding(byte[] binding) => binding.Length == 4 &&
        (binding.SequenceEqual(new byte[] { 255, 0, 0, 0 }) ||
        (binding[0] == 128 && binding[1] == 1 && binding[2] == 0 && binding[3] is 1 or 2 or 4 or 8 or 16) ||
        (binding[0] == 128 && binding[1] == 2 && binding[3] is >= 4 and <= 0x73) ||
        (binding[0] == 128 && binding[1] == 3 && binding[2] == 0 && binding[3] is 205 or 226 or 233 or 234) ||
        (binding[0] == 144 && binding[1] is 3 or 4 or 5 or 7 && binding[2] == 0 && binding[3] == 0));
    public static MouseProfile Decode(byte[] raw, MemoryLayout layout)
    {
        if (layout.MemoryModel != 1 || layout.ProfileFormat is < 1 or > 4 || !Wire.ValidCrc(raw) || raw.Length < 32 + layout.ButtonCount * 4 + 2)
            throw new DeviceException(FailureKind.Unsupported, "Profile format is not supported by the standard mouse decoder.");
        return new(raw[0] == 0 ? 0 : 1000 / raw[0], Enumerable.Range(0, 5).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(3 + i * 2, 2))).ToArray(), raw[1], raw[2], Enumerable.Range(0, layout.ButtonCount).Select(i => raw.AsSpan(32 + i * 4, 4).ToArray()).ToArray());
    }
    public byte[] Encode(byte[] original, MemoryLayout layout, DpiCapabilities dpiCaps, int[] rates)
    {
        if (!rates.Contains(Rate) || !new[] { 125, 250, 500, 1000 }.Contains(Rate)) throw new InvalidDataException("Unsupported polling rate.");
        if (Dpi.Length != 5 || Bindings.Length != layout.ButtonCount || Bindings.Any(b => b.Length != 4)) throw new InvalidDataException("Invalid profile size.");
        int count = Dpi.TakeWhile(d => d != 0).Count();
        if (count == 0 || Dpi.Take(count).Any(d => !dpiCaps.Contains(d)) || Dpi.Skip(count).Any(d => d != 0)) throw new InvalidDataException("DPI must use consecutive slots and the device's supported values.");
        if (DefaultIndex < 0 || DefaultIndex >= count || (ShiftIndex != 255 && (ShiftIndex < 0 || ShiftIndex >= count))) throw new InvalidDataException("Default / Shift DPI must refer to an enabled slot.");
        if (!Bindings[0].SequenceEqual(new byte[] { 128, 1, 0, 1 })) throw new InvalidDataException("Primary click must remain available on button 1.");
        _ = Decode(original, layout);
        for (int i = 0; i < Bindings.Length; i++)
        {
            var binding = Bindings[i];
            if (original.AsSpan(32 + i * 4, 4).SequenceEqual(binding)) continue;
            if (!IsStandardBinding(binding)) throw new InvalidDataException("Changing an unknown action or macro pointer requires a validated driver.");
        }
        var output = (byte[])original.Clone(); output[0] = (byte)(1000 / Rate); output[1] = (byte)DefaultIndex; output[2] = (byte)ShiftIndex;
        for (int i = 0; i < 5; i++) BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(3 + i * 2, 2), checked((ushort)Dpi[i]));
        for (int i = 0; i < Bindings.Length; i++) Bindings[i].CopyTo(output, 32 + i * 4);
        Wire.UpdateCrc(output); return output;
    }
}

public sealed record BackupEnvelope(string Format, string Payload, string Sha256);
public static class BackupFile
{
    public const int MaxBytes = 2_000_000;
    public static string Encode(DeviceSnapshot snapshot)
    {
        snapshot.Validate(); var payload = JsonSerializer.Serialize(snapshot, Json.Options);
        return JsonSerializer.Serialize(new BackupEnvelope("LightHub/2", payload, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))), Json.Options);
    }
    public static DeviceSnapshot Decode(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidDataException("Backup exceeds the size limit.");
        var envelope = JsonSerializer.Deserialize<BackupEnvelope>(text, Json.Options) ?? throw new InvalidDataException("Empty backup.");
        if (envelope.Format != "LightHub/2" || envelope.Payload is null || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope.Payload)))) throw new InvalidDataException("Backup checksum mismatch or unsupported backup version.");
        var s = JsonSerializer.Deserialize<DeviceSnapshot>(envelope.Payload, Json.Options) ?? throw new InvalidDataException("Empty snapshot."); s.Validate(); return s;
    }
    public static DeviceSnapshot Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new InvalidDataException("Backup exceeds the size limit.");
        using var reader = new StreamReader(stream, Encoding.UTF8); return Decode(reader.ReadToEnd());
    }
    public static void Save(string path, DeviceSnapshot snapshot) => AtomicFile.Write(path, Encode(snapshot));
}
public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        path = Path.GetFullPath(path); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { var bytes = Encoding.UTF8.GetBytes(content); stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

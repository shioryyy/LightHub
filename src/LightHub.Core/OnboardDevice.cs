using System.Text;

namespace LightHub.Core;

public sealed record Telemetry(int? Dpi, int? PollingRate, int? BatteryPercent, int? BatteryMillivolts);
public sealed record RuntimeState(int Mode, int ActiveSector, int DpiIndex, Telemetry Telemetry);
public sealed class OnboardDevice : IDisposable
{
    private readonly FeatureClient client;
    private readonly DeviceCatalog catalog;
    private readonly string platform, connection;
    public DeviceIdentity Identity { get; }
    public MemoryLayout Layout { get; }
    public SupportDecision Support { get; }
    public DpiCapabilities? DpiCaps { get; }
    public int[] Rates { get; }
    public OnboardDevice(IReportTransport transport, DeviceCatalog? catalog = null, string? platform = null, string connection = "unknown")
    {
        this.platform = platform ?? DeviceCatalog.Platform; this.connection = connection;
        client = new(transport); this.catalog = catalog ?? DeviceCatalog.Load();
        try
        {
            Identity = ReadIdentity(); Layout = MemoryLayout.Parse(client.Call(0x8100, 0));
            Support = this.catalog.Evaluate(Identity, Layout, this.platform, connection);
            if (client.Find(0x2201, false) != 0) DpiCaps = DpiCapabilities.Parse(client.Call(0x2201, 1, 0));
            if (client.Find(0x8060, false) != 0)
            {
                int mask = client.Call(0x8060, 0)[0];
                Rates = Enumerable.Range(1, 8).Where(i => (mask & (1 << (i - 1))) != 0 && 1000 % i == 0).Select(i => 1000 / i).Where(v => v is 125 or 250 or 500 or 1000).Order().ToArray();
            }
            else Rates = [];
        }
        catch { client.Dispose(); throw; }
    }
    public DeviceIdentity ReadIdentity()
    {
        int length = client.Call(5, 0)[0]; if (length is < 1 or > 128) throw new InvalidDataException("Device name length is invalid.");
        var name = new List<byte>(); while (name.Count < length) name.AddRange(client.Call(5, 1, (byte)name.Count).Take(Math.Min(16, length - name.Count)));
        var raw = client.Call(3, 0); if (raw.Length < 13) throw new InvalidDataException("Device identity is truncated.");
        var ids = new List<string>(); for (int i = 7; i <= 11; i += 2) { int id = Wire.Be(raw.AsSpan(i, 2)); if (id != 0) ids.Add(id.ToString("X4")); }
        string firmware = "unknown";
        if (raw[0] > 0)
        {
            var f = client.Call(3, 1, 0);
            if (f.Length >= 8) firmware = $"{Encoding.ASCII.GetString(f, 1, 3)} {f[4]:X2}.{f[5]:X2}.{Wire.Be(f.AsSpan(6, 2)):X4}";
        }
        return new(Encoding.UTF8.GetString(name.ToArray()).TrimEnd('\0'), Convert.ToHexString(raw.AsSpan(1, 4)), ids.Distinct().ToArray(), firmware, client.Call(5, 2)[0]);
    }
    public RuntimeState ReadRuntime(bool battery = true) => new(client.Call(0x8100, 2)[0], Wire.Be(client.Call(0x8100, 4)), client.Call(0x8100, 11)[0], ReadTelemetry(battery));
    public Telemetry ReadTelemetry(bool battery = true)
    {
        int? dpi = null, rate = null, percent = null, voltage = null;
        if (client.Find(0x2201, false) != 0) dpi = Wire.Be(client.Call(0x2201, 2, 0).AsSpan(1, 2));
        if (client.Find(0x8060, false) != 0) { int v = client.Call(0x8060, 1)[0]; if (v > 0) rate = 1000 / v; }
        if (battery)
        {
            if (client.Find(0x1004, false) != 0) { int p = client.Call(0x1004, 1)[0]; if (p <= 100) percent = p; }
            else if (client.Find(0x1000, false) != 0) { int p = client.Call(0x1000, 0)[0]; if (p <= 100) percent = p; }
            else if (client.Find(0x1001, false) != 0) { int v = Wire.Be(client.Call(0x1001, 0)); if (v > 0) voltage = v; }
        }
        return new(dpi, rate, percent, voltage);
    }
    public int SetCurrentDpi(DeviceSnapshot baseline, int dpi)
    {
        EnsureOperation(DeviceOperation.RuntimeDpi, baseline);
        if (DpiCaps is null) throw new DeviceException(FailureKind.Unsupported, "Sensor DPI capability is unavailable.");
        if (!DpiCaps!.Contains(dpi)) throw new InvalidDataException("DPI is outside the device's supported values.");
        if (client.Call(0x8100, 2)[0] != baseline.Mode || Wire.Be(client.Call(0x8100, 4)) != baseline.ActiveSector || client.Call(0x8100, 11)[0] != baseline.DpiIndex)
            throw new DeviceException(FailureKind.Conflict, "Active profile changed. Refresh before setting DPI.");
        // 0x2201 changes the current sensor resolution without a flash transaction or mode switch.
        client.Call(0x2201, 3, 0, (byte)(dpi >> 8), (byte)dpi);
        int actual = Wire.Be(client.Call(0x2201, 2, 0).AsSpan(1, 2));
        if (actual != dpi) throw new DeviceException(FailureKind.Protocol, "Current DPI read-back differs from the requested value.");
        return actual;
    }
    public DeviceSnapshot ReadSnapshot(CancellationToken cancel = default, bool allowCorrupt = false)
    {
        var identity = ReadIdentity();
        int mode = client.Call(0x8100, 2)[0], active = Wire.Be(client.Call(0x8100, 4)), slot = client.Call(0x8100, 11)[0];
        var sectors = new SortedDictionary<int, byte[]>();
        for (int i = 0; i < Layout.SectorCount; i++) { cancel.ThrowIfCancellationRequested(); sectors[i] = ReadSector(i); }
        var snapshot = new DeviceSnapshot(2, identity, Layout, mode, active, slot, sectors)
        { SensorDpi = DpiCaps is null ? null : Wire.Be(client.Call(0x2201, 2, 0).AsSpan(1, 2)) };
        if (!allowCorrupt) snapshot.Validate(); return snapshot;
    }
    public byte[] ReadSector(int id)
    {
        if (id < 0 || id >= Layout.SectorCount) throw new ArgumentOutOfRangeException(nameof(id));
        var b = new byte[Layout.SectorSize];
        for (int pos = 0; pos < b.Length;) { int offset = Math.Min(pos, b.Length - 16); var r = client.Call(0x8100, 5, (byte)(id >> 8), (byte)id, (byte)(offset >> 8), (byte)offset); if (r.Length != 16) throw new InvalidDataException("Truncated sector read."); r.CopyTo(b, offset); pos = offset + 16; }
        return b;
    }
    public DeviceSnapshot Edit(DeviceSnapshot baseline, int sector, MouseProfile profile, bool activate, int? replacementStage = null)
    {
        EnsureWritable(baseline);
        if (!baseline.Directory().Any(e => e.Sector == sector)) throw new InvalidDataException("Not a profile sector.");
        var desired = baseline.Copy(); desired.Sectors[sector] = profile.Encode(baseline.Sectors[sector], Layout, DpiCaps!, Rates);
        if (activate)
        {
            var entry = baseline.Directory().Single(e => e.Sector == sector); desired.Sectors[0][(entry.Slot - 1) * 4 + 2] = 1; Wire.UpdateCrc(desired.Sectors[0]);
            desired = desired with { Mode = 1, ActiveSector = sector, DpiIndex = profile.DefaultIndex };
        }
        else if (sector == baseline.ActiveSector)
        {
            int selected = replacementStage ?? baseline.DpiIndex;
            if (selected < 0 || selected >= profile.Dpi.Length || profile.Dpi[selected] == 0)
                throw new InvalidDataException("Choose a replacement for the current DPI stage before saving.");
            desired = desired with { DpiIndex = selected };
        }
        if (desired.ActiveSector != baseline.ActiveSector || desired.DpiIndex != baseline.DpiIndex ||
            !baseline.Sectors[baseline.ActiveSector].AsSpan(3 + baseline.DpiIndex * 2, 2).SequenceEqual(desired.Sectors[desired.ActiveSector].AsSpan(3 + desired.DpiIndex * 2, 2)))
            desired = desired with { SensorDpi = MouseProfile.Decode(desired.Sectors[desired.ActiveSector], Layout).Dpi[desired.DpiIndex] };
        desired.Validate(); return desired;
    }
    public void EnsureWritable(DeviceSnapshot snapshot)
    {
        if (!Support.CanWrite || DpiCaps is null || Rates.Length == 0) throw new DeviceException(FailureKind.ReadOnly, Support.Reason);
        EnsureIdentity(snapshot);
    }
    public SupportDecision OperationSupport(DeviceOperation operation) => catalog.EvaluateOperation(Identity, Layout, platform, connection, operation);
    public void EnsureOperation(DeviceOperation operation, DeviceSnapshot snapshot)
    {
        var decision = OperationSupport(operation);
        if (!decision.CanWrite) throw new DeviceException(FailureKind.ReadOnly, decision.Reason);
        EnsureIdentity(snapshot);
    }
    private void EnsureIdentity(DeviceSnapshot snapshot)
    {
        var identity = ReadIdentity();
        if (snapshot.Identity.UnitId != identity.UnitId || snapshot.Identity.Firmware != identity.Firmware || !snapshot.Identity.ProductIds.SequenceEqual(identity.ProductIds) || snapshot.Layout != Layout || MemoryLayout.Parse(client.Call(0x8100, 0)) != Layout)
            throw new DeviceException(FailureKind.Identity, "The backup or draft belongs to another device or memory layout.");
    }
    public void WriteSector(int sector, byte[] expected, byte[] desired, bool recovery)
    {
        if (expected.Length != Layout.SectorSize || desired.Length != Layout.SectorSize || !Wire.ValidCrc(desired) || (!recovery && !Wire.Intact(expected))) throw new InvalidDataException("Invalid sector write.");
        if (!ReadSector(sector).SequenceEqual(expected)) throw new DeviceException(FailureKind.Conflict, "The device configuration changed. Refresh before writing.");
        if (expected.SequenceEqual(desired)) return;
        client.Call(0x8100, 6, (byte)(sector >> 8), (byte)sector, 0, 0, (byte)(Layout.SectorSize >> 8), (byte)Layout.SectorSize);
        for (int offset = 0; offset < desired.Length; offset += 16) { var chunk = new byte[16]; desired.AsSpan(offset, Math.Min(16, desired.Length - offset)).CopyTo(chunk); client.Call(0x8100, 7, chunk); }
        client.Call(0x8100, 8);
        if (!ReadSector(sector).SequenceEqual(desired)) throw new DeviceException(FailureKind.RecoveryRequired, "Full sector read-back failed. Use the pre-write backup to recover.");
    }
    public void Activate(DeviceSnapshot desired)
    {
        int mode = client.Call(0x8100, 2)[0], active = Wire.Be(client.Call(0x8100, 4)), stage = client.Call(0x8100, 11)[0];
        if (active != desired.ActiveSector || stage != desired.DpiIndex)
        {
            if (mode != 1) { client.Call(0x8100, 1, 1); mode = 1; }
            if (active != desired.ActiveSector) client.Call(0x8100, 3, Wire.Be(desired.ActiveSector));
            client.Call(0x8100, 12, (byte)desired.DpiIndex);
        }
        if (Wire.Be(client.Call(0x8100, 4)) != desired.ActiveSector || client.Call(0x8100, 11)[0] != desired.DpiIndex) throw new DeviceException(FailureKind.RecoveryRequired, "Active profile verification failed.");
        if (desired.Mode != mode) client.Call(0x8100, 1, (byte)desired.Mode);
        if (client.Call(0x8100, 2)[0] != desired.Mode) throw new DeviceException(FailureKind.RecoveryRequired, "Onboard mode verification failed.");
    }
    public void Dispose() => client.Dispose();
}

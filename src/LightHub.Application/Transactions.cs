using System.Text.Json;
using LightHub.Core;

namespace LightHub.Application;

public sealed record TransactionRecord(string Id, string UnitId, string BackupPath, string BeforePath, string Status, DateTimeOffset Started, int[] Sectors, string? Error);
public sealed record TransactionResult(string BackupPath, DeviceSnapshot VerifiedSnapshot);
public sealed partial class TransactionStore
{
    public string Root { get; }
    public TransactionStore(string? root = null)
    {
        string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string legacy = Path.Combine(userData, "LightHubCommunity");
        // Existing journals contain absolute paths; keep their storage location until explicitly migrated.
        Root = root ?? (Directory.Exists(legacy) ? legacy : Path.Combine(userData, "LightHub"));
        Directory.CreateDirectory(Root);
    }
    public string SaveBackup(DeviceSnapshot snapshot)
    {
        string path = Path.Combine(Root, "backups", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.lhbackup");
        BackupFile.Save(path, snapshot); _ = BackupFile.Load(path); return path;
    }
    public string SaveRaw(DeviceSnapshot snapshot)
    {
        string path = Path.Combine(Root, "recovery", $"{Guid.NewGuid():N}.raw.json");
        AtomicFile.Write(path, JsonSerializer.Serialize(snapshot, Json.Options)); return path;
    }
    public void Record(TransactionRecord record) => AtomicFile.Write(Path.Combine(Root, "transactions", record.Id + ".json"), JsonSerializer.Serialize(record, Json.Options));
    public IReadOnlyList<TransactionRecord> Pending()
    {
        string dir = Path.Combine(Root, "transactions"); if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.json").Select(p =>
        {
            var record = JsonSerializer.Deserialize<TransactionRecord>(File.ReadAllText(p), Json.Options);
            if (record is null || record.Status is not ("pending" or "failed" or "complete" or "recovered") || string.IsNullOrWhiteSpace(record.BackupPath) || string.IsNullOrWhiteSpace(record.BeforePath))
                throw new InvalidDataException("Recovery journal is invalid; backup cleanup is disabled.");
            return record;
        }).Where(t => t.Status is "pending" or "failed").ToArray();
    }
    public void Resolve(string unit)
    {
        foreach (var record in Pending().Where(t => t.UnitId == unit)) Record(record with { Status = "recovered" });
    }
    public void Log(Exception exception)
    {
        try { string path = Path.Combine(Root, "application.log"); if (File.Exists(path) && new FileInfo(path).Length > 1_048_576) File.Move(path, path + ".1", true); File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {exception.GetType().Name} {(exception is DeviceException d ? d.Kind : "LocalFailure")}\n"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
public sealed class TransactionEngine(TransactionStore store)
{
    public string Apply(OnboardDevice device, DeviceSnapshot baseline, DeviceSnapshot desired, Action<string>? report = null, CancellationToken cancel = default)
        => ApplyVerified(device, baseline, desired, report, cancel).BackupPath;

    public TransactionResult ApplyVerified(OnboardDevice device, DeviceSnapshot baseline, DeviceSnapshot desired, Action<string>? report = null, CancellationToken cancel = default)
    {
        using var storageLease = store.LockBackups();
        if (store.Pending().Any(r => r.UnitId == baseline.Identity.UnitId)) throw new DeviceException(FailureKind.RecoveryRequired, "Resolve the unfinished transaction before changing this device.");
        baseline.Validate(); desired.Validate(); device.EnsureWritable(baseline); device.EnsureWritable(desired);
        if (desired.SensorDpi is { } sensorDpi && !device.DpiCaps!.Contains(sensorDpi)) throw new InvalidDataException("Unsupported target sensor DPI.");
        if (baseline.Mode != desired.Mode || baseline.ActiveSector != desired.ActiveSector) device.EnsureOperation(DeviceOperation.Activate, baseline);
        EnsureEditScope(baseline, desired);
        if (SlotActivation.DirectoryFlagsDiffer(baseline, desired)) device.EnsureOperation(DeviceOperation.EnableProfile, baseline);
        foreach (var entry in desired.Directory().Where(e => !baseline.Sectors[e.Sector].SequenceEqual(desired.Sectors[e.Sector])))
        {
            var profile = MouseProfile.Decode(desired.Sectors[entry.Sector], desired.Layout);
            _ = profile.Encode(baseline.Sectors[entry.Sector], desired.Layout, device.DpiCaps!, device.Rates);
        }
        report?.Invoke("Checking for conflicts");
        var current = device.ReadSnapshot(cancel);
        if (!current.SameState(baseline) || (baseline.SensorDpi is not null && current.SensorDpi != baseline.SensorDpi)) throw new DeviceException(FailureKind.Conflict, "Device memory or active state changed after reading. Refresh first.");
        if (current.SameState(desired) && (desired.SensorDpi is null || current.SensorDpi == desired.SensorDpi)) return new("", current);
        cancel.ThrowIfCancellationRequested(); string backup = store.SaveBackup(current);
        return Commit(device, current, desired, backup, backup, false, report);
    }
    public string Restore(OnboardDevice device, DeviceSnapshot desired, Action<string>? report = null, CancellationToken cancel = default)
    {
        using var storageLease = store.LockBackups();
        desired.Validate(); device.EnsureOperation(DeviceOperation.RestoreProfile, desired); device.EnsureWritable(desired);
        if (desired.SensorDpi is { } sensorDpi && !device.DpiCaps!.Contains(sensorDpi)) throw new InvalidDataException("Unsupported backup sensor DPI.");
        var current = device.ReadSnapshot(cancel, allowCorrupt: true);
        if (current.Identity.UnitId != desired.Identity.UnitId) throw new DeviceException(FailureKind.Identity, "Device identity changed during recovery.");
        if (current.Mode != desired.Mode || current.ActiveSector != desired.ActiveSector) device.EnsureOperation(DeviceOperation.Activate, current);
        // Restoring an enable/disable flag needs the same separately verified
        // directory path; a profile-only restore grant is not sufficient.
        if (SlotActivation.DirectoryFlagsDiffer(current, desired)) device.EnsureOperation(DeviceOperation.EnableProfile, current);
        // Recovery of macro / unknown data formats has not been validated; never write those sectors.
        var permitted = desired.Directory().Select(e => e.Sector).Append(0).ToHashSet();
        if (current.Sectors.Any(x => !permitted.Contains(x.Key) && !x.Value.SequenceEqual(desired.Sectors[x.Key]))) throw new DeviceException(FailureKind.Unsupported, "Macro or non-profile sectors differ. Their restoration requires a validated driver.");
        foreach (int sector in permitted.Where(s => s != 0))
        {
            if (!current.Sectors[sector].SequenceEqual(desired.Sectors[sector]))
            {
                var baselineBytes = (byte[])current.Sectors[sector].Clone(); Wire.UpdateCrc(baselineBytes);
                _ = MouseProfile.Decode(desired.Sectors[sector], desired.Layout).Encode(baselineBytes, desired.Layout, device.DpiCaps!, device.Rates);
            }
            for (int i = 13; i < desired.Layout.SectorSize - 2; i++)
                if (!(i >= 32 && i < 32 + desired.Layout.ButtonCount * 4) && current.Sectors[sector][i] != desired.Sectors[sector][i])
                    throw new DeviceException(FailureKind.Unsupported, "Unknown profile regions differ; no validated restoration driver covers them.");
        }
        cancel.ThrowIfCancellationRequested(); string before = store.SaveRaw(current), backup = store.SaveBackup(desired);
        var result = Commit(device, current, desired, backup, before, true, report); store.Resolve(desired.Identity.UnitId); return result.BackupPath;
    }
    private TransactionResult Commit(OnboardDevice device, DeviceSnapshot before, DeviceSnapshot after, string backup, string beforePath, bool recovery, Action<string>? report)
    {
        var changes = before.Sectors.Keys.Where(id => !before.Sectors[id].SequenceEqual(after.Sectors[id])).OrderBy(id => id == 0 ? int.MaxValue : id).ToArray();
        var record = new TransactionRecord(Guid.NewGuid().ToString("N"), before.Identity.UnitId, backup, beforePath, "pending", DateTimeOffset.UtcNow, changes, null);
        store.Record(record);
        bool verifiedOnDevice = false;
        try
        {
            int? expectedDpi = after.SensorDpi ?? device.ReadTelemetry().Dpi;
            if (before.ActiveSector != after.ActiveSector || before.DpiIndex != after.DpiIndex ||
                !before.Sectors[before.ActiveSector].AsSpan(3 + before.DpiIndex * 2, 2).SequenceEqual(after.Sectors[after.ActiveSector].AsSpan(3 + after.DpiIndex * 2, 2)))
                expectedDpi = MouseProfile.Decode(after.Sectors[after.ActiveSector], after.Layout).Dpi[after.DpiIndex];
            foreach (int sector in changes) { report?.Invoke($"Writing sector {sector}"); device.WriteSector(sector, before.Sectors[sector], after.Sectors[sector], recovery); }
            device.Activate(after);
            if (expectedDpi is { } dpi && device.ReadTelemetry().Dpi != dpi) device.SetCurrentDpi(after, dpi);
            report?.Invoke("Verifying device memory");
            var verified = device.ReadSnapshot();
            if (!verified.SameState(after)) throw new DeviceException(FailureKind.RecoveryRequired, "Final memory or active state differs from the requested configuration.");
            if (expectedDpi is not null && verified.SensorDpi != expectedDpi) throw new DeviceException(FailureKind.RecoveryRequired, "Sensor DPI differs from the requested runtime state.");
            verifiedOnDevice = true;
            store.Record(record with { Status = "complete" }); return new(backup, verified);
        }
        catch (Exception ex)
        {
            try { store.Record(record with { Status = "failed", Error = ex.Message }); } catch (Exception journalError) when (journalError is IOException or UnauthorizedAccessException) { }
            throw new DeviceException(FailureKind.RecoveryRequired, $"{(verifiedOnDevice ? "Device verified; local journal finalization failed. Do not repeat the write." : "Write did not complete.")} Recovery backup: {backup}\n{ex.Message}", ex);
        }
    }
    public static void EnsureEditScope(DeviceSnapshot baseline, DeviceSnapshot desired)
    {
        if (baseline.Identity != desired.Identity && (baseline.Identity.UnitId != desired.Identity.UnitId || !baseline.Identity.ProductIds.SequenceEqual(desired.Identity.ProductIds))) throw new DeviceException(FailureKind.Identity, "Draft identity changed.");
        var profileSectors = baseline.Directory().Select(e => e.Sector).ToHashSet();
        if (!baseline.Directory().Select(e => e.Sector).SequenceEqual(desired.Directory().Select(e => e.Sector))) throw new InvalidDataException("Profile directory structure cannot be edited.");
        foreach (var pair in baseline.Sectors)
        {
            var a = pair.Value; var b = desired.Sectors[pair.Key];
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;
                bool permitted = pair.Key == 0 ? (i < baseline.Directory().Count * 4 && i % 4 == 2 && a[i] == 0 && b[i] == 1) || i >= a.Length - 2
                    : profileSectors.Contains(pair.Key) && (i < 13 || (i >= 32 && i < 32 + baseline.Layout.ButtonCount * 4) || i >= a.Length - 2);
                if (!permitted) throw new InvalidDataException($"Edit touches unsupported bytes in sector {pair.Key}.");
            }
        }
    }
}

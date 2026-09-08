using LightHub.Core;

namespace LightHub.Application;

public interface IDeviceAccess
{
    IDisposable Acquire();
    OnboardDevice Open();
    void CheckCompetition();
}

public sealed record DeviceRead(DeviceSnapshot Snapshot, SupportDecision Support, DpiCapabilities? DpiCaps, int[] Rates, Telemetry Telemetry,
    IReadOnlyDictionary<DeviceOperation, SupportDecision> Operations);

// One session belongs to one selected endpoint. Invalidation never queues a restore on a new connection.
public sealed class DeviceSession(IDeviceAccess access, TransactionStore store) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private OnboardDevice? previewDevice;
    private IDisposable? previewLease;
    private DeviceSnapshot? previewBaseline;
    private int initialDpi, lastDpi;
    private long generation, previewGeneration;
    private bool valid = true;
    public bool Previewing => previewDevice is not null;
    private readonly string markerId = Guid.NewGuid().ToString("N");
    private string MarkerPath => Path.Combine(store.Root, "previews", markerId + ".json");
    public bool HasUnfinishedPreview => Directory.Exists(Path.Combine(store.Root, "previews")) && Directory.EnumerateFiles(Path.Combine(store.Root, "previews"), "*.json").Any();

    private async Task<T> Run<T>(Func<T> action, CancellationToken cancel = default)
    {
        await gate.WaitAsync(cancel).ConfigureAwait(false);
        try { return await Task.Run(() => { if (!valid) throw new DeviceException(FailureKind.Disconnected, "Read the reconnected device before changing it."); return action(); }, cancel).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    private T Connected<T>(Func<OnboardDevice, T> action)
    {
        if (Previewing) throw new InvalidOperationException("End the DPI preview first.");
        using var lease = access.Acquire(); using var device = access.Open(); return action(device);
    }
    private void ReadyToChange(DeviceSnapshot baseline)
    {
        access.CheckCompetition();
        if (store.Pending().Any(r => r.UnitId == baseline.Identity.UnitId)) throw new DeviceException(FailureKind.RecoveryRequired, "Review the unfinished transaction in Backups before changing the device.");
    }
    private static DeviceRead Result(OnboardDevice d, DeviceSnapshot s) => new(s, d.Support, d.DpiCaps, d.Rates, d.ReadTelemetry(),
        Enum.GetValues<DeviceOperation>().ToDictionary(o => o, d.OperationSupport));
    public Task<DeviceRead> Read(CancellationToken cancel = default) => Run(() => Connected(d => Result(d, d.ReadSnapshot(cancel))), cancel);
    public Task<RuntimeState> ReadRuntime(bool battery) => Run(() => Connected(d => d.ReadRuntime(battery)));
    public Task<DeviceRead> Save(DeviceSnapshot baseline, int sector, MouseProfile profile, int? replacementStage = null, Action<string>? report = null)
        => Run(() => Connected(d =>
        {
            ReadyToChange(baseline);
            if (baseline.Mode != 1) throw new DeviceException(FailureKind.Unsupported, "Explicitly enter onboard mode before saving.");
            var desired = d.Edit(baseline, sector, profile, false, replacementStage);
            return Result(d, new TransactionEngine(store).ApplyVerified(d, baseline, desired, report).VerifiedSnapshot);
        }));
    public Task<DeviceRead> Activate(DeviceSnapshot baseline, int sector, Action<string>? report = null)
        => Run(() => Connected(d =>
        {
            ReadyToChange(baseline); d.EnsureOperation(DeviceOperation.Activate, baseline);
            var entry = baseline.Directory().Single(e => e.Sector == sector);
            if (!entry.Enabled) throw new DeviceException(FailureKind.Unsupported, "Enabling disabled directory entries is not a validated operation.");
            var profile = MouseProfile.Decode(baseline.Sectors[sector], baseline.Layout);
            var desired = baseline.Copy() with { Mode = 1, ActiveSector = sector, DpiIndex = profile.DefaultIndex, SensorDpi = profile.Dpi[profile.DefaultIndex] };
            return Result(d, new TransactionEngine(store).ApplyVerified(d, baseline, desired, report).VerifiedSnapshot);
        }));
    public Task<DeviceRead> Restore(string path, Action<string>? report = null) => Run(() => Connected(d =>
    {
        access.CheckCompetition(); new TransactionEngine(store).Restore(d, BackupFile.Load(path), report);
        return Result(d, d.ReadSnapshot());
    }));
    public Task Export(string path, CancellationToken cancel = default) => Run(() => Connected(d => { BackupFile.Save(path, d.ReadSnapshot(cancel)); return true; }), cancel);
    public Task<int> Preview(DeviceSnapshot baseline, int dpi) => Run(() =>
    {
        ReadyToChange(baseline);
        if (previewDevice is null)
        {
            previewLease = access.Acquire();
            try
            {
                previewDevice = access.Open(); previewBaseline = baseline.Copy(); previewGeneration = generation;
                initialDpi = lastDpi = previewDevice.ReadTelemetry().Dpi ?? throw new InvalidDataException("Current DPI is unknown.");
                previewDevice.EnsureOperation(DeviceOperation.RuntimeDpi, baseline);
                AtomicFile.Write(MarkerPath, "{\"schemaVersion\":1,\"unfinished\":true}");
            }
            catch { ReleasePreview(); throw; }
        }
        try
        {
            if (generation != previewGeneration || previewDevice.ReadTelemetry().Dpi != lastDpi)
                throw new DeviceException(FailureKind.Conflict, "Preview ownership changed. Refresh; no automatic restore was sent.");
            lastDpi = previewDevice.SetCurrentDpi(previewBaseline!, dpi); return lastDpi;
        }
        catch { ReleasePreview(); throw; }
    });
    public Task<bool> EndPreview() => Run(() =>
    {
        if (previewDevice is null) return !HasUnfinishedPreview;
        try
        {
            access.CheckCompetition();
            if (generation != previewGeneration || previewDevice.ReadTelemetry().Dpi != lastDpi) return false;
            previewDevice.SetCurrentDpi(previewBaseline!, initialDpi);
            File.Delete(MarkerPath); return true;
        }
        finally { ReleasePreview(); }
    });
    private void ReleasePreview() { previewDevice?.Dispose(); previewDevice = null; previewLease?.Dispose(); previewLease = null; previewBaseline = null; }
    public void Invalidate() { Interlocked.Increment(ref generation); valid = false; }
    public void Dispose() { Invalidate(); gate.Wait(); try { ReleasePreview(); } finally { gate.Release(); } }
}

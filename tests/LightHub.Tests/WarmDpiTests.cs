using LightHub.Application;
using LightHub.Core;
using LightHub.Desktop;
using LightHub.HardwareProbe;
using Xunit;

namespace LightHub.Tests;

// Simulated coverage for the engineering warm-dpi command entry (review findings
// R2/R3) and the corrupt-state restore entry (R1). No real device is involved.
public sealed class WarmDpiTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lighthub-warmdpi-" + Guid.NewGuid().ToString("N"));
    private static OnboardDevice Open(FakeTransport fake, params DeviceOperation[] extra)
    {
        var rules = DeviceCatalog.Load().Rules.Select(r => r with { VerifiedOperations = r.VerifiedOperations.Concat(extra.Select(e => e.ToString())).Distinct().ToArray() });
        return new(fake, new DeviceCatalog(rules), "windows", "C539:receiver");
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private WarmDpiResult Run(FakeTransport fake, int cycles, bool canceled = false, Action? afterBaseline = null, string? markerRoot = null)
    {
        using var device = Open(fake, DeviceOperation.RuntimeDpi);
        var baseline = device.ReadSnapshot(TestContext.Current.CancellationToken);
        afterBaseline?.Invoke();
        return WarmDpiProbe.Run(device, baseline, device.DpiCaps!, cycles, markerRoot ?? root, null,
#pragma warning disable xUnit1051
            canceled ? new CancellationToken(true) : TestContext.Current.CancellationToken);
#pragma warning restore xUnit1051
    }

    [Fact]
    public void PlanPicksOneStepAboveAndNeverTouchesTheDevice()
    {
        var fake = new FakeTransport(DemoData.Create());
        using var device = Open(fake, DeviceOperation.RuntimeDpi);
        var baseline = device.ReadSnapshot(TestContext.Current.CancellationToken);
        var plan = WarmDpiProbe.Plan(baseline, device.DpiCaps!, 7);
        Assert.Equal(1600, plan.InitialDpi);
        Assert.Equal(1650, plan.TestDpi);
        Assert.Equal(7, plan.Cycles);
        Assert.Empty(fake.Mutations);
        Assert.Equal(0, fake.DpiWrites);
    }
    [Fact]
    public void CanceledRunWritesNothingAndCleansItsMarker()
    {
        var fake = new FakeTransport(DemoData.Create());
        var result = Run(fake, 3, canceled: true);
        Assert.Equal(0, fake.DpiWrites);
        Assert.Equal(0, result.CompletedCycles);
        Assert.True(result.Restored);
        Assert.Null(result.MarkerPath);
        Assert.Empty(Directory.GetFiles(root));
    }
    [Fact]
    public void CompletedCyclesRestoreAndRemoveMarker()
    {
        var fake = new FakeTransport(DemoData.Create());
        var result = Run(fake, 3);
        Assert.Equal(6, fake.DpiWrites);
        Assert.Equal(3, result.CompletedCycles);
        Assert.Equal(1600, fake.CurrentDpi);
        Assert.True(result.Restored);
        Assert.Null(result.MarkerPath);
        Assert.NotNull(result.P95Ms);
        Assert.Empty(Directory.GetFiles(root));
    }
    [Fact]
    public void WriteIssuedButReadBackFailsStillRestores()
    {
        var fake = new FakeTransport(DemoData.Create());
        var result = Run(fake, 2, afterBaseline: () => fake.DpiReadBackFailures = 1);
        Assert.Equal(2, fake.DpiWrites);
        Assert.Equal(0, result.CompletedCycles);
        Assert.Equal(1600, fake.CurrentDpi);
        Assert.True(result.Restored);
        Assert.Null(result.MarkerPath);
        Assert.Empty(Directory.GetFiles(root));
    }
    [Fact]
    public void UnconfirmedRestoreLeavesDurableMarkerAndBlocksRetries()
    {
        var fake = new FakeTransport(DemoData.Create());
        var result = Run(fake, 2, afterBaseline: () => fake.DpiReadBackFailures = 2);
        Assert.Equal(2, fake.DpiWrites);
        Assert.False(result.Restored);
        Assert.NotNull(result.MarkerPath);
        Assert.True(File.Exists(result.MarkerPath));
        Assert.Contains("unconfirmed", File.ReadAllText(result.MarkerPath!));
        Assert.Contains("1600", File.ReadAllText(result.MarkerPath!));
        Assert.Throws<IOException>(() => Run(fake, 1));
    }
}

// Review finding R1: the engineering restore entry must reach TransactionEngine.Restore
// even when the onboard directory is corrupt, while ordinary reads stay strict.
public sealed class CorruptStateRestoreEntryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lighthub-restore-" + Guid.NewGuid().ToString("N"));
    private static OnboardDevice Open(FakeTransport fake, params DeviceOperation[] extra)
    {
        // The probe adds Activate/EnableProfile in-process; restoring a corrupt
        // directory flag needs the same temporary rule (review finding R1).
        var rules = DeviceCatalog.Load().Rules.Select(r => r with { VerifiedOperations = r.VerifiedOperations.Concat(new[] { nameof(DeviceOperation.Activate), nameof(DeviceOperation.EnableProfile) }).Concat(extra.Select(e => e.ToString())).Distinct().ToArray() });
        return new(fake, new DeviceCatalog(rules), "windows", "C539:receiver");
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void CorruptDirectoryCrcIsReadableForRestoreAndFullyRestored()
    {
        var original = DemoData.Create();
        var fake = new FakeTransport(original);
        var corrupted = DemoData.Create();
        corrupted.Sectors[0][5] ^= 0xff;
        fake.Sectors[0] = corrupted.Sectors[0];
        using var device = Open(fake, DeviceOperation.RestoreProfile);
        Assert.Throws<InvalidDataException>(() => device.ReadSnapshot(TestContext.Current.CancellationToken));
        var corrupt = device.ReadSnapshot(TestContext.Current.CancellationToken, allowCorrupt: true);
        Assert.False(Wire.ValidCrc(corrupt.Sectors[0]));
        string backup = new TransactionStore(root).SaveBackup(original);
        new TransactionEngine(new TransactionStore(root)).Restore(device, BackupFile.Load(backup), null, TestContext.Current.CancellationToken);
        var final = device.ReadSnapshot(TestContext.Current.CancellationToken);
        Assert.True(final.SameState(original));
    }
    [Fact]
    public void DisabledActiveSlotFlagIsReadableForRestoreAndFullyRestored()
    {
        var original = DemoData.Create();
        var fake = new FakeTransport(original);
        var corrupted = DemoData.Create();
        corrupted.Sectors[0][2] = 0;
        Wire.UpdateCrc(corrupted.Sectors[0]);
        fake.Sectors[0] = corrupted.Sectors[0];
        using var device = Open(fake, DeviceOperation.RestoreProfile);
        Assert.Throws<InvalidDataException>(() => device.ReadSnapshot(TestContext.Current.CancellationToken));
        var corrupt = device.ReadSnapshot(TestContext.Current.CancellationToken, allowCorrupt: true);
        Assert.True(Wire.ValidCrc(corrupt.Sectors[0]));
        string backup = new TransactionStore(root).SaveBackup(original);
        new TransactionEngine(new TransactionStore(root)).Restore(device, BackupFile.Load(backup), null, TestContext.Current.CancellationToken);
        var final = device.ReadSnapshot(TestContext.Current.CancellationToken);
        Assert.True(final.SameState(original));
    }
}

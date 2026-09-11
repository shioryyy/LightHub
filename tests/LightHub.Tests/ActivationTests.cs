using LightHub.Application;
using LightHub.Core;
using LightHub.Desktop;
using Xunit;

namespace LightHub.Tests;

public sealed class ActivationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lighthub-activation-" + Guid.NewGuid().ToString("N"));
    private TransactionStore Store => new(root);
    private static OnboardDevice Open(FakeTransport fake, params DeviceOperation[] extra)
    {
        var rules = DeviceCatalog.Load().Rules.Select(r => r with { VerifiedOperations = r.VerifiedOperations.Concat(extra.Select(e => e.ToString())).Distinct().ToArray() });
        return new(fake, new DeviceCatalog(rules), "windows", "C539:receiver");
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void ActivationCannotBorrowProfileWritePermission()
    {
        var fake = new FakeTransport(DemoData.Create()); using var device = Open(fake);
        var before = device.ReadSnapshot(TestContext.Current.CancellationToken);
        Assert.Throws<DeviceException>(() => SlotActivation.Execute(device, Store, before, 5, true));
        Assert.Empty(fake.Mutations); Assert.Empty(Store.ListBackups());
    }
    [Fact]
    public void SameSlotActivationStillVerifiesRequestedSensorDpi()
    {
        var fake = new FakeTransport(DemoData.Create()) { CurrentDpi = 1650 }; using var device = Open(fake, DeviceOperation.Activate);
        var before = device.ReadSnapshot(TestContext.Current.CancellationToken);
        var result = SlotActivation.Execute(device, Store, before, 1);
        Assert.Equal(1600, result.VerifiedSnapshot.SensorDpi); Assert.NotEmpty(result.BackupPath); Assert.Empty(fake.WriteSectors);
        new TransactionEngine(Store).Restore(device, before, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(1650, fake.CurrentDpi);
    }
    [Fact]
    public void CorruptDirectoryFlagsCanBeRecoveredUsingTrustedBackupGeometry()
    {
        var fake = new FakeTransport(DemoData.Create()); using var device = Open(fake, DeviceOperation.Activate, DeviceOperation.EnableProfile);
        var before = device.ReadSnapshot(TestContext.Current.CancellationToken);
        fake.Sectors[0][2] = 0xff;
        new TransactionEngine(Store).Restore(device, before, cancel: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { 0 }, fake.WriteSectors);
        Assert.True(before.SameState(device.ReadSnapshot(TestContext.Current.CancellationToken)));
    }
    [Fact]
    public void EnablingRequiresBothExplicitIntentAndSeparateEvidence()
    {
        var fake = new FakeTransport(DemoData.Create()); using var device = Open(fake, DeviceOperation.Activate);
        var before = device.ReadSnapshot(TestContext.Current.CancellationToken);
        Assert.Throws<DeviceException>(() => SlotActivation.Execute(device, Store, before, 5));
        Assert.Throws<DeviceException>(() => SlotActivation.Execute(device, Store, before, 5, true));
        // A manually constructed target cannot bypass the application permission check.
        var plan = SlotActivation.Prepare(before, 5, device.DpiCaps!, device.Rates, true);
        Assert.Throws<DeviceException>(() => new TransactionEngine(Store).ApplyVerified(device, before, plan.Desired, cancel: TestContext.Current.CancellationToken));
        Assert.Empty(fake.Mutations);
    }
    [Fact]
    public void EnabledSlotSwitchOnlyChangesRuntimeAndRestoresExactly()
    {
        var source = DemoData.Create(); source.Sectors[0][6] = 1; Wire.UpdateCrc(source.Sectors[0]);
        var fake = new FakeTransport(source); using var device = Open(fake, DeviceOperation.Activate);
        var before = device.ReadSnapshot(TestContext.Current.CancellationToken);
        var result = SlotActivation.Execute(device, Store, before, 2);
        Assert.Equal(2, result.VerifiedSnapshot.ActiveSector); Assert.True(before.SameMemory(result.VerifiedSnapshot)); Assert.Empty(fake.WriteSectors);
        new TransactionEngine(Store).Restore(device, BackupFile.Load(result.BackupPath), cancel: TestContext.Current.CancellationToken);
        var restored = device.ReadSnapshot(TestContext.Current.CancellationToken); Assert.True(before.SameState(restored)); Assert.Equal(before.SensorDpi, restored.SensorDpi);
    }
    [Fact]
    public void EnablingWritesOnlyDirectoryBeforeActivationAndRestoresFlags()
    {
        var fake = new FakeTransport(DemoData.Create()); using var device = Open(fake, DeviceOperation.Activate, DeviceOperation.EnableProfile);
        var before = device.ReadSnapshot(TestContext.Current.CancellationToken); var plan = SlotActivation.Prepare(before, 5, device.DpiCaps!, device.Rates, true);
        Assert.True(plan.EnablesSlot); Assert.False(before.Directory().Single(e => e.Sector == 5).Enabled);
        var result = SlotActivation.Execute(device, Store, before, 5, true);
        Assert.Equal(new[] { 0 }, fake.WriteSectors); Assert.True(fake.Mutations.IndexOf("flash") < fake.Mutations.IndexOf("activate"));
        Assert.Equal(5, result.VerifiedSnapshot.ActiveSector); Assert.Equal(plan.TargetDpi, result.VerifiedSnapshot.SensorDpi);
        foreach (int sector in before.Sectors.Keys.Where(s => s != 0)) Assert.Equal(before.Sectors[sector], result.VerifiedSnapshot.Sectors[sector]);
        new TransactionEngine(Store).Restore(device, BackupFile.Load(result.BackupPath), cancel: TestContext.Current.CancellationToken);
        var restored = device.ReadSnapshot(TestContext.Current.CancellationToken); Assert.True(before.SameState(restored)); Assert.Equal(before.SensorDpi, restored.SensorDpi); Assert.Empty(Store.Pending());
    }
    [Fact]
    public void UnsafeExistingProfilesAreRejectedWithoutAnyWrite()
    {
        var fake = new FakeTransport(DemoData.Create()); using var device = Open(fake, DeviceOperation.Activate, DeviceOperation.EnableProfile);
        var before = device.ReadSnapshot(TestContext.Current.CancellationToken);
        foreach (int offset in new[] { 1, 3, 32, 60 })
        {
            var invalid = before.Copy(); invalid.Sectors[5][offset] = 0xff; Wire.UpdateCrc(invalid.Sectors[5]);
            var failure = Record.Exception(() => SlotActivation.Prepare(invalid, 5, device.DpiCaps!, device.Rates, true));
            Assert.True(failure is InvalidDataException or DeviceException, $"Offset {offset} was not rejected.");
        }
        Assert.Empty(fake.Mutations);
    }
    [Fact]
    public void ConflictOrDirectoryFailureDoesNotActivateAndKeepsRecovery()
    {
        var fake = new FakeTransport(DemoData.Create()); using var device = Open(fake, DeviceOperation.Activate, DeviceOperation.EnableProfile);
        var before = device.ReadSnapshot(TestContext.Current.CancellationToken); fake.CurrentDpi += 50;
        Assert.Equal(FailureKind.Conflict, Assert.Throws<DeviceException>(() => SlotActivation.Execute(device, Store, before, 5, true)).Kind);
        Assert.Empty(fake.Mutations); fake.CurrentDpi = before.SensorDpi!.Value; fake.FailChunk = 2;
        Assert.Equal(FailureKind.RecoveryRequired, Assert.Throws<DeviceException>(() => SlotActivation.Execute(device, Store, before, 5, true)).Kind);
        Assert.DoesNotContain("activate", fake.Mutations); Assert.Single(Store.Pending()); Assert.Single(fake.WriteSectors);
    }
}

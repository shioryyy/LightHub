using LightHub.Application;
using LightHub.Core;
using LightHub.Desktop;
using Xunit;

#pragma warning disable xUnit1051
namespace LightHub.Tests;

public sealed class ApplicationTests : IDisposable
{
    [Fact]
    public async Task RuntimeRefreshTracksHardwareDpiWithoutFlashOrBatteryPolling()
    {
        var fake = new FakeTransport(DemoData.Create()); using var session = Session(fake);
        var before = await session.Read(); int batteryReads = fake.BatteryReads;
        fake.CurrentDpi = 800; fake.Slot = 1;
        var state = await session.ReadRuntime(false);
        Assert.Equal(800, state.Telemetry.Dpi); Assert.Equal(1, state.DpiIndex);
        Assert.Equal(batteryReads, fake.BatteryReads); Assert.Empty(fake.Mutations);
        fake.CurrentDpi = 3200; fake.Slot = 3;
        Assert.Equal(3200, (await session.ReadRuntime(false)).Telemetry.Dpi);
        Assert.True(before.Snapshot.SameMemory((await session.Read()).Snapshot));
    }
    [Fact]
    public async Task ZeroBatteryVoltageIsUnknownAndValidMillivoltsArePreserved()
    {
        var fake = new FakeTransport(DemoData.Create()) { BatteryVoltage = 4202 }; using var session = Session(fake);
        Assert.Equal(4202, (await session.ReadRuntime(true)).Telemetry.BatteryMillivolts);
        fake.BatteryVoltage = 0;
        Assert.Null((await session.ReadRuntime(true)).Telemetry.BatteryMillivolts);
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "LightHub-application-" + Guid.NewGuid().ToString("N"));
    private sealed class Access(FakeTransport fake) : IDeviceAccess
    {
        public IDisposable Acquire() => new EmptyLease();
        public OnboardDevice Open() => new(fake, platform: "windows", connection: "C539:receiver");
        public void CheckCompetition() { }
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    }
    private DeviceSession Session(FakeTransport fake) => new(new Access(fake), new(root));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task ReadInHostModeNeverChangesRuntimeOrMemory()
    {
        var fake = new FakeTransport(DemoData.Create()) { Mode = 2 }; using var session = Session(fake);
        var read = await session.Read(); Assert.Equal(2, read.Snapshot.Mode); Assert.Empty(fake.Mutations);
        var profile = MouseProfile.Decode(read.Snapshot.Sectors[5], read.Snapshot.Layout);
        await Assert.ThrowsAsync<DeviceException>(() => session.Save(read.Snapshot, 5, profile)); Assert.Empty(fake.Mutations);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task SavePreservesActualStageAndDoesNotActivate(int sector)
    {
        var fake = new FakeTransport(DemoData.Create()) { Slot = 1, CurrentDpi = 800 }; using var session = Session(fake);
        var read = await session.Read(); var profile = MouseProfile.Decode(read.Snapshot.Sectors[sector], read.Snapshot.Layout);
        profile.Bindings[^1] = [128, 2, 1, 6];
        var result = await session.Save(read.Snapshot, sector, profile);
        Assert.Equal(1, result.Snapshot.DpiIndex); Assert.Equal(1, result.Snapshot.ActiveSector); Assert.Equal(800, result.Telemetry.Dpi);
        Assert.DoesNotContain(fake.Mutations, m => m is "mode" or "activate" or "stage");
    }
    [Fact]
    public async Task RemovingCurrentStageNeedsExplicitReplacement()
    {
        var fake = new FakeTransport(DemoData.Create()) { Slot = 4, CurrentDpi = 6400 }; using var session = Session(fake);
        var read = await session.Read(); var p = MouseProfile.Decode(read.Snapshot.Sectors[1], read.Snapshot.Layout); p.Dpi[4] = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => session.Save(read.Snapshot, 1, p)); Assert.Empty(fake.Mutations);
        var result = await session.Save(read.Snapshot, 1, p, 1); Assert.Equal(1, result.Snapshot.DpiIndex); Assert.Equal(800, result.Telemetry.Dpi);
    }
    [Fact]
    public async Task PreviewRestoresOnlyMatchingStateAndNeverCreatesFlashBackup()
    {
        var fake = new FakeTransport(DemoData.Create()); using var session = Session(fake); var read = await session.Read();
        await session.Preview(read.Snapshot, 850); Assert.True(session.Previewing); Assert.True(await session.EndPreview()); Assert.Equal(1600, fake.CurrentDpi);
        Assert.Empty(new TransactionStore(root).ListBackups()); Assert.Empty(fake.WriteSectors);
        await session.Preview(read.Snapshot, 900); fake.CurrentDpi = 1000;
        Assert.False(await session.EndPreview()); Assert.Equal(1000, fake.CurrentDpi); Assert.True(session.HasUnfinishedPreview);
    }
    [Fact]
    public async Task InvalidationNeverRestoresOntoReconnect()
    {
        var fake = new FakeTransport(DemoData.Create()); using (var session = Session(fake))
        {
            var read = await session.Read(); await session.Preview(read.Snapshot, 850); session.Invalidate();
            await Assert.ThrowsAsync<DeviceException>(session.EndPreview);
        }
        Assert.Equal(850, fake.CurrentDpi);
        using var fresh = Session(fake); await fresh.Read(); Assert.Equal(850, fake.CurrentDpi); Assert.True(fresh.HasUnfinishedPreview);
    }
    [Fact]
    public async Task PendingRecoveryBlocksPreviewAndSave()
    {
        var fake = new FakeTransport(DemoData.Create()); using var session = Session(fake); var read = await session.Read();
        var store = new TransactionStore(root); string backup = store.SaveBackup(read.Snapshot);
        store.Record(new("test", read.Snapshot.Identity.UnitId, backup, backup, "failed", DateTimeOffset.UtcNow, [5], "test"));
        await Assert.ThrowsAsync<DeviceException>(() => session.Preview(read.Snapshot, 850));
        await Assert.ThrowsAsync<DeviceException>(() => session.Save(read.Snapshot, 5, MouseProfile.Decode(read.Snapshot.Sectors[5], read.Snapshot.Layout)));
        Assert.Empty(fake.Mutations);
    }
    [Fact]
    public void CapabilitiesRequireFirmwareConnectionAndOperationEvidence()
    {
        var s = DemoData.Create(); var catalog = DeviceCatalog.Load();
        Assert.False(catalog.Evaluate(s.Identity with { Firmware = "unknown" }, s.Layout, "windows", "C539:receiver").CanWrite);
        Assert.False(catalog.Evaluate(s.Identity, s.Layout, "windows", "C088:direct").CanWrite);
        Assert.False(catalog.EvaluateOperation(s.Identity, s.Layout, "windows", "C539:receiver", DeviceOperation.WriteMacro).CanWrite);
        Assert.False(catalog.EvaluateOperation(s.Identity, s.Layout, "windows", "C539:receiver", DeviceOperation.Activate).CanWrite);
    }
    [Fact]
    public void RecycleRoundTripKeepsMetadataAndRechecksProtection()
    {
        var store = new TransactionStore(root); var snapshot = DemoData.Create(); string backup = store.SaveBackup(snapshot);
        store.SetBackupMetadata(backup, new("Before change", true, false));
        Assert.Throws<InvalidOperationException>(() => store.DeleteBackups([backup]));
        store.SetBackupMetadata(backup, new("Before change", false, false)); store.DeleteBackups([backup]);
        var entry = Assert.Single(store.ListTrash()); Assert.False(File.Exists(backup)); store.RecoverTrash(entry.Id);
        Assert.True(BackupFile.Load(backup).SameState(snapshot)); Assert.Equal("Before change", Assert.Single(store.ListBackups()).Metadata.Label);
        store.DeleteBackups([backup]); var recycled = Assert.Single(store.ListTrash());
        store.Record(new("pending", snapshot.Identity.UnitId, backup, backup, "pending", DateTimeOffset.UtcNow, [], null));
        Assert.Throws<InvalidOperationException>(() => store.PermanentlyDeleteTrash(recycled.Id));
    }
    [Fact]
    public void PresetsAreSemanticAndIncompatibleImportDoesNotChangeTarget()
    {
        var s = DemoData.Create(); var profile = MouseProfile.Decode(s.Sectors[1], s.Layout);
        var preset = LocalAssets.FromProfile("Office", "g-pro-wireless", profile); var assets = new LocalAssets(root); assets.Save(preset);
        Assert.Single(assets.List()); string serialized = System.Text.Json.JsonSerializer.Serialize(preset);
        Assert.DoesNotContain(s.Identity.UnitId, serialized); Assert.DoesNotContain("sectors", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(profile.Dpi, LocalAssets.Map(preset, "g-pro-wireless", profile).Dpi);
        Assert.Throws<InvalidDataException>(() => LocalAssets.Map(preset, "g305", profile));
        Assert.Throws<InvalidDataException>(() => assets.Save(preset with { SchemaVersion = 2 }));
    }
    [Fact]
    public void MacroValidationRejectsStuckKeysAndUnboundedSequences()
    {
        var macro = new LocalMacro(1, Guid.NewGuid().ToString("N"), "Copy", "single", [new("down", 0xe0, 0), new("down", 6, 0), new("up", 6, 0), new("up", 0xe0, 0)]);
        LocalAssets.ValidateMacro(macro);
        Assert.Throws<InvalidDataException>(() => LocalAssets.ValidateMacro(macro with { Events = [new("down", 6, 0)] }));
        Assert.Throws<InvalidDataException>(() => LocalAssets.ValidateMacro(macro with { Execution = "loop" }));
        Assert.Throws<InvalidDataException>(() => LocalAssets.ValidateMacro(macro with { Events = Enumerable.Repeat(new MacroEvent("delay", 0, 10000), 4).ToArray() }));
    }
    [Fact]
    public async Task IndependentProcessHoldsStorageLease()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LightHub.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string executable = Path.Combine(directory.FullName, "tests", "LightHub.LockProbe", "bin", configuration, "net10.0", OperatingSystem.IsWindows() ? "LightHub.LockProbe.exe" : "LightHub.LockProbe");
        var start = new System.Diagnostics.ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        start.ArgumentList.Add(root);
        var store = new TransactionStore(root); string backup = store.SaveBackup(DemoData.Create());
        using var process = System.Diagnostics.Process.Start(start)!;
        try
        {
            Assert.Equal("LOCKED", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Throws<DeviceException>(() => store.DeleteBackups([backup])); Assert.True(File.Exists(backup));
        }
        finally { await process.StandardInput.WriteLineAsync(); if (!process.WaitForExit(10000)) process.Kill(true); }
    }
    [Fact]
    public async Task ExternalSensorChangeConflictsBeforeFlash()
    {
        var fake = new FakeTransport(DemoData.Create()); using var session = Session(fake); var read = await session.Read();
        var p = MouseProfile.Decode(read.Snapshot.Sectors[5], read.Snapshot.Layout); p.Dpi[0] = 450; fake.CurrentDpi = 850;
        var error = await Assert.ThrowsAsync<DeviceException>(() => session.Save(read.Snapshot, 5, p));
        Assert.Equal(FailureKind.Conflict, error.Kind); Assert.Empty(fake.WriteSectors);
    }
    [Fact]
    public async Task RestoreDoesNotWriteUnknownLightingOrMacroReferences()
    {
        var fake = new FakeTransport(DemoData.Create()); using var session = Session(fake); var read = await session.Read();
        var store = new TransactionStore(root); string path = store.SaveBackup(read.Snapshot);
        fake.Sectors[5][210] ^= 1; Wire.UpdateCrc(fake.Sectors[5]);
        await Assert.ThrowsAsync<DeviceException>(() => session.Restore(path)); Assert.Empty(fake.WriteSectors);
        var profile = MouseProfile.Decode(read.Snapshot.Sectors[5], read.Snapshot.Layout); profile.Bindings[7] = [0, 0, 0, 25];
        Assert.Throws<InvalidDataException>(() => profile.Encode(read.Snapshot.Sectors[5], read.Snapshot.Layout, read.DpiCaps!, read.Rates));
    }
    [Fact]
    public void CompletionJournalFailureReportsVerifiedDeviceWithoutRetry()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing denies replacement; native Unix crash-durability is a separate gate.
        var fake = new FakeTransport(DemoData.Create()); using var device = new OnboardDevice(fake, platform: "windows", connection: "C539:receiver");
        var baseline = device.ReadSnapshot(); var p = MouseProfile.Decode(baseline.Sectors[5], baseline.Layout); p.Dpi[0] = 450;
        var desired = device.Edit(baseline, 5, p, false); var store = new TransactionStore(root); FileStream? blocked = null;
        try
        {
            var error = Assert.Throws<DeviceException>(() => new TransactionEngine(store).Apply(device, baseline, desired, phase =>
            {
                if (phase == "Verifying device memory") blocked = new FileStream(Directory.GetFiles(Path.Combine(root, "transactions"), "*.json").Single(), FileMode.Open, FileAccess.Read, FileShare.Read);
            }));
            Assert.Contains("Device verified", error.Message); Assert.True(device.ReadSnapshot().SameState(desired)); Assert.Single(fake.WriteSectors); Assert.Single(store.Pending());
        }
        finally { blocked?.Dispose(); }
    }
}

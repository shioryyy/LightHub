using System.Text;
using System.Text.Json;
using LightHub.Core;
using LightHub.Application;
using LightHub.Desktop;
using LightHub.Hid;
using Xunit;

// All protocol calls below use an in-memory transport; cancellation has its own explicit test.
#pragma warning disable xUnit1051

namespace LightHub.Tests;

public sealed class CoreTests
{
    [Fact] public void KnownCrcVector() => Assert.Equal(0x29b1, Wire.Crc(Encoding.ASCII.GetBytes("123456789")));
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(16)]
    public void FramingPreservesPayload(int length)
    {
        var payload = Enumerable.Range(0, length).Select(i => (byte)i).ToArray(); var b = Wire.Request(4, 9, 5, 12, payload);
        Assert.Equal(length <= 3 ? 7 : 20, b.Length); Assert.Equal(0x5c, b[3]); Assert.Equal(payload, b.Skip(4).Take(length));
    }
    [Fact]
    public void MatchingRejectsOtherSlotSoftwareAndShortLongPacket()
    {
        byte[] request = Wire.Request(2, 9, 5, 11, []); var response = (byte[])request.Clone(); Assert.NotNull(Wire.Match(request, response));
        response[1] = 3; Assert.Null(Wire.Match(request, response)); response[1] = 2; response[3]++; Assert.Null(Wire.Match(request, response));
        response[3]--; response[0] = 17; Assert.Null(Wire.Match(request, response));
    }
    [Fact] public void ProtocolErrorsAreTyped() => Assert.Equal(FailureKind.Protocol, Assert.Throws<DeviceException>(() => Wire.Match(Wire.Request(1, 9, 5, 11, []), [16, 1, 255, 9, 0x5b, 9, 0])).Kind);
    [Theory]
    [InlineData(1, 6, 255, 8)]
    [InlineData(3, 11, 256, 16)]
    [InlineData(5, 8, 255, 16)]
    [InlineData(8, 16, 512, 32)]
    public void VariableGeometryRoundTrips(int profiles, int buttons, int size, int sectors)
    {
        var s = DemoData.Create(profiles, buttons, size, sectors); s.Validate(); Assert.True(BackupFile.Decode(BackupFile.Encode(s)).SameState(s));
        var p = MouseProfile.Decode(s.Sectors[1], s.Layout);
        Assert.Equal(s.Sectors[1], p.Encode(s.Sectors[1], s.Layout, new([], 100, 25600, 50), [125, 250, 500, 1000]));
    }
    [Fact]
    public void UnknownModelsAndPlatformsAreNeverWritable()
    {
        var s = DemoData.Create(); var catalog = DeviceCatalog.Load();
        Assert.True(catalog.Evaluate(s.Identity, s.Layout, "windows", "C539:receiver").CanWrite);
        Assert.False(catalog.Evaluate(s.Identity, s.Layout, "linux").CanWrite);
        Assert.False(catalog.Evaluate(s.Identity with { ProductIds = ["FFFF"] }, s.Layout, "windows").CanWrite);
        Assert.False(catalog.Evaluate(s.Identity with { Name = "G PRO Wireless", DeviceType = 1 }, s.Layout, "windows").CanWrite);
        Assert.False(catalog.Evaluate(s.Identity, DemoData.Create(3).Layout, "windows").CanWrite);
    }
    [Fact]
    public void DpiRangeAndDiscreteListsAreDifferent()
    {
        var range = DpiCapabilities.Parse([0, 0, 100, 0xe0, 50, 0x64, 0, 0, 0]); Assert.Equal(25600, range.Maximum); Assert.True(range.Contains(850)); Assert.False(range.Contains(801));
        var discrete = DpiCapabilities.Parse([0, 1, 144, 3, 32, 6, 64, 0, 0]); Assert.True(discrete.Contains(800)); Assert.False(discrete.Contains(850));
        Assert.Throws<InvalidDataException>(() => DpiCapabilities.Parse([0, 0, 0]));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(801)]
    [InlineData(25650)]
    public void InvalidDpiDraftsFailBeforeWriting(int value)
    {
        var s = DemoData.Create(); var p = MouseProfile.Decode(s.Sectors[1], s.Layout); p.Dpi[0] = value;
        Assert.Throws<InvalidDataException>(() => p.Encode(s.Sectors[1], s.Layout, new([], 100, 25600, 50), [1000]));
    }
    [Fact]
    public void PrimaryClickIsProtected()
    {
        var s = DemoData.Create(); var p = MouseProfile.Decode(s.Sectors[1], s.Layout); p.Bindings[0] = [255, 0, 0, 0];
        Assert.Throws<InvalidDataException>(() => p.Encode(s.Sectors[1], s.Layout, new([], 100, 25600, 50), [1000]));
    }
    [Fact]
    public void EditsPreserveAllUnknownBytesAndMacros()
    {
        var s = DemoData.Create(); var p = MouseProfile.Decode(s.Sectors[1], s.Layout); p.Dpi[1] = 850;
        var output = p.Encode(s.Sectors[1], s.Layout, new([], 100, 25600, 50), [1000]);
        Assert.All(Enumerable.Range(0, output.Length).Where(i => output[i] != s.Sectors[1][i]), i => Assert.True(i is 5 or 6 or 253 or 254));
        Assert.Equal(s.Sectors[1].Skip(96).Take(157), output.Skip(96).Take(157));
    }
    [Fact]
    public void MalformedBackupsNeverReachHardware()
    {
        var s = DemoData.Create(); string encoded = BackupFile.Encode(s);
        Assert.Throws<InvalidDataException>(() => BackupFile.Decode(encoded.Replace("1234ABCD", "9876ABCD")));
        Assert.Throws<InvalidDataException>(() => BackupFile.Decode(new string('x', BackupFile.MaxBytes + 1)));
        var missing = s.Copy(); missing.Sectors.Remove(15); Assert.Throws<InvalidDataException>(() => missing.Validate());
        var corrupt = s.Copy(); corrupt.Sectors[1][12] ^= 1; Assert.Throws<InvalidDataException>(() => corrupt.Validate());
        var duplicate = s.Copy(); duplicate.Sectors[0][5] = 1; Wire.UpdateCrc(duplicate.Sectors[0]); Assert.Throws<InvalidDataException>(() => duplicate.Validate());
    }
    [Fact]
    public void EditScopeRejectsLightingAndMacroMutations()
    {
        var s = DemoData.Create(); var desired = s.Copy(); desired.Sectors[1][210] = 1; Wire.UpdateCrc(desired.Sectors[1]);
        Assert.Throws<InvalidDataException>(() => TransactionEngine.EnsureEditScope(s, desired));
        desired = s.Copy(); desired.Sectors[10][0] = 0; Wire.UpdateCrc(desired.Sectors[10]); Assert.Throws<InvalidDataException>(() => TransactionEngine.EnsureEditScope(s, desired));
    }
    [Fact]
    public void WindowsCollectionsRemainPerPhysicalDevice()
    {
        string a = @"\\?\hid#vid_046d&pid_c539&mi_02&col01#7&abc&0&0000#{guid}";
        string b = @"\\?\hid#vid_046d&pid_c539&mi_02&col02#7&abc&0&0001#{guid}";
        Assert.Equal(HidDiscovery.NormalizeWindowsPath(a), HidDiscovery.NormalizeWindowsPath(b));
        Assert.NotEqual(HidDiscovery.NormalizeWindowsPath(a), HidDiscovery.NormalizeWindowsPath(b.Replace("abc", "def")));
    }
}

public sealed class TransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lighthub-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact]
    public void SuccessfulWriteHasBackupJournalAndExactReadback()
    {
        var baseline = DemoData.Create(); var fake = new FakeTransport(baseline); using var d = Open(fake); var current = d.ReadSnapshot(); var p = MouseProfile.Decode(current.Sectors[5], current.Layout); p.Dpi[0] = 450;
        var desired = d.Edit(current, 5, p, false); var store = new TransactionStore(root); var engine = new TransactionEngine(store);
        var result = engine.ApplyVerified(d, current, desired); string backup = result.BackupPath;
        Assert.NotSame(desired, result.VerifiedSnapshot); Assert.True(result.VerifiedSnapshot.SameState(desired));
        Assert.True(BackupFile.Load(backup).SameState(current)); Assert.True(d.ReadSnapshot().SameState(desired)); Assert.Single(fake.WriteSectors); Assert.Equal(16, fake.Chunks); Assert.Empty(store.Pending());
        engine.Restore(d, current); Assert.True(d.ReadSnapshot().SameState(current));
    }
    [Fact]
    public void NoopDoesNotTouchFlashOrCreateBackup()
    {
        using var d = Open(new(DemoData.Create())); var s = d.ReadSnapshot(); Assert.Equal("", new TransactionEngine(new(root)).Apply(d, s, s.Copy())); Assert.False(Directory.Exists(Path.Combine(root, "backups")));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    public void InterruptedChunkNeverRetriesAndLeavesRecoveryRecord(int chunk)
    {
        var fake = new FakeTransport(DemoData.Create()) { FailChunk = chunk }; using var d = Open(fake); var before = d.ReadSnapshot(); var p = MouseProfile.Decode(before.Sectors[5], before.Layout); p.Dpi[0] = 450;
        var store = new TransactionStore(root); var ex = Assert.Throws<DeviceException>(() => new TransactionEngine(store).Apply(d, before, d.Edit(before, 5, p, false)));
        Assert.Equal(FailureKind.RecoveryRequired, ex.Kind); Assert.Equal(chunk, fake.Chunks); Assert.Equal(0, fake.Commits); Assert.Single(fake.WriteSectors); var journal = Assert.Single(store.Pending()); Assert.True(File.Exists(journal.BackupPath));
    }
    [Fact]
    public void StaleStateFailsBeforeFlash()
    {
        var fake = new FakeTransport(DemoData.Create()); using var d = Open(fake); var before = d.ReadSnapshot(); fake.Slot = 1;
        Assert.Equal(FailureKind.Conflict, Assert.Throws<DeviceException>(() => new TransactionEngine(new(root)).Apply(d, before, before.Copy())).Kind); Assert.Empty(fake.WriteSectors);
    }
    [Fact]
    public void RecoveryCanRepairBadProfileCrc()
    {
        var fake = new FakeTransport(DemoData.Create()); using var d = Open(fake); var backup = d.ReadSnapshot(); fake.Sectors[5][11] ^= 1;
        Assert.Throws<InvalidDataException>(() => d.ReadSnapshot()); new TransactionEngine(new(root)).Restore(d, backup); Assert.True(d.ReadSnapshot().SameState(backup));
    }
    [Fact]
    public void CorruptReadbackFailsTransaction()
    {
        var fake = new FakeTransport(DemoData.Create()) { CorruptCommit = true }; using var d = Open(fake); var before = d.ReadSnapshot(); var p = MouseProfile.Decode(before.Sectors[5], before.Layout); p.Dpi[0] = 450;
        Assert.Throws<DeviceException>(() => new TransactionEngine(new(root)).Apply(d, before, d.Edit(before, 5, p, false))); Assert.Single(new TransactionStore(root).Pending());
    }
    [Fact]
    public void FinalVerificationStillChecksUntouchedMemory()
    {
        var fake = new FakeTransport(DemoData.Create()) { ChangeUntouchedSector = true };
        using var d = Open(fake); var before = d.ReadSnapshot();
        var p = MouseProfile.Decode(before.Sectors[5], before.Layout); p.Dpi[0] = 450;
        var store = new TransactionStore(root);
        var ex = Assert.Throws<DeviceException>(() => new TransactionEngine(store).ApplyVerified(d, before, d.Edit(before, 5, p, false)));
        Assert.Equal(FailureKind.RecoveryRequired, ex.Kind); Assert.Single(store.Pending());
    }
    [Fact]
    public void OtherUnitBackupIsRefused()
    {
        using var d = Open(new(DemoData.Create())); var s = d.ReadSnapshot(); s = s with { Identity = s.Identity with { UnitId = "AAAAAAAA" } };
        Assert.Equal(FailureKind.Identity, Assert.Throws<DeviceException>(() => new TransactionEngine(new(root)).Restore(d, s)).Kind);
    }
    [Fact]
    public void CancellationOnlyBeforeFirstWrite()
    {
        var fake = new FakeTransport(DemoData.Create()); using var d = Open(fake); var s = d.ReadSnapshot();
        Assert.Throws<OperationCanceledException>(() => new TransactionEngine(new(root)).Apply(d, s, s.Copy(), cancel: new(true))); Assert.Empty(fake.WriteSectors);
    }
    [Fact]
    public void MacroRecoveryRequiresSeparateValidatedDriver()
    {
        var fake = new FakeTransport(DemoData.Create()); using var d = Open(fake); var s = d.ReadSnapshot(); fake.Sectors[9][0] = 0; Wire.UpdateCrc(fake.Sectors[9]);
        Assert.Equal(FailureKind.Unsupported, Assert.Throws<DeviceException>(() => new TransactionEngine(new(root)).Restore(d, s)).Kind); Assert.Empty(fake.WriteSectors);
    }
    [Fact]
    public void ReadOnlyCannotBeOverriddenByDraft()
    {
        using var d = new OnboardDevice(new FakeTransport(DemoData.Create()), platform: "linux"); var s = d.ReadSnapshot();
        Assert.Equal(FailureKind.ReadOnly, Assert.Throws<DeviceException>(() => new TransactionEngine(new(root)).Apply(d, s, s.Copy())).Kind);
    }
    [Fact]
    public void CurrentDpiChangesSensorWithoutFlashOrOnboardStateChanges()
    {
        var fake = new FakeTransport(DemoData.Create()); using var d = Open(fake); var before = d.ReadSnapshot();
        Assert.Equal(850, d.SetCurrentDpi(before, 850));
        Assert.Equal(850, d.ReadTelemetry().Dpi); Assert.True(d.ReadSnapshot().SameState(before));
        Assert.Empty(fake.WriteSectors); Assert.Equal(0, fake.Commits); Assert.Equal(1, fake.DpiWrites);
    }
    [Fact]
    public void CurrentDpiRespectsBoundsReadOnlyAndActiveProfileConflicts()
    {
        var fake = new FakeTransport(DemoData.Create()); using var d = Open(fake); var before = d.ReadSnapshot();
        Assert.Throws<InvalidDataException>(() => d.SetCurrentDpi(before, 801));
        fake.Slot = 0;
        Assert.Equal(FailureKind.Conflict, Assert.Throws<DeviceException>(() => d.SetCurrentDpi(before, 850)).Kind);
        using var readOnly = new OnboardDevice(fake, platform: "linux");
        Assert.Equal(FailureKind.ReadOnly, Assert.Throws<DeviceException>(() => readOnly.SetCurrentDpi(before, 850)).Kind);
        Assert.Equal(0, fake.DpiWrites);
    }
    [Fact]
    public void CurrentDpiRejectsWrongReadbackWithoutRetrying()
    {
        var fake = new FakeTransport(DemoData.Create()) { IgnoreDpiWrite = true }; using var d = Open(fake);
        var before = d.ReadSnapshot();
        Assert.Equal(FailureKind.Protocol, Assert.Throws<DeviceException>(() => d.SetCurrentDpi(before, 850)).Kind);
        Assert.Equal(1, fake.DpiWrites); Assert.Empty(fake.WriteSectors);
    }
    [Fact]
    public void LeaseBlocksAnotherProcessHandle()
    {
        using var first = new DeviceLease("receiver", root); Assert.Throws<DeviceException>(() => new DeviceLease("receiver", root)); using var other = new DeviceLease("other-receiver", root);
    }
    private static OnboardDevice Open(FakeTransport fake) => new(fake, platform: "windows", connection: "C539:receiver");
}

public sealed class FakeTransport : IReportTransport
{
    public SortedDictionary<int, byte[]> Sectors; public int Mode = 1, Active = 1, Slot = 2, Chunks, Commits, FailChunk; public bool CorruptCommit, ChangeUntouchedSector;
    public int CurrentDpi = 1600, DpiWrites; public bool IgnoreDpiWrite;
    public int BatteryVoltage = 3980, BatteryReads;
    public List<string> Mutations { get; } = [];
    public List<int> WriteSectors { get; } = []; private int writing, offset; private byte[] buffer = []; private readonly MemoryLayout layout;
    public FakeTransport(DeviceSnapshot s) { Sectors = s.Copy().Sectors; layout = s.Layout; }
    public byte[] Exchange(byte feature, byte function, ReadOnlySpan<byte> data)
    {
        var r = new byte[16];
        if (feature == 0) { int id = Wire.Be(data); r[0] = (byte)(id switch { 5 => 3, 3 => 2, 0x8100 => 9, 0x2201 => 12, 0x8060 => 11, 0x1001 => 6, _ => 0 }); }
        else if (feature == 3) { byte[] name = Encoding.UTF8.GetBytes("G PRO Wireless"); if (function == 0) r[0] = (byte)name.Length; else if (function == 2) r[0] = 3; else name.AsSpan(data[0], Math.Min(16, name.Length - data[0])).CopyTo(r); }
        else if (feature == 2) { if (function == 0) { r[0] = 1; Convert.FromHexString("1234ABCD").CopyTo(r, 1); r[7] = 0x40; r[8] = 0x79; r[9] = 0xc0; r[10] = 0x88; } else { Encoding.ASCII.GetBytes("BOT").CopyTo(r, 1); r[4] = 0x74; r[5] = 2; r[7] = 0x26; } }
        else if (feature == 12) { if (function == 1) { r[2] = 100; r[3] = 0xe0; r[4] = 50; r[5] = 0x64; } else if (function == 3) { DpiWrites++; if (!IgnoreDpiWrite) CurrentDpi = Wire.Be(data[1..]); } else { Wire.Be(CurrentDpi).CopyTo(r, 1); } }
        else if (feature == 11) r[0] = (byte)(function == 0 ? 0x8b : 1);
        else if (feature == 6) { BatteryReads++; Wire.Be(BatteryVoltage).CopyTo(r, 0); }
        else if (feature == 9) switch (function)
            {
                case 0: return Convert.FromHexString(layout.Raw);
                case 1: Mutations.Add("mode"); Mode = data[0]; break;
                case 2: r[0] = (byte)Mode; break;
                case 3: Mutations.Add("activate"); Active = Wire.Be(data); break;
                case 4: Wire.Be(Active).CopyTo(r, 0); break;
                case 5: Sectors[Wire.Be(data)].AsSpan(Wire.Be(data[2..]), 16).CopyTo(r); break;
                case 6: Mutations.Add("flash"); writing = Wire.Be(data); WriteSectors.Add(writing); buffer = new byte[layout.SectorSize]; offset = 0; break;
                case 7: Chunks++; if (Chunks == FailChunk) throw new IOException("Simulated disconnect"); int n = Math.Min(16, buffer.Length - offset); data[..n].CopyTo(buffer.AsSpan(offset)); offset += n; break;
                case 8: Commits++; Sectors[writing] = buffer; if (CorruptCommit) Sectors[writing][11] ^= 1; if (ChangeUntouchedSector) { Sectors[9][0] = 0; Wire.UpdateCrc(Sectors[9]); } break;
                case 11: r[0] = (byte)Slot; break;
                case 12: Mutations.Add("stage"); Slot = data[0]; break;
                default: throw new InvalidOperationException("Unexpected function");
            }
        return r;
    }
    public void Dispose() { }
}

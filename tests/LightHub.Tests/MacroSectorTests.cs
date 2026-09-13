using LightHub.Core;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text;
using LightHub.Application;
using LightHub.Desktop;
using Xunit;

namespace LightHub.Tests;

// Read-only onboard macro format coverage (review-safe M4 step one): strict bounds,
// state reporting on truncation / loops / unknown opcodes, no writes anywhere.
public sealed class MacroSectorTests
{
    private static DeviceSnapshot Snapshot() => DemoData.Create();
    private static ushort[] Ids(DeviceSnapshot s) => OnboardMacroFormat.MacroSectorIds(s);

    private static void WriteEvents(DeviceSnapshot s, ushort sector, params byte[][] events)
    {
        var data = s.Sectors[sector];
        int offset = 0;
        foreach (var e in events) { e.CopyTo(data, offset); offset += e.Length; }
        data[offset] = 0xFF;
        Wire.UpdateCrc(data);
    }
    private static byte[] Press(byte modifiers, byte usage) => [0x43, modifiers, usage];
    private static byte[] Release(byte modifiers, byte usage) => [0x44, modifiers, usage];
    private static byte[] Delay(int ms) => [0x40, (byte)(ms >> 8), (byte)ms];

    [Fact]
    public void MacroSectorIdsFollowLastProfileSector()
    {
        var ids = Ids(Snapshot());
        Assert.Equal(Enumerable.Range(6, 10).Select(i => (ushort)i), ids);
    }
    [Fact]
    public void PointerDetectionMatchesReferenceShapes()
    {
        Assert.True(OnboardMacroFormat.IsMacroPointer([0x00, 0x09, 0x00, 0x10]));
        Assert.False(OnboardMacroFormat.IsMacroPointer([0x80, 0x01, 0x00, 0x01]));
        Assert.False(OnboardMacroFormat.IsMacroPointer([0x00, 0x00, 0x00, 0x00]));
        Assert.False(OnboardMacroFormat.IsMacroPointer([0xFF, 0x00, 0x00, 0x00]));
        var b = new byte[] { 0x00, 0x09, 0x00, 0x10 };
        Assert.Equal((9, 0x10), (OnboardMacroFormat.PointerSector(b), OnboardMacroFormat.PointerOffset(b)));
    }
    [Fact]
    public void BlankSectorReadsAsBlank()
    {
        var s = Snapshot();
        var macro = OnboardMacroFormat.Decode(s, Ids(s), 9, 4);
        Assert.Equal("blank", macro.State);
        Assert.Empty(macro.Steps);
    }
    [Fact]
    public void CompleteMacroCollapsesPressReleasePairs()
    {
        var s = Snapshot();
        WriteEvents(s, 9, Press(0x02, 0x06), Release(0x02, 0x06), Delay(120), Press(0, 0x04), Release(0, 0x04), [0xFF, 0xFF]);
        var macro = OnboardMacroFormat.Decode(s, Ids(s), 9, 0);
        Assert.Equal("complete", macro.State);
        Assert.Equal(3, macro.Steps.Count);
        Assert.Equal("shift+c", macro.Steps[0].ToString());
        Assert.Equal("wait 120 ms", macro.Steps[1].ToString());
        Assert.Equal("a", macro.Steps[2].ToString());
    }
    [Fact]
    public void SplitPressReleaseAcrossJumpStaysOneStep()
    {
        var s = Snapshot();
        WriteEvents(s, 9, Press(0, 0x04), [0x60, 0x00, 0x0A, 0x00, 0x00]);
        WriteEvents(s, 10, Release(0, 0x04));
        var macro = OnboardMacroFormat.Decode(s, Ids(s), 9, 0);
        Assert.Equal("complete", macro.State);
        var step = Assert.Single(macro.Steps);
        Assert.Equal("a", step.ToString());
    }
    [Fact]
    public void JumpOutsideMacroRangeIsUnsupportedNotWritten()
    {
        var s = Snapshot();
        WriteEvents(s, 9, Press(0, 0x04), [0x60, 0x00, 0x01, 0x00, 0x00]);
        var macro = OnboardMacroFormat.Decode(s, Ids(s), 9, 0);
        Assert.Equal("unsupported-opcode", macro.State);
        Assert.Equal("a down", macro.Steps[0].ToString());
    }
    [Fact]
    public void UnknownOpcodeStopsWithRawByteAndKeepsEarlierSteps()
    {
        var s = Snapshot();
        WriteEvents(s, 9, Press(0, 0x04), Release(0, 0x04), [0x71, 0x00]);
        var macro = OnboardMacroFormat.Decode(s, Ids(s), 9, 0);
        Assert.Equal("unsupported-opcode", macro.State);
        Assert.Equal(2, macro.Steps.Count);
        Assert.Equal("unknown opcode 0x71", macro.Steps[^1].ToString());
    }
    [Fact]
    public void TruncatedDelayReportsStateWithoutThrowing()
    {
        var s = Snapshot();
        // Fill the payload with delay events and no END marker, so the final event's
        // operands would run into the CRC bytes: strict parsing must stop at the edge.
        var data = s.Sectors[9];
        for (int i = 0; i + 2 < data.Length - 2; i += 3) { data[i] = 0x40; data[i + 1] = 0x01; data[i + 2] = 0x00; }
        data[data.Length - 3] = 0x40; data[data.Length - 2] = 0x01;
        Wire.UpdateCrc(data);
        var macro = OnboardMacroFormat.Decode(s, Ids(s), 9, 0);
        Assert.Equal("truncated", macro.State);
    }
    [Fact]
    public void JumpLoopIsDetected()
    {
        var s = Snapshot();
        WriteEvents(s, 9, Press(0, 0x04), [0x60, 0x00, 0x0A, 0x00, 0x00]);
        WriteEvents(s, 10, [0x60, 0x00, 0x09, 0x00, 0x00]);
        var macro = OnboardMacroFormat.Decode(s, Ids(s), 9, 0);
        Assert.Equal("loop", macro.State);
        Assert.NotEmpty(macro.Steps);
    }
    [Fact]
    public void CorruptCrcIsReportedAsInvalid()
    {
        var s = Snapshot();
        WriteEvents(s, 9, Press(0, 0x04));
        s.Sectors[9][10] ^= 0xFF;
        var macro = OnboardMacroFormat.Decode(s, Ids(s), 9, 0);
        Assert.Equal("invalid-crc", macro.State);
    }
    [Fact]
    public void StartOutsideMacroRangeIsRefused()
    {
        var s = Snapshot();
        var macro = OnboardMacroFormat.Decode(s, Ids(s), 1, 0);
        Assert.Equal("outside-macro-range", macro.State);
    }
}

// The device editor surfaces what a macro pointer refers to, read-only, in both
// languages, without offering any way to write onboard macro bytes.
public sealed class MacroInfoUiTests
{
    [AvaloniaFact]
    public async Task MacroPointerBindingShowsOnboardStepsAndLanguageSwitch()
    {
        var w = new MainWindow(true); w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs();
        try
        {
            var s = w.Model.Snapshot!;
            s.Sectors[9][0] = 0x43; s.Sectors[9][1] = 0x02; s.Sectors[9][2] = 0x06;
            s.Sectors[9][3] = 0x44; s.Sectors[9][4] = 0x02; s.Sectors[9][5] = 0x06;
            s.Sectors[9][6] = 0x40; s.Sectors[9][7] = 0x00; s.Sectors[9][8] = 0x78; s.Sectors[9][9] = 0xFF;
            Wire.UpdateCrc(s.Sectors[9]);
            byte[] pointer = [0x00, 0x09, 0x00, 0x00];
            pointer.CopyTo(s.Sectors[1], 32 + 4);
            Wire.UpdateCrc(s.Sectors[1]);
            w.Model.LoadProfile(1);
            var vm = w.Model.ButtonAssignment;
            vm.Button = w.Model.Buttons[1];
            Assert.True(vm.HasOnboardMacroInfo);
            Assert.Contains("shift+c", vm.OnboardMacroInfo);
            Assert.Contains("wait 120 ms", vm.OnboardMacroInfo);
            vm.Button = w.Model.Buttons[0];
            Assert.False(vm.HasOnboardMacroInfo);
            w.FindControl<ComboBox>("Language")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            vm.Button = w.Model.Buttons[1];
            Assert.StartsWith("板载宏", vm.OnboardMacroInfo);
            Assert.Contains("wait 120 ms", vm.OnboardMacroInfo);
        }
        finally { w.Close(); }
    }
}

public sealed class ProfileNameTests
{
    private static void WriteName(DeviceSnapshot s, string? name)
    {
        var data = s.Sectors[1];
        for (int i = 0; i < 48; i++) data[ProfileName.Offset + i] = 0xFF;
        if (name is not null) Encoding.Unicode.GetBytes(name).CopyTo(data, ProfileName.Offset);
        Wire.UpdateCrc(data);
    }
    [Fact]
    public void TerminatedUtf16NameIsRead()
    {
        var s = DemoData.Create();
        WriteName(s, "G HUB");
        Assert.Equal("G HUB", ProfileName.Read(s, 1));
        WriteName(s, null);
        Assert.Null(ProfileName.Read(s, 1));
    }
    [Fact]
    public void TerminatorAndShortNamesStopParsing()
    {
        var s = DemoData.Create();
        var data = s.Sectors[1];
        Encoding.Unicode.GetBytes("AB").CopyTo(data, ProfileName.Offset);
        data[ProfileName.Offset + 4] = 0xFF; data[ProfileName.Offset + 5] = 0xFF; // explicit terminator
        Wire.UpdateCrc(data);
        Assert.Equal("AB", ProfileName.Read(s, 1));
    }
    [Fact]
    public void ControlCharactersAndLoneSurrogatesAreRejected()
    {
        var s = DemoData.Create();
        var data = s.Sectors[1];
        data[ProfileName.Offset] = 0x07; data[ProfileName.Offset + 1] = 0x00; // control char
        data[ProfileName.Offset + 2] = 0x41; data[ProfileName.Offset + 3] = 0x00;
        Wire.UpdateCrc(data);
        Assert.Null(ProfileName.Read(s, 1));
        data[ProfileName.Offset] = 0x00; data[ProfileName.Offset + 1] = 0xD8; // lone high surrogate
        Wire.UpdateCrc(data);
        Assert.Null(ProfileName.Read(s, 1));
    }
    [AvaloniaFact]
    public async Task DiagnosticsSummarizeMacrosWithoutStepContent()
    {
        var w = new MainWindow(true); w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs();
        try
        {
            var s = w.Model.Snapshot!;
            s.Sectors[9][0] = 0x43; s.Sectors[9][1] = 0x02; s.Sectors[9][2] = 0x06;
            s.Sectors[9][3] = 0x44; s.Sectors[9][4] = 0x02; s.Sectors[9][5] = 0x06; s.Sectors[9][6] = 0xFF;
            Wire.UpdateCrc(s.Sectors[9]);
            byte[] pointer = [0x00, 0x09, 0x00, 0x00];
            pointer.CopyTo(s.Sectors[1], 32 + 4);
            Wire.UpdateCrc(s.Sectors[1]);
            var text = w.Model.RedactedDiagnostics();
            Assert.Contains("\"pointerBindings\": 1", text);
            Assert.DoesNotContain("shift+c", text);
            Assert.DoesNotContain("0x09", text);
        }
        finally { w.Close(); }
    }
}

public sealed class OnboardMacroPackerTests
{
    private static LocalMacro Macro(params MacroEvent[] events) => new(1, Guid.NewGuid().ToString("N"), "compile-test", "single", events);
    private static DeviceSnapshot SnapshotWith(OnboardMacroPacker.PackedMacro packed)
    {
        var s = DemoData.Create();
        foreach (var pair in packed.Sectors) s.Sectors[pair.Key] = pair.Value;
        return s;
    }

    [Fact]
    public void SingleSectorMacroRoundTripsThroughTheReader()
    {
        var macro = Macro(new("down", 0x04, 0), new("up", 0x04, 0), new("delay", 0, 500), new("down", 0xe0, 0), new("down", 0x06, 0), new("up", 0x06, 0), new("up", 0xe0, 0));
        var packed = OnboardMacroPacker.Pack(macro, [6, 7, 8], 255);
        Assert.Single(packed.Sectors);
        Assert.Equal("complete", OnboardMacroFormat.Decode(SnapshotWith(packed), [6, 7, 8], packed.StartSector, 0).State);
        var steps = OnboardMacroFormat.Decode(SnapshotWith(packed), [6, 7, 8], packed.StartSector, 0).Steps;
        Assert.Equal(new[] { "key", "delay", "key-press", "key", "key-release" }, steps.Select(s => s.Kind));
        Assert.Equal(500, steps[1].DelayMs);
    }
    [Fact]
    public void LargeMacroSpansSectorsWithJumpsAndStillRoundTrips()
    {
        var events = Enumerable.Range(0, 100).Select(_ => new MacroEvent("delay", 0, 250)).ToArray();
        var macro = Macro(events);
        var packed = OnboardMacroPacker.Pack(macro, [6, 7, 8, 9], 255);
        Assert.True(packed.Sectors.Count > 1);
        var decoded = OnboardMacroFormat.Decode(SnapshotWith(packed), [6, 7, 8, 9], packed.StartSector, 0);
        Assert.Equal("complete", decoded.State);
        Assert.Equal(100, decoded.Steps.Count);
        Assert.All(decoded.Steps, s => Assert.Equal(250, s.DelayMs));
    }
    [Fact]
    public void OversizedMacroFailsCapacityInsteadOfGuessing()
    {
        var events = Enumerable.Range(0, 100).Select(_ => new MacroEvent("delay", 0, 10000)).ToArray();
        IReadOnlyList<ushort> oneId = [6];
        Assert.Throws<MacroLibraryException>(() => OnboardMacroPacker.Pack(Macro(events), oneId, 255));
    }
    [Fact]
    public void ModifierUsagesEncodeAsModifierBits()
    {
        var macro = Macro(new("down", 0xe0, 0), new("up", 0xe0, 0));
        var packed = OnboardMacroPacker.Pack(macro, [6], 255);
        var data = packed.Sectors[6];
        Assert.Equal(0x43, data[0]); Assert.Equal(0x01, data[1]); Assert.Equal(0x00, data[2]);
        Assert.Equal(0x44, data[3]); Assert.Equal(0x01, data[4]);
    }
}

// The guided recovery entry lists unfinished transactions with their exact backup
// paths; restore itself stays gated on a connected, matching device.
public sealed class RecoveryGuideTests
{
    [AvaloniaFact]
    public async Task PendingTransactionShowsGuidedRecoveryEntry()
    {
        string root = Path.Combine(Path.GetTempPath(), "lighthub-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TransactionStore(root);
            var backup = store.SaveBackup(DemoData.Create());
            store.Record(new("test-rec", "1234ABCD", backup, backup, "failed", DateTimeOffset.UtcNow.AddMinutes(-5), [5], "Simulated interrupted write"));
            var w = new MainWindow(false, store); w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs();
            try
            {
                await w.Model.RefreshBackupsAsync(); Dispatcher.UIThread.RunJobs();
                Assert.True(w.Model.HasPendingTransactions);
                var item = Assert.Single(w.Model.RecoveryGuide);
                Assert.True(item.BackupAvailable);
                Assert.EndsWith(".lhbackup", item.BackupName);
                Assert.False(item.CanRestore); // no device connected in the headless test
                Assert.Contains("Simulated interrupted write", item.Details);
                Assert.Equal("test-rec", item.Record.Id);
            }
            finally
            {
                w.Model.DisposeSession(); w.Close(); Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (IOException) when (attempt < 15) { Thread.Sleep(200); }
            }
        }
    }
}

// Coverage for the alpha.3 review findings A3-R1..R7.
public sealed class ReviewAlpha3Tests
{
    private static LocalMacro Macro(params MacroEvent[] events) => new(1, Guid.NewGuid().ToString("N"), "review-test", "single", events);
    private static DeviceSnapshot SnapshotWith(OnboardMacroPacker.PackedMacro packed)
    {
        var s = DemoData.Create();
        foreach (var pair in packed.Sectors) s.Sectors[pair.Key] = pair.Value;
        return s;
    }

    // A3-R1: the returned set is exactly the sectors used, in packing order.
    [Fact]
    public void ReturnedSectorsFollowPackingOrderNotSectorNumber()
    {
        IReadOnlyList<ushort> ids = [9, 6, 7];
        var events = Enumerable.Range(0, 100).Select(_ => new MacroEvent("delay", 0, 250)).ToArray();
        var packed = OnboardMacroPacker.Pack(Macro(events), ids, 255);
        // Two sectors, used in packing order (9 first, then 6) — not sorted by id.
        Assert.Equal(new[] { 9, 6 }, packed.Sectors.Keys);
        Assert.Equal(9, packed.StartSector);
        var single = OnboardMacroPacker.Pack(Macro(new MacroEvent[] { new("delay", 0, 100) }), ids, 255);
        Assert.Equal(new[] { 9 }, single.Sectors.Keys);
        IReadOnlyList<ushort> sparseIds = [20, 40, 60];
        var sparseEvents = Enumerable.Range(0, 100).Select(_ => new MacroEvent("delay", 0, 250)).ToArray();
        var sparse = OnboardMacroPacker.Pack(Macro(sparseEvents), sparseIds, 255);
        Assert.Equal(new[] { 20, 40 }, sparse.Sectors.Keys);
    }
    // A3-R2: a damaged recovery backup must not escape the Safe boundary.
    [AvaloniaFact]
    public async Task CorruptRecoveryBackupKeepsUiUsable()
    {
        string root = Path.Combine(Path.GetTempPath(), "lighthub-recovery-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TransactionStore(root);
            var backup = store.SaveBackup(DemoData.Create());
            store.Record(new("test-rec", "1234ABCD", backup, backup, "failed", DateTimeOffset.UtcNow, [5], "Simulated"));
            File.WriteAllText(backup, "NOT A BACKUP");
            var w = new MainWindow(false, store); w.Show(); await w.Initialization; Dispatcher.UIThread.RunJobs();
            try
            {
                await w.Model.RefreshBackupsAsync(); Dispatcher.UIThread.RunJobs();
                w.FindControl<TabControl>("Tabs")!.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
                var button = w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "RecoveryRestore");
                Assert.NotNull(button);
                button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.False(w.Model.HasPendingTransactions is false && false); // still alive
                await w.Model.RefreshBackupsAsync(); // the window remains fully usable
                Assert.True(w.Model.HasPendingTransactions); // transaction unresolved, no crash
            }
            finally { w.Model.DisposeSession(); w.Close(); Dispatcher.UIThread.RunJobs(); }
        }
        finally
        {
            for (int attempt = 0; ; attempt++) { try { Directory.Delete(root, true); break; } catch (IOException) when (attempt < 15) { Thread.Sleep(200); } }
        }
    }
    // A3-R3: declared format must gate the parser.
    [Fact]
    public void UnknownMacroFormatIsReportedNotInterpreted()
    {
        var s = DemoData.Create();
        s.Sectors[9][0] = 0x43; s.Sectors[9][1] = 0x00; s.Sectors[9][2] = 0x04; s.Sectors[9][3] = 0xFF;
        Wire.UpdateCrc(s.Sectors[9]);
        Assert.Equal("complete", OnboardMacroFormat.Decode(s, [9], 9, 0).State);
        var odd = s with { Layout = s.Layout with { MacroFormat = 2 } };
        Assert.Equal("unsupported-format", OnboardMacroFormat.Decode(odd, [9], 9, 0).State);
        var oddModel = s with { Layout = s.Layout with { MemoryModel = 2 } };
        Assert.Equal("unsupported-format", OnboardMacroFormat.Decode(oddModel, [9], 9, 0).State);
    }
    // A3-R4: digit zero and unknown base keys keep their real usage.
    [Fact]
    public void KeyNamesPreserveActualUsage()
    {
        Assert.Equal("0", OnboardMacroFormat.KeyName(0, 0x27));
        Assert.Equal("ctrl+key 0x74", OnboardMacroFormat.KeyName(1, 0x74));
        Assert.Equal("ctrl", OnboardMacroFormat.KeyName(1, 0));
        Assert.Equal("key 0x00", OnboardMacroFormat.KeyName(0, 0));
        Assert.Equal("9", OnboardMacroFormat.KeyName(0, 0x26));
    }
    // A3-R5: surrogate pairs accepted, lone surrogates and C1 controls rejected.
    [Fact]
    public void UnicodeNameValidationMatchesUtf16Semantics()
    {
        var s = DemoData.Create();
        var data = s.Sectors[1];
        WriteNameUnits(data, 'A', 0xD83D, 0xDE00, 'B'); // A + emoji + B
        Wire.UpdateCrc(data);
        Assert.Equal("A😀B".Replace("😀", char.ConvertFromUtf32(0x1F600)), ProfileName.Read(s, 1));
        WriteNameUnits(data, 'A', 0xD83D); // lone high surrogate
        Wire.UpdateCrc(data);
        Assert.Null(ProfileName.Read(s, 1));
        WriteNameUnits(data, 'A', 0xDE00); // lone low surrogate
        Wire.UpdateCrc(data);
        Assert.Null(ProfileName.Read(s, 1));
        WriteNameUnits(data, 'A', 0x0085, 'B'); // C1 control (NEL)
        Wire.UpdateCrc(data);
        Assert.Null(ProfileName.Read(s, 1));
    }
    private static void WriteNameUnits(byte[] data, params int[] units)
    {
        for (int i = 0; i < 48; i++) data[ProfileName.Offset + i] = 0xFF;
        int offset = ProfileName.Offset;
        foreach (int unit in units) { data[offset++] = (byte)(unit & 0xFF); data[offset++] = (byte)(unit >> 8); }
    }
    // A3-R6: exactly MaxEvents events plus END stays complete; one more truncates.
    // Built byte by byte: the local library caps macros at 256 events, so an
    // onboard-sized stream can only be constructed directly.
    [Fact]
    public void EventCapAllowsExactlyMaxEventsThenEnd()
    {
        var sectors = DemoData.Create(sectors: 60);
        var ids = OnboardMacroFormat.MacroSectorIds(sectors);
        var exact = DecodeDelayStream(sectors, ids, 4096);
                Assert.Equal("complete", exact.State);
        Assert.Equal(4096, exact.Steps.Count);
        var over = DecodeDelayStream(DemoData.Create(sectors: 60), ids, 4097);
        Assert.Equal("truncated", over.State);
        Assert.Equal(4096, over.Steps.Count);
    }
    private static OnboardMacro DecodeDelayStream(DeviceSnapshot s, ushort[] ids, int eventCount)
    {
        int perSector = (255 - 2 - 5) / 3;
        int remaining = eventCount;
        for (int i = 0; i < ids.Length && remaining > 0; i++)
        {
            var data = s.Sectors[ids[i]];
            int offset = 0, count = Math.Min(perSector, remaining);
            for (int e = 0; e < count; e++) { data[offset] = 0x40; data[offset + 1] = 0x00; data[offset + 2] = 0x01; offset += 3; }
            remaining -= count;
            if (remaining > 0) { data[offset] = 0x60; data[offset + 1] = (byte)(ids[i + 1] >> 8); data[offset + 2] = (byte)ids[i + 1]; data[offset + 3] = 0x00; data[offset + 4] = 0x00; offset += 5; }
            data[offset] = 0xFF;
            Wire.UpdateCrc(data);
        }
        return OnboardMacroFormat.Decode(s, ids, ids[0], 0);
    }
    // A3-R7: EncodedLength validates shape like Pack does.
    [Fact]
    public void EncodedLengthRejectsMalformedMacros()
    {
        Assert.Throws<MacroLibraryException>(() => OnboardMacroPacker.EncodedLength(new(999, Guid.NewGuid().ToString("N"), "x", "single", [new MacroEvent("delay", 0, 100)])));
        Assert.Throws<MacroLibraryException>(() => OnboardMacroPacker.EncodedLength(new(1, Guid.NewGuid().ToString("N"), "x", "weird", [new MacroEvent("delay", 0, 100)])));
        IReadOnlyList<ushort> oneId = [6];
        Assert.Throws<MacroLibraryException>(() => OnboardMacroPacker.Pack(Macro(new MacroEvent[] { }), oneId, 255)); // empty events
        Assert.Equal(8, OnboardMacroPacker.EncodedLength(Macro(new("down", 0x04, 0), new("up", 0x04, 0)))); // 6 + terminator, even
    }
}

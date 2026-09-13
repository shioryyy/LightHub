using LightHub.Core;
using Avalonia.Headless.XUnit;
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
        var events = Enumerable.Range(0, 100).Select(_ => new MacroEvent("delay", 0, 10000)).ToArray();
        var macro = Macro(events);
        var packed = OnboardMacroPacker.Pack(macro, [6, 7, 8, 9], 255);
        Assert.True(packed.Sectors.Count > 1);
        var decoded = OnboardMacroFormat.Decode(SnapshotWith(packed), [6, 7, 8, 9], packed.StartSector, 0);
        Assert.Equal("complete", decoded.State);
        Assert.Equal(100, decoded.Steps.Count);
        Assert.All(decoded.Steps, s => Assert.Equal(10000, s.DelayMs));
    }
    [Fact]
    public void OversizedMacroFailsCapacityInsteadOfGuessing()
    {
        var events = Enumerable.Range(0, 100).Select(_ => new MacroEvent("delay", 0, 10000)).ToArray();
        Assert.Throws<MacroLibraryException>(() => OnboardMacroPacker.Pack(Macro(events), [6], 255));
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

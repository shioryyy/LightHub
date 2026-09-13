using LightHub.Core;
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

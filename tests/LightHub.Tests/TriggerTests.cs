using LightHub.Core;
using Xunit;

namespace LightHub.Tests;

public sealed class TriggerTests
{
    [Fact]
    public void TriggerInspectionOnlyLooksUpFeaturesAndReadsControlMetadata()
    {
        using var transport = new TriggerTransport(); var result = TriggerInspector.Inspect(transport, TestContext.Current.CancellationToken);
        var control = Assert.Single(result.Controls);
        Assert.Equal(0x50, control.Cid); Assert.True(control.Divertable); Assert.False(control.CurrentlyDiverted);
        Assert.True(result.ReadOnly); Assert.False(result.ExecutionVerified);
        Assert.Equal(9, transport.Calls.Count);
        Assert.All(transport.Calls, call => Assert.True(call is (0, 0) or (8, 0) or (8, 1) or (8, 2)));
    }
    [Fact]
    public void TriggerQueryErrorsAreNotMisreportedAsUnsupported()
    {
        using var transport = new TriggerTransport { LookupError = true };
        var result = TriggerInspector.Inspect(transport, TestContext.Current.CancellationToken);
        Assert.All(result.Features, f => { Assert.Equal("query-error", f.Status); Assert.Null(f.Index); });
        Assert.Empty(result.Controls); Assert.False(result.ExecutionVerified);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TriggerInspectionRejectsWrongIdentityAndUnboundedControlCount(bool wrongCid, bool largeCount)
    {
        using var transport = new TriggerTransport { WrongCid = wrongCid, LargeCount = largeCount };
        Assert.Throws<InvalidDataException>(() => TriggerInspector.Inspect(transport, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(transport.Calls, c => c.Item2 >= 3);
    }
    private sealed class TriggerTransport : IReportTransport
    {
        public bool LookupError, WrongCid, LargeCount;
        public List<(byte, byte)> Calls { get; } = [];
        public byte[] Exchange(byte feature, byte function, ReadOnlySpan<byte> parameters)
        {
            Calls.Add((feature, function));
            if (feature == 0)
            {
                if (LookupError) throw new DeviceException(FailureKind.Protocol, "Feature query refused.");
                return Wire.Be(parameters) == 0x1b04 ? [8, 0, 1] : [0, 0, 0];
            }
            return function switch
            {
                0 => [(byte)(LargeCount ? 255 : 1)],
                1 => [0, 0x50, 0, 0x50, 0x31, 1, 1, 1, 0],
                2 => [0, (byte)(WrongCid ? 0x51 : 0x50), 0, 0, 0x50, 0],
                _ => throw new InvalidOperationException("Unexpected mutation")
            };
        }
        public void Dispose() { }
    }
}

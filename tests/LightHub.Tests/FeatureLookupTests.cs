using LightHub.Core;
using Xunit;

namespace LightHub.Tests;

public sealed class FeatureLookupTests
{
    [Fact]
    public void InvalidFeatureLookupIsOptionalAndCachedWithoutHidingOtherErrors()
    {
        var transport = new ErrorTransport(0xff, 6); using var client = new FeatureClient(transport);
        Assert.Equal(0, client.Find(0x1004, false)); Assert.Equal(0, client.Find(0x1004, false)); Assert.Equal(1, transport.Calls);
        Assert.Equal(FailureKind.Unsupported, Assert.Throws<DeviceException>(() => client.Find(0x1004)).Kind);
    }
    [Theory]
    [InlineData(0xff, 2)]
    [InlineData(0xff, 9)]
    [InlineData(0x8f, 6)]
    public void ProtocolOrLegacyErrorsAreNotCachedAsMissingFeatures(byte report, byte code)
    {
        var transport = new ErrorTransport(report, code); using var client = new FeatureClient(transport);
        Assert.Equal(FailureKind.Protocol, Assert.Throws<DeviceException>(() => client.Find(0x1004, false)).Kind);
        Assert.Throws<DeviceException>(() => client.Find(0x1004, false)); Assert.Equal(2, transport.Calls);
    }
    private sealed class ErrorTransport(byte report, byte code) : IReportTransport
    {
        public int Calls;
        public byte[] Exchange(byte feature, byte function, ReadOnlySpan<byte> parameters)
        {
            Calls++; var request = Wire.Request(1, feature, function, 7, parameters);
            return Wire.Match(request, [0x10, 1, report, feature, request[3], code, 0])!;
        }
        public void Dispose() { }
    }
}

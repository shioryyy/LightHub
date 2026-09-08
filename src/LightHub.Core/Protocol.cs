using System.Buffers.Binary;

namespace LightHub.Core;

public enum FailureKind { Disconnected, Timeout, Unsupported, Protocol, InvalidData, Conflict, ReadOnly, Identity, RecoveryRequired }
public sealed class DeviceException(FailureKind kind, string message, Exception? inner = null) : IOException(message, inner)
{
    public FailureKind Kind { get; } = kind;
}

public interface IReportTransport : IDisposable
{
    // Each instance represents one wireless slot; calls must be serialized by its owner.
    byte[] Exchange(byte feature, byte function, ReadOnlySpan<byte> parameters);
}

public static class Wire
{
    public static byte[] Request(byte index, byte feature, byte function, byte softwareId, ReadOnlySpan<byte> data)
    {
        if (function > 15 || softwareId is 0 or > 15 || data.Length > 16) throw new ArgumentOutOfRangeException(nameof(data));
        var bytes = new byte[data.Length <= 3 ? 7 : 20];
        bytes[0] = bytes.Length == 7 ? (byte)0x10 : (byte)0x11;
        bytes[1] = index; bytes[2] = feature; bytes[3] = (byte)(function << 4 | softwareId);
        data.CopyTo(bytes.AsSpan(4)); return bytes;
    }
    public static byte[]? Match(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
    {
        if (response.Length < 7 || response[0] is not (0x10 or 0x11) || (response[0] == 0x11 && response.Length < 20)) return null;
        if (response[1] != request[1]) return null;
        if (response[2] is 0xff or 0x8f && response[3] == request[2] && response[4] == request[3])
            throw new DeviceException(FailureKind.Protocol, $"HID++ error 0x{response[5]:X2}, feature {request[2]}, function {request[3] >> 4}.");
        return response[2] == request[2] && response[3] == request[3] ? response.Slice(4, response[0] == 0x10 ? 3 : 16).ToArray() : null;
    }
    public static int Be(ReadOnlySpan<byte> b) => b.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(b) : throw new InvalidDataException("Truncated integer.");
    public static byte[] Be(int value) => [(byte)(value >> 8), (byte)value];
    public static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xffff;
        foreach (var b in bytes) { crc ^= (ushort)(b << 8); for (int i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1); }
        return crc;
    }
    public static bool ValidCrc(ReadOnlySpan<byte> b) => b.Length >= 2 && Crc(b[..^2]) == Be(b[^2..]);
    public static bool Intact(ReadOnlySpan<byte> b) => ValidCrc(b) || (b.Length > 0 && b.IndexOfAnyExcept((byte)255) < 0);
    public static void UpdateCrc(Span<byte> b) => BinaryPrimitives.WriteUInt16BigEndian(b[^2..], Crc(b[..^2]));
}

public sealed class FeatureClient(IReportTransport transport) : IDisposable
{
    private readonly Dictionary<int, byte> cache = [];
    public byte Find(int id, bool required = true)
    {
        if (!cache.TryGetValue(id, out byte feature)) { feature = transport.Exchange(0, 0, Wire.Be(id))[0]; cache[id] = feature; }
        if (required && feature == 0) throw new DeviceException(FailureKind.Unsupported, $"HID++ feature 0x{id:X4} is unavailable.");
        return feature;
    }
    public byte[] Call(int id, byte function, params byte[] data) => transport.Exchange(Find(id), function, data);
    public void Dispose() => transport.Dispose();
}

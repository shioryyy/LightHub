using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HidSharp;
using LightHub.Core;
using LightHub.Application;
using DeviceException = LightHub.Core.DeviceException;

namespace LightHub.Hid;

public sealed record DeviceEndpoint(string Id, string Name, int ProductId, byte Slot, string PhysicalKey, IReadOnlyList<HidDevice> Channels)
{
    public override string ToString() => $"{Name} · {ProductId:X4} / {Slot}";
}
public sealed record DiscoveryResult(IReadOnlyList<DeviceEndpoint> Devices, IReadOnlyList<string> Warnings);
public static partial class HidDiscovery
{
    private static readonly HashSet<int> Receivers = [0xc52b, 0xc52f, 0xc531, 0xc532, 0xc534, 0xc537, 0xc539, 0xc53a, 0xc53f, 0xc541, 0xc542, 0xc545, 0xc547, 0xc548];
    public static DiscoveryResult Scan(CancellationToken cancel = default)
    {
        var endpoints = new List<(HidDevice Device, string Key)>(); var warnings = new List<string>();
        foreach (var device in DeviceList.Local.GetHidDevices(0x046d))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                if (device.GetMaxOutputReportLength() < 7 || device.GetMaxInputReportLength() < 7) continue;
                var reports = device.GetReportDescriptor().OutputReports.Select(r => r.ReportID).ToArray();
                if (reports.Contains((byte)0x10) || reports.Contains((byte)0x11)) endpoints.Add((device, PhysicalKey(device.DevicePath)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { warnings.Add($"HID {device.ProductID:X4}: {ex.Message}"); }
        }
        var result = new List<DeviceEndpoint>();
        foreach (var group in endpoints.GroupBy(x => x.Key))
        {
            using var receiverLease = new DeviceLease(group.Key, new TransactionStore().Root);
            var channels = group.Select(x => x.Device).ToArray(); int pid = channels[0].ProductID;
            // Probe each receiver slot separately; never treat receiver product ID as the mouse identity.
            byte[] slots = Receivers.Contains(pid) ? [1, 2, 3, 4, 5, 6] : [255];
            foreach (byte slot in slots)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    using var transport = new HidTransport(channels, slot); using var client = new FeatureClient(transport);
                    if (client.Find(0x8100, false) == 0) continue;
                    int length = client.Call(5, 0)[0]; if (length is < 1 or > 128) continue;
                    var name = new List<byte>(); while (name.Count < length) name.AddRange(client.Call(5, 1, (byte)name.Count).Take(Math.Min(16, length - name.Count)));
                    string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key + ":" + slot)))[..16];
                    result.Add(new(id, Encoding.UTF8.GetString(name.ToArray()).TrimEnd('\0'), pid, slot, group.Key, channels));
                }
                catch (DeviceException ex) when (ex.Kind is FailureKind.Protocol or FailureKind.Timeout or FailureKind.Unsupported) { if (slot is 1 or 255) warnings.Add($"HID {pid:X4}, slot {slot}: {ex.Message}"); }
                catch (IOException ex) { warnings.Add($"HID {pid:X4}: {ex.Message}"); }
            }
        }
        return new(result, warnings);
    }
    public static string PhysicalKey(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        return NormalizeWindowsPath(path);
    }
    public static string NormalizeWindowsPath(string path)
    {
        var parts = path.ToLowerInvariant().Split('#'); if (parts.Length != 4) return path;
        if (!CollectionPattern().IsMatch(parts[1])) return path;
        parts[1] = CollectionPattern().Replace(parts[1], ""); int n = parts[2].LastIndexOf('&'); if (n >= 0) parts[2] = parts[2][..n];
        return string.Join('#', parts);
    }
    [GeneratedRegex("&col[0-9]+", RegexOptions.IgnoreCase)] private static partial Regex CollectionPattern();
}

public sealed class HidTransport : IReportTransport
{
    private readonly Dictionary<byte, HidStream> outputs = [];
    private readonly List<(HidStream Stream, int InputSize)> streams = [];
    private readonly byte slot;
    private readonly byte softwareId = (byte)Random.Shared.Next(6, 15);
    private bool unusable;
    private int preferredInput;
    public HidTransport(IEnumerable<HidDevice> channels, byte slot)
    {
        this.slot = slot;
        try
        {
            foreach (var d in channels)
            {
                var reports = d.GetReportDescriptor().OutputReports.Select(r => r.ReportID).Where(id => id is 0x10 or 0x11).ToArray();
                if (reports.Length == 0) continue;
                var stream = d.Open(); stream.ReadTimeout = 15; stream.WriteTimeout = 2500; streams.Add((stream, d.GetMaxInputReportLength()));
                foreach (byte id in reports) outputs.TryAdd(id, stream);
            }
            if (!outputs.ContainsKey(0x11)) throw new IOException("HID++ long report channel is unavailable.");
        }
        catch { Dispose(); throw; }
    }
    public byte[] Exchange(byte feature, byte function, ReadOnlySpan<byte> parameters)
    {
        ObjectDisposedException.ThrowIf(unusable, this);
        var request = Wire.Request(slot, feature, function, softwareId, parameters);
        if (!outputs.TryGetValue(request[0], out var output)) { var longRequest = new byte[20]; request.CopyTo(longRequest, 0); longRequest[0] = 0x11; request = longRequest; output = outputs[0x11]; }
        try
        {
            output.Write(request);
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 2500)
            {
                // Replies often use the long-report collection even for short requests.
                // Remember the responding collection so every memory chunk avoids an idle read.
                for (int attempt = 0; attempt < streams.Count; attempt++)
                {
                    int index = (preferredInput + attempt) % streams.Count;
                    var (stream, size) = streams[index];
                    var response = new byte[Math.Max(size, 20)]; int count;
                    try { count = stream.Read(response); } catch (TimeoutException) { continue; }
                    var matched = Wire.Match(request, response.AsSpan(0, count));
                    if (matched is not null) { preferredInput = index; return matched; }
                }
            }
            throw new DeviceException(FailureKind.Timeout, "Mouse response timed out. Wake or reconnect the device.");
        }
        catch (DeviceException ex) when (ex.Kind == FailureKind.Protocol) { throw; }
        catch (Exception ex) when (ex is IOException or TimeoutException) { unusable = true; throw new DeviceException(ex is TimeoutException ? FailureKind.Timeout : FailureKind.Disconnected, ex.Message, ex); }
    }
    public void Dispose() { foreach (var (stream, _) in streams) stream.Dispose(); streams.Clear(); outputs.Clear(); unusable = true; }
}

public sealed class DeviceLease : IDisposable
{
    private readonly FileStream file;
    public DeviceLease(string key, string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "locks"));
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        try { file = new FileStream(Path.Combine(root, "locks", name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new DeviceException(FailureKind.Conflict, "Another LightHub process is accessing this receiver.", ex); }
    }
    public void Dispose() => file.Dispose();
}
public static class Hardware
{
    public static OnboardDevice Open(DeviceEndpoint endpoint) => new(new HidTransport(endpoint.Channels, endpoint.Slot), connection: $"{endpoint.ProductId:X4}:{(endpoint.Slot == 255 ? "direct" : "receiver")}");
    public static void CheckCompetingSoftware()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process) { try { if (process.ProcessName.StartsWith("lghub", StringComparison.OrdinalIgnoreCase)) throw new DeviceException(FailureKind.Conflict, "Exit G HUB before writing device memory."); } catch (InvalidOperationException) { } }
        }
    }
}

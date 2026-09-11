using System.Diagnostics;
using System.Text.Json;
using LightHub.Application;
using LightHub.Core;
using LightHub.Hid;

var store = new TransactionStore();
try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("LightHub CLI\n  list\n  inspect <endpoint-id>\n  trigger-inspect <endpoint-id> (read only; no event capture)\n  backup <endpoint-id> <path>\n  restore <endpoint-id> <path> --yes\n  save <endpoint-id> <sector> <preset.lhpreset> --yes\n  activate <endpoint-id> <sector> [--enable-disabled] --yes\n  smoke <endpoint-id> --write-inactive-and-restore\n  dpi-smoke <endpoint-id>\nIDs come from list. Operation permissions are independent. Backups contain device identity."); return 0;
    }
    var scan = HidDiscovery.Scan();
    foreach (var warning in scan.Warnings) Console.Error.WriteLine(warning);
    if (args[0] == "list") { Console.WriteLine(JsonSerializer.Serialize(scan.Devices.Select(d => new { d.Id, d.Name, productId = d.ProductId.ToString("X4"), d.Slot }), Json.Options)); return 0; }
    if (args.Length < 2) throw new ArgumentException("Endpoint ID required.");
    var endpoint = scan.Devices.SingleOrDefault(d => d.Id == args[1]) ?? throw new IOException("Endpoint not found. Run list again.");
    if (args[0] == "trigger-inspect" && args.Length == 2)
    {
        using var lease = new DeviceLease(endpoint.PhysicalKey, store.Root);
        using var transport = new HidTransport(endpoint.Channels, endpoint.Slot);
        Console.WriteLine(JsonSerializer.Serialize(new { endpoint.Name, connectionProductId = endpoint.ProductId.ToString("X4"), endpoint.Slot, Inspection = TriggerInspector.Inspect(transport) }, Json.Options));
        return 0;
    }
    using var session = new DeviceSession(new HidDeviceAccess(endpoint, store.Root), store);
    if (args[0] == "restore" && args.Length == 4 && args[3] == "--yes") { await session.Restore(args[2], Console.WriteLine); Console.WriteLine("Restore verified."); return 0; }
    var read = await session.Read(); var snapshot = read.Snapshot;
    switch (args[0])
    {
        case "inspect" when args.Length == 2:
            Console.WriteLine(JsonSerializer.Serialize(new { snapshot.Identity.Name, snapshot.Identity.ProductIds, snapshot.Identity.Firmware, snapshot.Identity.DeviceType, snapshot.Layout, read.Support, read.Operations, read.DpiCaps, read.Rates, read.Telemetry, snapshot.Mode, snapshot.ActiveSector, snapshot.DpiIndex, profiles = snapshot.Directory() }, Json.Options)); break;
        case "backup" when args.Length == 3: BackupFile.Save(args[2], snapshot); Console.WriteLine("Backup verified: " + Path.GetFullPath(args[2])); break;
        case "save" when args.Length == 5 && args[4] == "--yes":
            int target = int.Parse(args[2]); var preset = LocalAssets.Read(args[3]);
            var mapped = LocalAssets.Map(preset, read.Support.ModelId, MouseProfile.Decode(snapshot.Sectors[target], snapshot.Layout));
            await session.Save(snapshot, target, mapped, report: Console.WriteLine); Console.WriteLine("Saved and verified; active state preserved."); break;
        case "activate" when (args.Length == 4 && args[3] == "--yes") || (args.Length == 5 && args[3] == "--enable-disabled" && args[4] == "--yes"):
            await session.Activate(snapshot, int.Parse(args[2]), Console.WriteLine, enableDisabled: args.Length == 5); Console.WriteLine("Activation verified."); break;
        case "dpi-smoke" when args.Length == 2:
            int originalDpi = read.Telemetry.Dpi ?? throw new InvalidOperationException("Current DPI unavailable.");
            int testDpi = originalDpi + read.DpiCaps!.Step;
            if (testDpi == originalDpi || !read.DpiCaps.Contains(testDpi)) throw new InvalidOperationException("No safe test DPI increment.");
            var liveTimer = Stopwatch.StartNew();
            try { await session.Preview(snapshot, testDpi); Console.WriteLine($"Current DPI verified in {liveTimer.Elapsed.TotalMilliseconds:F0} ms (session open included)."); }
            finally { if (!await session.EndPreview()) throw new IOException("Preview state changed; automatic restore withheld."); }
            if (!(await session.Read()).Snapshot.SameState(snapshot)) throw new IOException("Onboard snapshot changed during current DPI test.");
            Console.WriteLine("PASS: current DPI changed and restored; onboard memory and active state unchanged."); break;
        case "smoke" when args.Length == 3 && args[2] == "--write-inactive-and-restore":
            var entry = snapshot.Directory().LastOrDefault(p => !p.Enabled && p.Sector != snapshot.ActiveSector) ?? throw new InvalidOperationException("No inactive profile available.");
            var profile = MouseProfile.Decode(snapshot.Sectors[entry.Sector], snapshot.Layout);
            int changedDpi = profile.Dpi[0] + read.DpiCaps!.Step;
            if (changedDpi == profile.Dpi[0] || !read.DpiCaps.Contains(changedDpi)) throw new InvalidOperationException("No safe test DPI increment.");
            profile.Dpi[0] = changedDpi; profile = profile with { Rate = read.Rates.First(r => r != profile.Rate) }; profile.Bindings[^1] = [128, 2, 1, 6];
            string baseline = store.SaveBackup(snapshot); Console.WriteLine("Baseline: " + baseline);
            var timer = Stopwatch.StartNew();
            try { await session.Save(snapshot, entry.Sector, profile, report: Console.WriteLine); Console.WriteLine($"Modified profile verified in {timer.Elapsed.TotalMilliseconds:F0} ms."); }
            finally { timer.Restart(); await session.Restore(baseline, Console.WriteLine); Console.WriteLine($"Baseline restored and verified in {timer.Elapsed.TotalMilliseconds:F0} ms."); }
            if (!(await session.Read()).Snapshot.SameState(snapshot)) throw new IOException("Restored snapshot differs.");
            Console.WriteLine("PASS: modified inactive profile and restored complete baseline."); break;
        default: throw new ArgumentException("Unknown command or arguments. See --help.");
    }
    return 0;
}
catch (Exception ex) { store.Log(ex); Console.Error.WriteLine(ex.Message); return 1; }

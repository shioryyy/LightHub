using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LightHub.Application;
using LightHub.Core;
using LightHub.Hid;

// Engineering-only executable. Not referenced by Desktop/CLI or distributed in
// their packages. No writes without an explicit execute flag and an exact GPW1
// tuple that already has ordinary profile write/recovery evidence.
if (args.Length == 0)
{
    Console.WriteLine("Hardware validation (not a compatibility grant)\n  plan ENDPOINT SECTOR\n  activation ENDPOINT SECTOR --execute-and-restore [--hold-seconds 0..120]\n  restore ENDPOINT BACKUP --execute\nRun from the repository root. Do not force-terminate a hardware experiment.");
    return 0;
}

var store = new TransactionStore();
try
{
    bool planOnly = args[0] == "plan" && args.Length == 3;
    bool activation = args[0] == "activation" && args.Length is 4 or 6 && args[3] == "--execute-and-restore";
    bool restore = args[0] == "restore" && args.Length == 4 && args[3] == "--execute";
    if (!planOnly && !activation && !restore) throw new ArgumentException("Unknown command. No device operation was started.");
    int holdSeconds = 0;
    if (activation && args.Length == 6 && (args[4] != "--hold-seconds" || !int.TryParse(args[5], out holdSeconds) || holdSeconds is < 0 or > 120))
        throw new ArgumentException("Hold time must be 0..120 seconds.");
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This engineering procedure is limited to Windows GPW1.");
    var scan = HidDiscovery.Scan();
    foreach (var warning in scan.Warnings) Console.Error.WriteLine(warning);
    var endpoint = scan.Devices.SingleOrDefault(d => d.Id == args[1]) ?? throw new IOException("Endpoint unavailable. Wake the mouse and run the ordinary CLI list command.");
    using var lease = new DeviceLease(endpoint.PhysicalKey, store.Root);
    DeviceSnapshot baseline; DpiCapabilities caps; int[] rates;
    using (var device = Hardware.Open(endpoint))
    {
        baseline = device.ReadSnapshot(); caps = device.DpiCaps ?? throw new InvalidDataException("DPI capabilities missing."); rates = device.Rates;
        if (device.Support.ModelId != "g-pro-wireless" || !device.Support.CanWrite || endpoint.ProductId != 0xc539 || baseline.Identity.Firmware != "BOT 74.02.0026")
            throw new InvalidOperationException("No matching GPW1/firmware/C539 profile write evidence. Validation permission was not granted.");
    }
    if (store.Pending().Any(r => r.UnitId == baseline.Identity.UnitId) && !restore)
        throw new InvalidOperationException("There is an unfinished transaction. Recover it before starting another experiment.");

    if (planOnly || activation)
    {
        if (!int.TryParse(args[2], out int sector) || sector == baseline.ActiveSector) throw new ArgumentException("Choose a different, already populated profile sector.");
        var plan = SlotActivation.Prepare(baseline, sector, caps, rates, true);
        Console.WriteLine(JsonSerializer.Serialize(new { procedure = "activation-and-restore", baseline.Identity.Name, baseline.Identity.Firmware,
            baseline.Mode, baseline.ActiveSector, baseline.DpiIndex, baseline.SensorDpi, target = new { plan.Slot, plan.Sector, plan.EnablesSlot, plan.TargetDpi },
            profileBytesChanged = false, directoryWrite = plan.EnablesSlot, holdSeconds, productionPermissionGranted = false }, Json.Options));
        if (planOnly) return 0;
    }

    // This temporary rule is confined to this process. Production devices.json is
    // not changed. Every write still uses the same identity, conflict, backup,
    // journal and complete read-back checks as the ordinary application.
    var catalog = new DeviceCatalog(DeviceCatalog.Load().Rules.Select(r => r.Id == "g-pro-wireless"
        ? r with { VerifiedOperations = r.VerifiedOperations.Concat(new[] { nameof(DeviceOperation.Activate), nameof(DeviceOperation.EnableProfile) }).Distinct().ToArray() }
        : r));
    using var validated = new OnboardDevice(new HidTransport(endpoint.Channels, endpoint.Slot), catalog, "windows", "C539:receiver");
    Hardware.CheckCompetingSoftware(); validated.EnsureWritable(baseline);
    if (restore)
    {
        var desired = BackupFile.Load(args[2]);
        new TransactionEngine(store).Restore(validated, desired, Console.WriteLine);
        var restored = validated.ReadSnapshot();
        if (!restored.SameState(desired) || restored.SensorDpi != desired.SensorDpi) throw new IOException("Recovery comparison failed.");
        Console.WriteLine("Engineering recovery verified. No production capability was enabled."); return 0;
    }

    string recovery = store.SaveBackup(baseline);
    Console.WriteLine("Recovery backup (private): " + recovery);
    Console.WriteLine("The selected profile will become active temporarily. Do not unplug or power off during the procedure. Ctrl+C ends the hold and attempts recovery.");
    using var stop = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stop.Cancel(); };
    Console.CancelKeyPress += cancelHandler;
    bool needsRecovery = false, applied = false, recovered = false;
    var recordId = Guid.NewGuid().ToString("N");
    var started = DateTimeOffset.UtcNow;
    try
    {
        try
        {
            SlotActivation.Execute(validated, store, baseline, int.Parse(args[2]), true, Console.WriteLine);
            needsRecovery = applied = true;
            store.Record(new(recordId, baseline.Identity.UnitId, recovery, recovery, "pending", started, [0], "Engineering activation awaits baseline restoration."));
            Console.WriteLine("Temporary activation read-back verified. Remaining hold: " + holdSeconds + " seconds.");
            if (holdSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(holdSeconds), stop.Token);
        }
        catch (DeviceException ex) when (ex.Kind == FailureKind.RecoveryRequired) { needsRecovery = true; throw; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (needsRecovery)
            {
                Hardware.CheckCompetingSoftware();
                new TransactionEngine(store).Restore(validated, baseline, Console.WriteLine);
                var final = validated.ReadSnapshot();
                recovered = final.SameState(baseline) && final.SensorDpi == baseline.SensorDpi;
                if (!recovered) throw new IOException("Final baseline comparison failed. Keep the recovery backup.");
            }
        }
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        string assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location)));
        string directory = Path.GetFullPath("artifacts/hardware-validation"); Directory.CreateDirectory(directory);
        string result = Path.Combine(directory, "activation-" + recordId + ".json");
        AtomicFile.Write(result, JsonSerializer.Serialize(new { schemaVersion = 1, started, ended = DateTimeOffset.UtcNow, baseline.Identity.Name,
            baseline.Identity.Firmware, connection = "C539:receiver", platform = Environment.OSVersion.VersionString, applied, recovered,
            originalActiveSector = baseline.ActiveSector, targetSector = int.Parse(args[2]), originalDpi = baseline.SensorDpi, holdSeconds,
            physicalInputVerified = false, powerCycleVerified = false, productionPermissionGranted = false, assemblyHash }, Json.Options));
        Console.WriteLine("Read-back evidence: " + result);
    }
    Console.WriteLine("PASS: temporary activation and complete baseline restoration. Physical input, power-cycle and sleep/wake still need separate verification.");
    return 0;
}
catch (Exception ex) { store.Log(ex); Console.Error.WriteLine(ex.Message); return 1; }

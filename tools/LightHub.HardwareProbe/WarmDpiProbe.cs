using System.Diagnostics;
using System.Text.Json;
using LightHub.Core;

namespace LightHub.HardwareProbe;

public sealed record WarmDpiPlan(int InitialDpi, int TestDpi, int Cycles);
public sealed record WarmDpiResult(int CompletedCycles, int InitialDpi, int TestDpi, IReadOnlyList<double> SamplesMs, double? P95Ms, bool Restored, string? MarkerPath);

// Engineering warm-connection timing. Only temporary sensor DPI is written, through
// the ordinary SetCurrentDpi active-state and read-back checks; there is no flash
// transaction. A pending marker is durable between the first write and the confirmed
// restore so an interrupted run cannot be mistaken for a clean one, and a restore
// that cannot be confirmed is recorded instead of retried blindly across reconnects.
public static class WarmDpiProbe
{
    public static WarmDpiPlan Plan(DeviceSnapshot baseline, DpiCapabilities caps, int cycles)
    {
        if (baseline.SensorDpi is not { } initialDpi) throw new InvalidDataException("Current DPI is unknown.");
        int testDpi = initialDpi + caps.Step;
        if (!caps.Contains(testDpi)) testDpi = initialDpi - caps.Step;
        if (!caps.Contains(testDpi) || testDpi == initialDpi) throw new InvalidOperationException("No safe DPI step away from the current value.");
        return new(initialDpi, testDpi, cycles);
    }

    public static WarmDpiResult Run(OnboardDevice device, DeviceSnapshot baseline, DpiCapabilities caps, int cycles, string markerDirectory, Action<string>? report = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(markerDirectory);
        foreach (var leftover in Directory.EnumerateFiles(markerDirectory, "warm-dpi-pending-*.json"))
            throw new IOException($"An earlier warm-dpi run left an unfinished marker: {leftover}. Verify the device DPI before retrying; resolve it manually, never by a blind write.");
        var plan = Plan(baseline, caps, cycles);
        string marker = Path.Combine(markerDirectory, "warm-dpi-pending-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicFile.Write(marker, JsonSerializer.Serialize(new { plan.InitialDpi, plan.TestDpi, plan.Cycles, status = "pending", started = DateTimeOffset.UtcNow }, Json.Options));
        report?.Invoke($"Temporary DPI {plan.InitialDpi} <-> {plan.TestDpi}, {cycles} bounded set/restore cycles. Do not power off.");
        var samples = new List<double>(cycles);
        var timer = new Stopwatch();
        int knownDpi = plan.InitialDpi;
        bool writeStateUnknown = false, canceled = false;
        try
        {
            for (int i = 0; i < cycles; i++)
            {
                cancel.ThrowIfCancellationRequested();
                timer.Restart();
                device.SetCurrentDpi(baseline, plan.TestDpi);
                knownDpi = plan.TestDpi;
                device.SetCurrentDpi(baseline, plan.InitialDpi);
                knownDpi = plan.InitialDpi;
                timer.Stop();
                samples.Add(timer.Elapsed.TotalMilliseconds);
                report?.Invoke($"cycle {i + 1}/{cycles}: {timer.Elapsed.TotalMilliseconds:F0} ms");
            }
        }
        catch (OperationCanceledException) { canceled = true; }
        catch (Exception ex) { writeStateUnknown = true; report?.Invoke($"Cycle failed after {samples.Count} completed cycles: {ex.Message}"); }
        finally
        {
            // SetCurrentDpi re-verifies mode, active slot and DPI index against the
            // baseline before writing, so this attempt cannot touch a device whose
            // runtime state no longer matches; a failure here is recorded, not retried.
            if (writeStateUnknown || knownDpi != plan.InitialDpi)
            {
                try { device.SetCurrentDpi(baseline, plan.InitialDpi); knownDpi = plan.InitialDpi; writeStateUnknown = false; }
                catch (Exception ex) { report?.Invoke($"Restore attempt failed: {ex.Message}"); }
            }
            if (!writeStateUnknown && knownDpi == plan.InitialDpi)
            {
                try { File.Delete(marker); marker = ""; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report?.Invoke($"Pending marker could not be removed: {ex.Message}"); }
            }
            else
            {
                string unrestored = Path.Combine(Path.GetDirectoryName(marker)!, Path.GetFileNameWithoutExtension(marker) + "-unrestored.json");
                try { AtomicFile.Write(unrestored, JsonSerializer.Serialize(new { plan.InitialDpi, plan.TestDpi, plan.Cycles, completedCycles = samples.Count, status = writeStateUnknown ? "unconfirmed" : "unrestored", canceled, recorded = DateTimeOffset.UtcNow }, Json.Options)); if (unrestored != marker) File.Delete(marker); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report?.Invoke($"Unrestored marker could not be written: {ex.Message}"); }
                marker = unrestored;
            }
        }
        double? p95 = samples.Count > 0 ? samples.Order().ElementAt((int)Math.Ceiling(0.95 * samples.Count) - 1) : null;
        return new(samples.Count, plan.InitialDpi, plan.TestDpi, samples, p95, !writeStateUnknown && knownDpi == plan.InitialDpi, marker.Length > 0 ? marker : null);
    }
}

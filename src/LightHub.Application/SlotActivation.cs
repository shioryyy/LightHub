using LightHub.Core;

namespace LightHub.Application;

public sealed record SlotActivationPlan(int Slot, int Sector, bool EnablesSlot, int TargetDpi, DeviceSnapshot Desired);

public static class SlotActivation
{
    // Pure preflight: this can explain a currently unavailable operation without
    // granting permission or touching HID. Permission is checked when executing.
    public static SlotActivationPlan Prepare(DeviceSnapshot baseline, int sector, DpiCapabilities caps, int[] rates, bool enableDisabled)
    {
        baseline.Validate();
        var entry = baseline.Directory().SingleOrDefault(e => e.Sector == sector) ?? throw new InvalidDataException("Not a declared profile slot.");
        if (!entry.Enabled && !enableDisabled) throw new DeviceException(FailureKind.Unsupported, "This slot is disabled. Explicitly choose enable and activate.");
        var raw = baseline.Sectors[sector]; var profile = MouseProfile.Decode(raw, baseline.Layout);
        // Reuse live range, consecutive DPI and primary-click protection. This is
        // activation of an existing profile, so no normalization is allowed.
        if (!raw.SequenceEqual(profile.Encode(raw, baseline.Layout, caps, rates))) throw new InvalidDataException("Profile requires editing before activation.");
        if (profile.Bindings.Any(b => !MouseProfile.IsStandardBinding(b))) throw new DeviceException(FailureKind.Unsupported, "This slot contains unverified actions; activation is withheld.");
        var desired = baseline.Copy() with { Mode = 1, ActiveSector = sector, DpiIndex = profile.DefaultIndex, SensorDpi = profile.Dpi[profile.DefaultIndex] };
        if (!entry.Enabled)
        {
            desired.Sectors[0][(entry.Slot - 1) * 4 + 2] = 1;
            Wire.UpdateCrc(desired.Sectors[0]);
        }
        desired.Validate();
        return new(entry.Slot, sector, !entry.Enabled, profile.Dpi[profile.DefaultIndex], desired);
    }

    public static TransactionResult Execute(OnboardDevice device, TransactionStore store, DeviceSnapshot baseline, int sector,
        bool enableDisabled = false, Action<string>? report = null)
    {
        device.EnsureOperation(DeviceOperation.Activate, baseline);
        var plan = Prepare(baseline, sector, device.DpiCaps ?? throw new DeviceException(FailureKind.Unsupported, "DPI capabilities unavailable."), device.Rates, enableDisabled);
        if (plan.EnablesSlot) device.EnsureOperation(DeviceOperation.EnableProfile, baseline);
        return new TransactionEngine(store).ApplyVerified(device, baseline, plan.Desired, report);
    }

    internal static bool DirectoryFlagsDiffer(DeviceSnapshot before, DeviceSnapshot after) =>
        // Recovery may start from a corrupt directory. The desired snapshot has
        // already been validated and supplies the trusted flag positions.
        after.Directory().Any(entry => before.Sectors[0][(entry.Slot - 1) * 4 + 2] != after.Sectors[0][(entry.Slot - 1) * 4 + 2]);
}

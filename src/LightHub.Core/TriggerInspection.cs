namespace LightHub.Core;

public sealed record TriggerFeature(int Id, byte? Index, byte? Version, string Status);
public sealed record TriggerControl(int Cid, int TaskId, byte Flags, byte Position, byte Group, byte GroupMask, byte AdditionalFlags,
    bool Divertable, bool CurrentlyDiverted, int RemappedCid);
public sealed record TriggerInspection(IReadOnlyList<TriggerFeature> Features, IReadOnlyList<TriggerControl> Controls, IReadOnlyList<string> Warnings)
{
    public bool ReadOnly => true;
    public bool ExecutionVerified => false;
}

// Read-only preparation for a trigger experiment. Protocol facts: better-logihub
// 71bf2b0/src/specialkeys.rs (MIT, attribution in THIRD-PARTY-NOTICES.md).
// No diversion, remapping, event subscription or input injection is performed here.
public static class TriggerInspector
{
    public static TriggerInspection Inspect(IReportTransport transport, CancellationToken cancel = default)
    {
        var features = new List<TriggerFeature>(); var controls = new List<TriggerControl>(); var warnings = new List<string>();
        foreach (int id in new[] { 0x1b00, 0x1b01, 0x1b02, 0x1b03, 0x1b04, 0x8010 })
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var reply = transport.Exchange(0, 0, Wire.Be(id));
                if (reply.Length < 3) throw new InvalidDataException("Truncated feature lookup.");
                features.Add(new(id, reply[0], reply[2], reply[0] == 0 ? "absent" : "present"));
            }
            catch (DeviceException ex) when (ex.Kind == FailureKind.Protocol)
            {
                // An error response is not evidence that a feature is absent.
                features.Add(new(id, null, null, "query-error")); warnings.Add($"0x{id:X4}: {ex.Message}");
            }
        }
        if (features.Single(f => f.Id == 0x1b04) is { Status: "present", Index: { } index })
        {
            var countReply = transport.Exchange(index, 0, []);
            if (countReply.Length < 1 || countReply[0] > 64) throw new InvalidDataException("Control count exceeds the read-only inspector limit (64).");
            var seen = new HashSet<int>();
            for (int i = 0; i < countReply[0]; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var info = transport.Exchange(index, 1, [(byte)i]);
                if (info.Length < 9) throw new InvalidDataException("Truncated control information.");
                int cid = Wire.Be(info);
                if (!seen.Add(cid)) throw new InvalidDataException("Duplicate control ID.");
                var reporting = transport.Exchange(index, 2, Wire.Be(cid));
                if (reporting.Length < 6 || Wire.Be(reporting) != cid) throw new InvalidDataException("Control reporting identity mismatch.");
                controls.Add(new(cid, Wire.Be(info.AsSpan(2)), info[4], info[5], info[6], info[7], info[8],
                    (info[4] & 0x20) != 0, (reporting[2] & 1) != 0, Wire.Be(reporting.AsSpan(3))));
            }
        }
        warnings.Add("Capabilities alone do not prove physical triggers, suppression, release, reconnect behavior or software macro execution.");
        return new(features, controls, warnings);
    }
}

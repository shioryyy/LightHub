using System.Buffers.Binary;

namespace LightHub.Core;

public sealed record OnboardMacroStep(string Kind, int? DelayMs = null, byte Modifiers = 0, byte Usage = 0, ushort ConsumerUsage = 0, byte? Opcode = null)
{
    public override string ToString() => Describe();
    public string Describe()
    {
        switch (Kind)
        {
            case "key": return OnboardMacroFormat.KeyName(Modifiers, Usage);
            case "key-press": return OnboardMacroFormat.KeyName(Modifiers, Usage) + " down";
            case "key-release": return OnboardMacroFormat.KeyName(Modifiers, Usage) + " up";
            case "delay": return $"wait {DelayMs} ms";
            case "consumer": return "consumer " + OnboardMacroFormat.ConsumerName(ConsumerUsage);
            case "consumer-press": return "consumer " + OnboardMacroFormat.ConsumerName(ConsumerUsage) + " down";
            case "consumer-release": return "consumer " + OnboardMacroFormat.ConsumerName(ConsumerUsage) + " up";
            case "wait-for-release": return "wait for release";
            case "unknown": return $"unknown opcode 0x{Opcode:X2}";
            default: return Kind;
        }
    }
}

// Read-only view of one onboard macro. State explains why decoding stopped:
// complete reached the END marker, blank is a never-written 0xFF sector, anything
// else reports how far strict parsing got. Nothing here ever rewrites bytes.
public sealed record OnboardMacro(string State, IReadOnlyList<OnboardMacroStep> Steps)
{
    public override string ToString() => State + " (" + Steps.Count + " steps)";
}

// Onboard macro format 1 (memory model 1): bindings reference a sector and payload
// offset; the sector payload holds a byte stream of events terminated by 0xFF.
// Event shapes follow the validated reference implementation (better-logihub, MIT).
public static class OnboardMacroFormat
{
    private const int MaxEvents = 4096;

    public static bool IsMacroPointer(byte[] binding) => binding.Length == 4 && binding[0] == 0 && !binding.All(b => b == 0);
    public static ushort PointerSector(byte[] binding) => BinaryPrimitives.ReadUInt16BigEndian(binding.AsSpan(0, 2));
    public static ushort PointerOffset(byte[] binding) => BinaryPrimitives.ReadUInt16BigEndian(binding.AsSpan(2, 2));

    public static ushort[] MacroSectorIds(DeviceSnapshot snapshot)
    {
        var profileSectors = snapshot.Directory().Select(e => e.Sector).ToHashSet();
        int last = profileSectors.Count == 0 ? 0 : profileSectors.Max();
        return Enumerable.Range(last + 1, snapshot.Layout.SectorCount - last - 1).Where(s => s != 0 && !profileSectors.Contains(s)).Select(s => (ushort)s).ToArray();
    }

    public static OnboardMacro Decode(DeviceSnapshot snapshot, ushort[] macroSectors, ushort startSector, ushort startOffset)
    {
        var steps = new List<OnboardMacroStep>();
        var events = new List<(byte Opcode, byte First, byte Second)>();
        if (!macroSectors.Contains(startSector)) return new("outside-macro-range", steps);
        if (snapshot.Sectors.TryGetValue(startSector, out var startData) && startData.All(b => b == 0xFF))
            return new("blank", steps);
        var visited = new HashSet<(ushort, int)>();
        ushort sector = startSector;
        int offset = startOffset;
        var blank = new Func<byte[], bool>(data => data.All(b => b == 0xFF));
        while (true)
        {
            if (events.Count > MaxEvents || steps.Count > MaxEvents) { Collapse(events, steps); return new("truncated", steps); }
            if (!visited.Add((sector, offset))) { Collapse(events, steps); return new("loop", steps); }
            if (!snapshot.Sectors.TryGetValue(sector, out var data)) { Collapse(events, steps); return new("invalid-crc", steps); }
            if (!Wire.ValidCrc(data))
            {
                if (!blank(data)) { Collapse(events, steps); return new("invalid-crc", steps); }
            }
            int payloadEnd = data.Length - 2;
            if (offset >= payloadEnd) { Collapse(events, steps); return new("truncated", steps); }
            byte opcode = data[offset];
            byte first = offset + 1 < data.Length ? data[offset + 1] : (byte)0;
            byte second = offset + 2 < data.Length ? data[offset + 2] : (byte)0;
            switch (opcode)
            {
                case 0xFF:
                    Collapse(events, steps);
                    return new("complete", steps);
                case 0x01:
                    events.Add((0x01, 0, 0));
                    offset += 1;
                    break;
                case 0x40 or 0x43 or 0x44 or 0x45 or 0x46:
                    if (offset + 3 > payloadEnd) { Collapse(events, steps); return new("truncated", steps); }
                    events.Add((opcode, first, second));
                    offset += 3;
                    break;
                case 0x60:
                    if (offset + 5 > payloadEnd) { Collapse(events, steps); return new("truncated", steps); }
                    if (data[offset + 3] != 0 || data[offset + 4] != 0) { Collapse(events, steps); return new("unsupported-opcode", steps); }
                    ushort next = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 1, 2));
                    if (!macroSectors.Contains(next)) { Collapse(events, steps); return new("unsupported-opcode", steps); }
                    offset = 0;
                    sector = next;
                    break;
                default:
                    Collapse(events, steps);
                    steps.Add(new OnboardMacroStep("unknown", Opcode: opcode));
                    return new("unsupported-opcode", steps);
            }
        }
    }

    // A press immediately followed by its matching release collapses into one step.
    private static void Collapse(List<(byte Opcode, byte First, byte Second)> events, List<OnboardMacroStep> steps)
    {
        for (int i = 0; i < events.Count; i++)
        {
            var (opcode, first, second) = events[i];
            var next = i + 1 < events.Count ? events[i + 1] : ((byte Opcode, byte First, byte Second)?)null;
            switch (opcode)
            {
                case 0x43 when next is { Opcode: 0x44 } n && n.First == first && n.Second == second:
                    steps.Add(new("key", Modifiers: first, Usage: second)); i++;
                    break;
                case 0x43: steps.Add(new("key-press", Modifiers: first, Usage: second)); break;
                case 0x44: steps.Add(new("key-release", Modifiers: first, Usage: second)); break;
                case 0x40: steps.Add(new("delay", DelayMs: BinaryPrimitives.ReadUInt16BigEndian([first, second]))); break;
                case 0x45 when next is { Opcode: 0x46 } n && n.First == first && n.Second == second:
                    steps.Add(new("consumer", ConsumerUsage: BinaryPrimitives.ReadUInt16BigEndian([first, second]))); i++;
                    break;
                case 0x45: steps.Add(new("consumer-press", ConsumerUsage: BinaryPrimitives.ReadUInt16BigEndian([first, second]))); break;
                case 0x46: steps.Add(new("consumer-release", ConsumerUsage: BinaryPrimitives.ReadUInt16BigEndian([first, second]))); break;
                case 0x01: steps.Add(new("wait-for-release")); break;
                default: steps.Add(new("unknown", Opcode: opcode)); break;
            }
        }
        events.Clear();
    }

    private static readonly string[] ModifierNames = ["ctrl", "shift", "alt", "win", "rctrl", "rshift", "ralt", "rwin"];
    public static string KeyName(byte modifiers, byte usage) => (Prefix(modifiers) + BaseKeyName(usage)) is { Length: > 0 } name ? name : "key 0x" + usage.ToString("X2");
    private static string Prefix(byte modifiers)
    {
        var parts = Enumerable.Range(0, 8).Where(i => (modifiers & (1 << i)) != 0).Select(i => ModifierNames[i] + "+");
        return string.Concat(parts);
    }
    private static string BaseKeyName(byte usage) => usage switch
    {
        >= 0x04 and <= 0x1D => ((char)('a' + usage - 0x04)).ToString(),
        >= 0x1E and <= 0x27 => ((char)('1' + usage - 0x1E)).ToString(),
        0x28 => "enter", 0x29 => "esc", 0x2A => "backspace", 0x2B => "tab", 0x2C => "space",
        0x2D => "-", 0x2E => "=", 0x2F => "[", 0x30 => "]", 0x31 => "\\", 0x33 => ";", 0x34 => "'",
        0x35 => "`", 0x36 => ",", 0x37 => ".", 0x38 => "/",
        >= 0x3A and <= 0x45 => "f" + (usage - 0x3A + 1),
        0x4F => "right", 0x50 => "left", 0x51 => "down", 0x52 => "up",
        _ => "",
    };
    public static string ConsumerName(ushort usage) => usage switch
    {
        0xB6 => "prev", 0xB7 => "stop", 0xB8 => "next", 0xCD => "play",
        0xE2 => "mute", 0xE9 => "vol+", 0xEA => "vol-", 0x30 => "power",
        _ => "usage 0x" + usage.ToString("X2"),
    };
}

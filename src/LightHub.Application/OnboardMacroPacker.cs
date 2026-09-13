using LightHub.Core;

namespace LightHub.Application;

// Compiles a validated local keyboard macro into onboard macro-format-1 bytes:
// the exact stream a future, separately validated write driver would place into
// macro sectors. Pure functions; nothing here touches a device. Capacity follows
// the packing rules of the validated reference implementation (2-byte record
// alignment, a 5-byte reserve before sector jumps, 0xFF terminator and padding).
public static class OnboardMacroPacker
{
    public sealed record PackedMacro(IReadOnlyDictionary<int, byte[]> Sectors, ushort StartSector, int EventBytes);

    private static List<byte[]> EventBytes(LocalMacro macro)
    {
        var events = new List<byte[]>(macro.Events.Length);
        foreach (var e in macro.Events)
        {
            switch (e.Kind)
            {
                case "down" when e.Usage is >= 0xe0 and <= 0xe7:
                    events.Add([0x43, (byte)(1 << (e.Usage - 0xe0)), 0x00]);
                    break;
                case "down":
                    events.Add([0x43, 0x00, (byte)e.Usage]);
                    break;
                case "up" when e.Usage is >= 0xe0 and <= 0xe7:
                    events.Add([0x44, (byte)(1 << (e.Usage - 0xe0)), 0x00]);
                    break;
                case "up":
                    events.Add([0x44, 0x00, (byte)e.Usage]);
                    break;
                case "delay":
                    events.Add([0x40, (byte)(e.DelayMs >> 8), (byte)e.DelayMs]);
                    break;
                default:
                    throw new MacroLibraryException("Format");
            }
        }
        return events;
    }

    public static int EncodedLength(LocalMacro macro)
    {
        int bytes = EventBytes(macro).Sum(e => e.Length);
        bytes += 1;             // 0xFF terminator
        bytes += bytes % 2;     // padding keeps records 2-byte aligned
        return bytes;
    }

    public static PackedMacro Pack(LocalMacro macro, IReadOnlyList<ushort> macroSectorIds, int sectorSize)
    {
        MacroValidator.ValidateShape(macro);
        if (macroSectorIds.Count == 0 || sectorSize < 8) throw new MacroLibraryException("Capacity");
        int payloadSize = sectorSize - 2;
        var sectors = macroSectorIds.ToDictionary(id => (int)id, _ => Enumerable.Repeat((byte)0xFF, sectorSize).ToArray());
        int sectorIndex = 0, offset = 0, bytes = 0;
        void Jump()
        {
            if (payloadSize - offset < 5 || sectorIndex + 1 >= macroSectorIds.Count) throw new MacroLibraryException("Capacity");
            ushort next = macroSectorIds[sectorIndex + 1];
            var payload = sectors[(int)macroSectorIds[sectorIndex]];
            payload[offset] = 0x60; payload[offset + 1] = (byte)(next >> 8); payload[offset + 2] = (byte)next;
            payload[offset + 3] = 0x00; payload[offset + 4] = 0x00;
            sectorIndex++; offset = 0;
        }
        foreach (var e in EventBytes(macro))
        {
            int remaining = payloadSize - offset;
            if (e.Length > remaining || remaining - e.Length < 5) Jump();
            e.CopyTo(sectors[(int)macroSectorIds[sectorIndex]], offset);
            offset += e.Length; bytes += e.Length;
        }
        if (payloadSize - offset < 2) Jump(); // terminator plus alignment padding
        sectors[(int)macroSectorIds[sectorIndex]][offset] = 0xFF;
        bytes++;
        offset++;
        if (offset % 2 == 1) { sectors[(int)macroSectorIds[sectorIndex]][offset] = 0xFF; bytes++; }
        foreach (var pair in sectors.Take(sectorIndex + 1)) Wire.UpdateCrc(pair.Value);
        // Only the sectors this macro touches are returned; untouched macro sectors
        // keep whatever the device already holds and are never part of a write set.
        int lastUsed = (int)macroSectorIds[sectorIndex];
        return new(sectors.Where(p => p.Key <= lastUsed).ToDictionary(p => p.Key, p => p.Value), macroSectorIds[0], bytes);
    }
}

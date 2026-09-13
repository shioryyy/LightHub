using System.Buffers.Binary;

namespace LightHub.Core;

// Read-only profile-name support (memory model 1, layout A): each profile sector
// carries up to 24 UTF-16LE code units at 0xA0, terminated by 0x0000/0xFFFF; an
// all-0xFF area means the device never stored a name. Writing names needs a
// validated driver and stays closed; this only reports what is already there.
public static class ProfileName
{
    public const int Offset = 0xA0;
    public const int Units = 24;

    public static string? Read(DeviceSnapshot snapshot, int sector)
    {
        if (!snapshot.Sectors.TryGetValue(sector, out var data)) return null;
        if (data.Length < Offset + Units * 2 + 2) return null;
        var area = data[Offset..(Offset + Units * 2)];
        if (area.All(b => b == 0xFF)) return null;
        var units = new List<char>();
        for (int offset = 0; offset + 1 < area.Length; offset += 2)
        {
            ushort unit = BinaryPrimitives.ReadUInt16LittleEndian(area.AsSpan(offset, 2));
            if (unit is 0 or 0xFFFF) break;
            if (unit is >= 0xD800 and <= 0xDFFF or < 0x20 or 0x7F) return null;
            units.Add((char)unit);
        }
        return units.Count == 0 ? null : new string(units.ToArray());
    }
}

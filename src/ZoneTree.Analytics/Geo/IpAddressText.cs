namespace ZoneTree.Analytics.Geo;

/// <summary>
/// Allocation-free parsing of the usual textual forms of IP addresses: dotted decimal IPv4
/// without leading zeros, and IPv6 with optional "::" and a trailing dotted IPv4 part. Other
/// forms, such as zone ids or octal parts, are rejected; <see cref="System.Net.IPAddress"/>
/// parses those.
/// </summary>
static class IpAddressText
{
  public static bool TryParseIpv4(ReadOnlySpan<char> text, out uint address)
  {
    address = 0;
    uint part = 0;
    int parts = 0, digits = 0;
    foreach (var c in text)
    {
      if (c == '.')
      {
        if (digits == 0 || parts == 3) return false;
        address = address << 8 | part;
        parts++;
        part = 0;
        digits = 0;
        continue;
      }
      var digit = (uint)(c - '0');
      if (digit > 9 || (digits > 0 && part == 0)) return false;
      part = part * 10 + digit;
      if (++digits > 3 || part > 255) return false;
    }
    if (digits == 0 || parts != 3) return false;
    address = address << 8 | part;
    return true;
  }

  public static bool TryParseIpv6(ReadOnlySpan<char> text, out UInt128 address)
  {
    address = UInt128.Zero;
    Span<ushort> groups = stackalloc ushort[8];
    int count = 0, gap = -1, i = 0;
    if (text.Length < 2) return false;
    if (text[0] == ':')
    {
      if (text[1] != ':') return false;
      gap = 0;
      i = 2;
      if (i == text.Length) return true;
    }
    while (true)
    {
      var start = i;
      uint group = 0;
      // A table lookup: branching on the kind of character mispredicts on random hex digits.
      for (; i < text.Length; i++)
      {
        var c = text[i];
        var digit = c < HexValues.Length ? HexValues[c] : Invalid;
        if (digit == Invalid) break;
        group = group << 4 | digit;
      }
      if (i < text.Length && text[i] == '.')
      {
        if (count > 6 || !TryParseIpv4(text[start..], out var ipv4)) return false;
        groups[count++] = (ushort)(ipv4 >> 16);
        groups[count++] = (ushort)ipv4;
        break;
      }
      if (i == start || i - start > 4 || count == 8) return false;
      groups[count++] = (ushort)group;
      if (i == text.Length) break;
      if (text[i] != ':' || ++i == text.Length) return false;
      if (text[i] == ':')
      {
        if (gap >= 0) return false;
        gap = count;
        if (++i == text.Length) break;
      }
    }
    if (gap < 0 ? count != 8 : count > 7) return false;
    if (gap >= 0)
    {
      // The groups after "::" move to the end; the gap becomes zeros.
      var zeros = 8 - count;
      for (var g = count - 1; g >= gap; g--) groups[g + zeros] = groups[g];
      groups.Slice(gap, zeros).Clear();
    }
    var upper = (ulong)groups[0] << 48 | (ulong)groups[1] << 32 | (ulong)groups[2] << 16 | groups[3];
    var lower = (ulong)groups[4] << 48 | (ulong)groups[5] << 32 | (ulong)groups[6] << 16 | groups[7];
    address = new UInt128(upper, lower);
    return true;
  }

  const byte Invalid = 0xFF;

  static readonly byte[] HexValues = CreateHexValues();

  static byte[] CreateHexValues()
  {
    var values = new byte[128];
    Array.Fill(values, Invalid);
    for (var c = '0'; c <= '9'; c++) values[c] = (byte)(c - '0');
    for (var c = 'a'; c <= 'f'; c++) values[c] = values[c - 32] = (byte)(c - 'a' + 10);
    return values;
  }
}

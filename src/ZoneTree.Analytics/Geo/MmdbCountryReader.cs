using System.Buffers.Binary;
using System.Text;

namespace ZoneTree.Analytics.Geo;

/// <summary>
/// The address ranges of a MaxMind DB (MMDB) file and their countries. Ranges are sorted, start at
/// address zero and cover the whole address space; adjacent ranges of the same country are merged.
/// Country 0 is unknown.
/// </summary>
sealed class MmdbCountries
{
  public string DatabaseType;
  public DateTimeOffset BuildTime;
  public List<string> Countries = [null];
  public List<uint> Ipv4Starts = [];
  public List<ushort> Ipv4Countries = [];
  // Null for an IPv4 database.
  public List<UInt128> Ipv6Starts;
  public List<ushort> Ipv6Countries;
}

/// <summary>
/// Reads the countries of a MaxMind DB file: its search tree is flattened into address ranges.
/// A record's country is "country_code" (ip-location-db), or the "iso_code" of "country" or else
/// "registered_country" (MaxMind, DB-IP).
/// </summary>
/// <remarks>
/// IPv4 addresses are read from the ::/96 subtree of an IPv6 database. Other IPv6 paths into that
/// subtree, such as aliases for IPv4-mapped, 6to4 or Teredo addresses, are left unknown.
/// </remarks>
static class MmdbCountryReader
{
  static ReadOnlySpan<byte> MetadataMarker =>
      [0xAB, 0xCD, 0xEF, (byte)'M', (byte)'a', (byte)'x', (byte)'M', (byte)'i', (byte)'n', (byte)'d', (byte)'.', (byte)'c', (byte)'o', (byte)'m'];

  const int MaxMetadataSize = 128 * 1024;

  const int MaxDepth = 32;

  // Bounds the tree walk of a malformed file whose nodes share children.
  const long MaxVisits = 1L << 27;

  enum Type { Pointer = 1, String = 2, Bytes = 4, UInt16 = 5, UInt32 = 6, Map = 7, Int32 = 8, UInt64 = 9, UInt128 = 10, Array = 11, Boolean = 14 }

  public static MmdbCountries Read(byte[] file)
  {
    try
    {
      return new Flattener(file).Read();
    }
    catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
    {
      throw new InvalidDataException("The file is not a valid MaxMind DB (MMDB) file.", exception);
    }
  }

  static InvalidDataException Invalid(string message) => new($"The file is not a valid MaxMind DB (MMDB) file: {message}");

  sealed class Flattener(byte[] file)
  {
    readonly Dictionary<uint, ushort> countryOfRecord = [];
    readonly Dictionary<string, ushort> countryIds = new(StringComparer.Ordinal);
    MmdbCountries result;
    int dataStart;
    int dataEnd;
    uint nodeCount;
    int recordSize;
    uint ipv4Root;
    long visits;

    public MmdbCountries Read()
    {
      var tail = Math.Min(file.Length, MaxMetadataSize);
      var marker = file.AsSpan(file.Length - tail).LastIndexOf(MetadataMarker);
      if (marker < 0) throw Invalid("the metadata marker is missing.");
      dataEnd = file.Length - tail + marker;
      result = new();
      var ipVersion = ReadMetadata(file.AsSpan(dataEnd + MetadataMarker.Length));
      var treeSize = (long)nodeCount * recordSize / 4;
      dataStart = checked((int)treeSize + 16);
      if (dataStart > dataEnd) throw Invalid("the search tree is larger than the file.");

      ipv4Root = 0;
      if (ipVersion == 6)
        for (var depth = 0; depth < 96 && ipv4Root < nodeCount; depth++)
          ipv4Root = Record(ipv4Root, 0);
      var starts = new List<UInt128>();
      var countries = new List<ushort>();
      Walk(ipv4Root, 0, 32, UInt128.Zero, false, starts, countries);
      foreach (var start in starts) result.Ipv4Starts.Add((uint)start);
      result.Ipv4Countries = countries;
      if (ipVersion == 6)
      {
        result.Ipv6Starts = [];
        result.Ipv6Countries = [];
        Walk(0, 0, 128, UInt128.Zero, true, result.Ipv6Starts, result.Ipv6Countries);
      }
      return result;
    }

    int ReadMetadata(ReadOnlySpan<byte> metadata)
    {
      var position = 0;
      var map = Locate(metadata, ref position, 0);
      var pairs = Header(metadata, ref map, Type.Map);
      int major = 0, ipVersion = 0;
      for (var i = 0; i < pairs; i++)
      {
        var key = ReadString(metadata, ref map);
        var value = Locate(metadata, ref map, 0);
        switch (Encoding.UTF8.GetString(key))
        {
          case "binary_format_major_version": major = (int)ReadUInt(metadata, value); break;
          case "node_count": nodeCount = (uint)ReadUInt(metadata, value); break;
          case "record_size": recordSize = (int)ReadUInt(metadata, value); break;
          case "ip_version": ipVersion = (int)ReadUInt(metadata, value); break;
          case "database_type": result.DatabaseType = Encoding.UTF8.GetString(ReadString(metadata, ref value)); break;
          case "build_epoch": result.BuildTime = DateTimeOffset.FromUnixTimeSeconds((long)ReadUInt(metadata, value)); break;
        }
      }
      if (major != 2) throw Invalid($"binary format version {major} is not supported.");
      if (recordSize is not (24 or 28 or 32)) throw Invalid($"record size {recordSize} is not supported.");
      if (ipVersion is not (4 or 6)) throw Invalid($"IP version {ipVersion} is not supported.");
      return ipVersion;
    }

    uint Record(uint node, int bit)
    {
      var tree = file.AsSpan();
      switch (recordSize)
      {
        case 24:
          var at = checked((int)(node * 6L + bit * 3));
          return (uint)(tree[at] << 16 | tree[at + 1] << 8 | tree[at + 2]);
        case 28:
          at = checked((int)(node * 7L));
          return bit == 0
              ? (uint)((tree[at + 3] & 0xF0) << 20 | tree[at] << 16 | tree[at + 1] << 8 | tree[at + 2])
              : (uint)((tree[at + 3] & 0x0F) << 24 | tree[at + 4] << 16 | tree[at + 5] << 8 | tree[at + 6]);
        default:
          return BinaryPrimitives.ReadUInt32BigEndian(tree[checked((int)(node * 8L + bit * 4))..]);
      }
    }

    // Emits the ranges under a record in address order. Within IPv6, the IPv4 subtree is unknown.
    void Walk(uint record, int depth, int bits, UInt128 prefix, bool ipv6, List<UInt128> starts, List<ushort> countries)
    {
      if (++visits > MaxVisits) throw Invalid("the search tree has too many paths.");
      if (record < nodeCount && !(ipv6 && record == ipv4Root))
      {
        if (depth == bits) throw Invalid("the search tree is deeper than the address length.");
        Walk(Record(record, 0), depth + 1, bits, prefix << 1, ipv6, starts, countries);
        Walk(Record(record, 1), depth + 1, bits, prefix << 1 | UInt128.One, ipv6, starts, countries);
        return;
      }
      var country = record > nodeCount ? Country(record) : (ushort)0;
      if (countries.Count > 0 && countries[^1] == country) return;
      starts.Add(depth == 0 ? UInt128.Zero : prefix << (bits - depth));
      countries.Add(country);
    }

    ushort Country(uint record)
    {
      if (countryOfRecord.TryGetValue(record, out var id)) return id;
      var offset = (long)record - nodeCount - 16;
      if (offset < 0 || offset >= dataEnd - dataStart) throw Invalid("a record points outside the data section.");
      var code = ReadCountry(file.AsSpan(dataStart, dataEnd - dataStart), (int)offset);
      id = 0;
      if (!string.IsNullOrEmpty(code) && !countryIds.TryGetValue(code, out id))
      {
        if (result.Countries.Count > ushort.MaxValue) throw Invalid("there are too many countries.");
        id = (ushort)result.Countries.Count;
        result.Countries.Add(code);
        countryIds.Add(code, id);
      }
      countryOfRecord.Add(record, id);
      return id;
    }

    static string ReadCountry(ReadOnlySpan<byte> data, int offset)
    {
      var position = offset;
      var map = Locate(data, ref position, 0);
      if (Peek(data, map) != Type.Map) return null;
      var pairs = Header(data, ref map, Type.Map);
      string code = null, country = null, registered = null;
      for (var i = 0; i < pairs; i++)
      {
        var key = ReadString(data, ref map);
        var value = Locate(data, ref map, 0);
        if (key.SequenceEqual("country_code"u8) && Peek(data, value) == Type.String)
          code = Encoding.UTF8.GetString(ReadString(data, ref value));
        else if (key.SequenceEqual("country"u8))
          country = IsoCode(data, value);
        else if (key.SequenceEqual("registered_country"u8))
          registered = IsoCode(data, value);
      }
      return country ?? code ?? registered;
    }

    static string IsoCode(ReadOnlySpan<byte> data, int map)
    {
      if (Peek(data, map) != Type.Map) return null;
      var pairs = Header(data, ref map, Type.Map);
      for (var i = 0; i < pairs; i++)
      {
        var key = ReadString(data, ref map);
        var value = Locate(data, ref map, 0);
        if (key.SequenceEqual("iso_code"u8) && Peek(data, value) == Type.String)
          return Encoding.UTF8.GetString(ReadString(data, ref value));
      }
      return null;
    }
  }

  // The position of the value at position, following a pointer, and moves position past it.
  static int Locate(ReadOnlySpan<byte> section, ref int position, int depth)
  {
    if (section[position] >> 5 != (int)Type.Pointer)
    {
      var start = position;
      Skip(section, ref position, depth);
      return start;
    }
    var control = section[position++];
    var value = control & 7;
    switch ((control >> 3) & 3)
    {
      case 0: value = value << 8 | section[position]; position += 1; break;
      case 1: value = (value << 16 | section[position] << 8 | section[position + 1]) + 2048; position += 2; break;
      case 2: value = (value << 24 | section[position] << 16 | section[position + 1] << 8 | section[position + 2]) + 526336; position += 3; break;
      default: value = checked((int)BinaryPrimitives.ReadUInt32BigEndian(section[position..])); position += 4; break;
    }
    if (section[value] >> 5 == (int)Type.Pointer) throw Invalid("a pointer points to a pointer.");
    return value;
  }

  static void Skip(ReadOnlySpan<byte> section, ref int position, int depth)
  {
    if (depth > MaxDepth) throw Invalid("values are nested too deeply.");
    if (section[position] >> 5 == (int)Type.Pointer)
    {
      Locate(section, ref position, depth);
      return;
    }
    var type = Peek(section, position);
    var size = Header(section, ref position, type);
    switch (type)
    {
      case Type.Map: for (var i = 0; i < size * 2; i++) Skip(section, ref position, depth + 1); break;
      case Type.Array: for (var i = 0; i < size; i++) Skip(section, ref position, depth + 1); break;
      case Type.Boolean: break;
      default: position += size; break;
    }
  }

  static Type Peek(ReadOnlySpan<byte> section, int position)
  {
    var type = section[position] >> 5;
    return (Type)(type == 0 ? 7 + section[position + 1] : type);
  }

  // Reads the control bytes of a value of the expected type and returns its size.
  static int Header(ReadOnlySpan<byte> section, ref int position, Type expected)
  {
    var control = section[position++];
    var type = control >> 5;
    if (type == 0) type = 7 + section[position++];
    if ((Type)type != expected) throw Invalid($"expected a value of type {expected}, found type {type}.");
    var size = control & 31;
    switch (size)
    {
      case 29: size = 29 + section[position]; position += 1; break;
      case 30: size = 285 + (section[position] << 8 | section[position + 1]); position += 2; break;
      case 31: size = 65821 + (section[position] << 16 | section[position + 1] << 8 | section[position + 2]); position += 3; break;
    }
    return size;
  }

  static ReadOnlySpan<byte> ReadString(ReadOnlySpan<byte> section, ref int position)
  {
    var at = Locate(section, ref position, 0);
    var size = Header(section, ref at, Type.String);
    return section.Slice(at, size);
  }

  static ulong ReadUInt(ReadOnlySpan<byte> section, int position)
  {
    var type = Peek(section, position);
    if (type is not (Type.UInt16 or Type.UInt32 or Type.UInt64 or Type.UInt128 or Type.Int32))
      throw Invalid($"expected an integer, found type {(int)type}.");
    var size = Header(section, ref position, type);
    if (size > 8) throw Invalid("an integer is too large.");
    ulong value = 0;
    foreach (var b in section.Slice(position, size)) value = value << 8 | b;
    return value;
  }
}

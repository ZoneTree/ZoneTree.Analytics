using System.Buffers.Binary;
using System.Text;

namespace ZoneTree.Analytics.Internal;

/// <summary>
/// A small growable binary writer used by the storage codecs.
/// It is not thread-safe. Reuse an instance per thread with <see cref="Reset"/>.
/// </summary>
sealed class ByteWriter
{
  byte[] buffer;

  int length;

  public ByteWriter(int capacity = 256)
  {
    buffer = new byte[capacity];
  }

  public void Reset() => length = 0;

  public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

  void Ensure(int additional)
  {
    var required = length + additional;
    if (required <= buffer.Length)
      return;
    var newSize = Math.Max(required, buffer.Length * 2);
    Array.Resize(ref buffer, newSize);
  }

  public void WriteByte(byte value)
  {
    Ensure(1);
    buffer[length++] = value;
  }

  public void WriteBytes(ReadOnlySpan<byte> bytes)
  {
    Ensure(bytes.Length);
    bytes.CopyTo(buffer.AsSpan(length));
    length += bytes.Length;
  }

  public void WriteUInt64BigEndian(ulong value)
  {
    Ensure(8);
    BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(length), value);
    length += 8;
  }

  public void WriteDouble(double value)
  {
    Ensure(8);
    BinaryPrimitives.WriteInt64LittleEndian(
        buffer.AsSpan(length), BitConverter.DoubleToInt64Bits(value));
    length += 8;
  }

  public void WriteVarUInt64(ulong value)
  {
    Ensure(10);
    while (value >= 0x80)
    {
      buffer[length++] = (byte)(value | 0x80);
      value >>= 7;
    }
    buffer[length++] = (byte)value;
  }

  public void WriteVarInt64(long value)
  {
    // zig-zag encoding keeps small negative numbers small.
    WriteVarUInt64((ulong)((value << 1) ^ (value >> 63)));
  }

  /// <summary>
  /// Writes a length-prefixed UTF-8 string. Null is encoded distinctly from empty.
  /// </summary>
  public void WriteString(string value)
  {
    if (value == null)
    {
      WriteVarUInt64(0);
      return;
    }
    var byteCount = Encoding.UTF8.GetByteCount(value);
    WriteVarUInt64((ulong)byteCount + 1);
    Ensure(byteCount);
    Encoding.UTF8.GetBytes(value, buffer.AsSpan(length));
    length += byteCount;
  }

  /// <summary>
  /// Writes raw UTF-8 bytes of the string without a length prefix.
  /// </summary>
  public void WriteRawUtf8(string value)
  {
    if (string.IsNullOrEmpty(value))
      return;
    var byteCount = Encoding.UTF8.GetByteCount(value);
    Ensure(byteCount);
    Encoding.UTF8.GetBytes(value, buffer.AsSpan(length));
    length += byteCount;
  }
}

/// <summary>
/// The reading counterpart of <see cref="ByteWriter"/>.
/// </summary>
ref struct ByteReader
{
  readonly ReadOnlySpan<byte> span;

  int position;

  public ByteReader(ReadOnlySpan<byte> span)
  {
    this.span = span;
    position = 0;
  }

  public byte ReadByte() => span[position++];

  public ReadOnlySpan<byte> ReadBytes(int count)
  {
    var result = span.Slice(position, count);
    position += count;
    return result;
  }

  public double ReadDouble()
  {
    var bits = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(position, 8));
    position += 8;
    return BitConverter.Int64BitsToDouble(bits);
  }

  public ulong ReadVarUInt64()
  {
    ulong result = 0;
    var shift = 0;
    while (true)
    {
      var b = span[position++];
      result |= (ulong)(b & 0x7F) << shift;
      if ((b & 0x80) == 0)
        return result;
      shift += 7;
      if (shift > 63)
        throw new FormatException("Invalid variable-length integer.");
    }
  }

  public long ReadVarInt64()
  {
    var raw = ReadVarUInt64();
    return (long)(raw >> 1) ^ -(long)(raw & 1);
  }

  public string ReadString()
  {
    var lengthPlusOne = ReadVarUInt64();
    if (lengthPlusOne == 0)
      return null;
    var byteCount = (int)(lengthPlusOne - 1);
    if (byteCount == 0)
      return string.Empty;
    var result = Encoding.UTF8.GetString(span.Slice(position, byteCount));
    position += byteCount;
    return result;
  }

  /// <summary>
  /// Reads a string through the cache, which saves allocating repeated values.
  /// </summary>
  public string ReadString(Utf8StringCache cache)
  {
    var lengthPlusOne = ReadVarUInt64();
    if (lengthPlusOne == 0)
      return null;
    var byteCount = (int)(lengthPlusOne - 1);
    var result = cache.Get(span.Slice(position, byteCount));
    position += byteCount;
    return result;
  }

  /// <summary>
  /// Reads the UTF-8 bytes of the next length-prefixed string without decoding it.
  /// Null and empty strings both return an empty span.
  /// </summary>
  public ReadOnlySpan<byte> ReadStringBytes()
  {
    var lengthPlusOne = ReadVarUInt64();
    if (lengthPlusOne <= 1)
      return default;
    var byteCount = (int)(lengthPlusOne - 1);
    var result = span.Slice(position, byteCount);
    position += byteCount;
    return result;
  }

  public void SkipString()
  {
    var lengthPlusOne = ReadVarUInt64();
    if (lengthPlusOne > 1)
      position += (int)(lengthPlusOne - 1);
  }

  /// <summary>
  /// Compares the next length-prefixed string with the given UTF-8 bytes
  /// and advances past it.
  /// </summary>
  public bool ReadStringEquals(ReadOnlySpan<byte> utf8)
  {
    var lengthPlusOne = ReadVarUInt64();
    if (lengthPlusOne == 0)
      return false;
    var byteCount = (int)(lengthPlusOne - 1);
    var result = span.Slice(position, byteCount).SequenceEqual(utf8);
    position += byteCount;
    return result;
  }
}

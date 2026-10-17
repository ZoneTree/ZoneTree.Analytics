using System.Buffers.Binary;
using System.Text;
using ZoneTree.Analytics.Internal;

namespace ZoneTree.Analytics.Storage;

/// <summary>A decoded raw event key.</summary>
/// <param name="Site">The stored site name.</param>
/// <param name="Ticks">The event's UTC timestamp ticks.</param>
/// <param name="Sequence">The sequence that distinguishes events at the same timestamp.</param>
public readonly record struct RawEventKey(string Site, long Ticks, long Sequence)
{
  /// <summary>The event's timestamp as a UTC instant.</summary>
  public DateTimeOffset Timestamp => new(Ticks, TimeSpan.Zero);
}

/// <summary>A decoded rollup key with named dimension values.</summary>
/// <remarks>
/// Granularity is determined by the storage tier, not encoded in the key. A missing dimension
/// is represented by null, while an explicitly empty value is an empty string.
/// </remarks>
public sealed class RollupKey
{
  /// <summary>The stored site name.</summary>
  public string Site { get; init; }

  /// <summary>The stored event name, including engine-generated events such as sessions.</summary>
  public string Event { get; init; }

  /// <summary>
  /// The stored bucket ticks: UTC for minute and hour tiers, local midnight in the engine's
  /// reporting time zone for the day tier. Use <see cref="GetBucketStart"/> to interpret them.
  /// </summary>
  public long BucketTicks { get; init; }

  /// <summary>Dimension names and their values. Empty for a totals key.</summary>
  public Dictionary<string, string> Dimensions { get; } = new(StringComparer.Ordinal);

  /// <summary>The canonical dimension-set identifier, with names in ordinal order.</summary>
  public string DimensionSetId => string.Join(",", Dimensions.Keys.Order(StringComparer.Ordinal));

  /// <summary>Interprets the bucket using its storage tier and the engine's reporting time zone.</summary>
  public DateTimeOffset GetBucketStart(Granularity granularity, TimeZoneInfo timeZone)
  {
    ArgumentNullException.ThrowIfNull(timeZone);
    var interval = granularity switch
    {
      Granularity.Minute => TimeInterval.Minute,
      Granularity.Hour => TimeInterval.Hour,
      Granularity.Day => TimeInterval.Day,
      _ => throw new ArgumentOutOfRangeException(nameof(granularity))
    };
    return new ReportingCalendar(timeZone, DayOfWeek.Monday).BucketLabel(BucketTicks, interval);
  }
}

/// <summary>Converts encoded analytics storage keys to usable structures and back.</summary>
/// <remarks>
/// These codecs preserve the current storage format. Encoding uses stored names and values,
/// without ingestion normalization, and rejects control characters used by the key layout.
/// Session keys are already usable <see cref="ulong"/> values when read through their ZoneTree.
/// </remarks>
public static class StorageKeyCodec
{
  /// <summary>Encodes a raw event key in the engine's storage format.</summary>
  public static Memory<byte> EncodeRawEvent(RawEventKey key)
  {
    ValidateName(key.Site, nameof(key));
    ArgumentOutOfRangeException.ThrowIfNegative(key.Ticks, nameof(key));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Ticks, DateTime.MaxValue.Ticks, nameof(key));
    ArgumentOutOfRangeException.ThrowIfNegative(key.Sequence, nameof(key));
    return KeyCodec.RawKey(key.Site, key.Ticks, key.Sequence);
  }

  /// <summary>Decodes the site, timestamp and sequence of a raw event key.</summary>
  /// <exception cref="FormatException">The bytes are not a complete, valid raw event key.</exception>
  public static RawEventKey DecodeRawEvent(ReadOnlySpan<byte> bytes)
  {
    var offset = 0;
    var site = ReadSegment(bytes, ref offset);
    if (bytes.Length - offset != 16)
      throw new FormatException("A raw event key must end with a timestamp and sequence.");
    var ticks = ReadTicks(bytes[offset..]);
    var sequence = BinaryPrimitives.ReadInt64BigEndian(bytes[(offset + 8)..]);
    if (sequence < 0)
      throw new FormatException("A raw event sequence cannot be negative.");
    return new RawEventKey(site, ticks, sequence);
  }

  /// <summary>Encodes a rollup key, ordering dimension names canonically and preserving nulls.</summary>
  public static Memory<byte> EncodeRollup(RollupKey key)
  {
    ArgumentNullException.ThrowIfNull(key);
    ValidateName(key.Site, nameof(key));
    ValidateName(key.Event, nameof(key));
    ArgumentOutOfRangeException.ThrowIfNegative(key.BucketTicks, nameof(key));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(key.BucketTicks, DateTime.MaxValue.Ticks, nameof(key));
    var names = key.Dimensions.Keys.Order(StringComparer.Ordinal).ToArray();
    foreach (var name in names)
    {
      ValidateName(name, nameof(key));
      if (name.Contains(',', StringComparison.Ordinal))
        throw new ArgumentException("Dimension names cannot contain commas.", nameof(key));
      if (key.Dimensions[name] is { } value)
      {
        if (value.Any(char.IsControl))
          throw new ArgumentException("Dimension values cannot contain control characters.", nameof(key));
        Text.ValidateUnicode(value, nameof(key));
      }
    }
    var combined = string.Join(KeyCodec.ValueSeparator, names.Select(name => KeyCodec.Stored(key.Dimensions[name])));
    return KeyCodec.RollupKey(key.Site, key.Event, string.Join(",", names), key.BucketTicks, combined);
  }

  /// <summary>Decodes the site, event, bucket and named dimension values of a rollup key.</summary>
  /// <exception cref="FormatException">The bytes are not a complete, valid rollup key.</exception>
  public static RollupKey DecodeRollup(ReadOnlySpan<byte> bytes)
  {
    var offset = 0;
    var site = ReadSegment(bytes, ref offset);
    var eventName = ReadSegment(bytes, ref offset);
    var set = ReadSegment(bytes, ref offset, allowEmpty: true);
    if (bytes.Length - offset < 8)
      throw new FormatException("A rollup key must contain a bucket.");
    var key = new RollupKey { Site = site, Event = eventName, BucketTicks = ReadTicks(bytes[offset..]) };
    var combined = ReadUtf8(bytes[(offset + 8)..]);
    var names = set.Length == 0 ? [] : set.Split(',');
    var values = KeyCodec.SplitValues(combined, names.Length);
    if ((names.Length == 0 && combined.Length != 0) || names.Length != values.Length)
      throw new FormatException("The rollup dimension names and values do not match.");
    for (var i = 0; i < names.Length; ++i)
    {
      if (string.IsNullOrWhiteSpace(names[i]) || (i > 0 && StringComparer.Ordinal.Compare(names[i - 1], names[i]) >= 0))
        throw new FormatException("Rollup dimension names must be distinct and in ordinal order.");
      var value = KeyCodec.Public(values[i]);
      if (value != null && value.Any(char.IsControl))
        throw new FormatException("A rollup dimension value contains a reserved control character.");
      key.Dimensions.Add(names[i], value);
    }
    return key;
  }

  static void ValidateName(string name, string parameter)
  {
    Text.ValidateStoredName(name, parameter);
  }

  static string ReadSegment(ReadOnlySpan<byte> bytes, ref int offset, bool allowEmpty = false)
  {
    var length = bytes[offset..].IndexOf(KeyCodec.Terminator);
    if (length < 0)
      throw new FormatException("A storage key segment is missing its terminator.");
    var name = ReadUtf8(bytes.Slice(offset, length));
    if ((!allowEmpty && string.IsNullOrWhiteSpace(name)) || name.Any(char.IsControl))
      throw new FormatException("A storage key contains an invalid name.");
    offset += length + 1;
    return name;
  }

  static string ReadUtf8(ReadOnlySpan<byte> bytes)
  {
    try
    {
      return Text.StrictUtf8.GetString(bytes);
    }
    catch (DecoderFallbackException exception)
    {
      throw new FormatException("A storage key contains invalid UTF-8.", exception);
    }
  }

  static long ReadTicks(ReadOnlySpan<byte> bytes)
  {
    var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
    if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
      throw new FormatException("A storage key timestamp is outside the supported date range.");
    return ticks;
  }
}

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.Privacy;

/// <summary>
/// Derives 64-bit visitor hashes. Raw identifiers, IP addresses and user agents
/// are never stored; only HMAC-SHA256 based hashes are.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Explicit visitor ids: HMAC(secret, site | id). Stable over time.</item>
/// <item>Anonymous visitors: HMAC(salt of the UTC day, site | ip | user agent).
/// Salts are random, kept for the current and previous day, then destroyed, so hashes cannot
/// be recomputed or linked across days. The salt of an older day, created for events with
/// historical timestamps, is kept until a day after its last use, so a backfill hashes each
/// visitor of that day the same way across salt rotations and restarts.</item>
/// </list>
/// </remarks>
sealed class VisitorHasher
{
  const string SecretKey = "visitor-secret";

  const string SaltPrefix = "salt:";

  /// <summary>The UTC ticks of the last use of an older day's salt, per day.</summary>
  const string SaltUsePrefix = "salt-used:";

  /// <summary>How often the last use of an older day's salt is persisted.</summary>
  const long UseRefreshTicks = TimeSpan.TicksPerHour;

  readonly VisitorIdentification mode;

  readonly byte[] secret;

  readonly MetaStore meta;

  readonly ConcurrentDictionary<long, byte[]> salts = new();

  /// <summary>The last persisted use of each older day's salt in use.</summary>
  readonly ConcurrentDictionary<long, long> uses = new();

  readonly object saltLock = new();

  public VisitorHasher(VisitorIdentification mode, byte[] configuredSecret, MetaStore meta)
  {
    this.mode = mode;
    this.meta = meta;
    secret = configuredSecret ?? LoadOrCreateSecret(meta);
  }

  static byte[] LoadOrCreateSecret(MetaStore meta)
  {
    var stored = meta.Get(SecretKey);
    if (stored != null)
      return Convert.FromBase64String(stored);
    var created = RandomNumberGenerator.GetBytes(32);
    meta.Set(SecretKey, Convert.ToBase64String(created));
    return created;
  }

  /// <summary>
  /// Hash of an explicit visitor id. Zero when visitors are not identified.
  /// </summary>
  public ulong HashVisitorId(string site, string visitorId)
  {
    if (mode == VisitorIdentification.None || string.IsNullOrEmpty(visitorId))
      return 0;
    return Hash(secret, "id", site, visitorId, null);
  }

  /// <summary>
  /// Computes the visitor hash of an event. Zero means "no visitor".
  /// </summary>
  public ulong Compute(string site, string visitorId, string ipAddress, string userAgent, long utcTicks)
  {
    if (mode == VisitorIdentification.None)
      return 0;
    if (!string.IsNullOrEmpty(visitorId))
      return Hash(secret, "id", site, visitorId, null);
    if (mode == VisitorIdentification.ExplicitOnly)
      return 0;
    if (string.IsNullOrEmpty(ipAddress) && string.IsNullOrEmpty(userAgent))
      return 0;
    var key = mode == VisitorIdentification.PersistentHash
        ? secret
        : GetSalt(utcTicks - utcTicks % TimeSpan.TicksPerDay);
    return Hash(key, "anon", site, ipAddress ?? "", userAgent ?? "");
  }

  static string DayName(long utcDay) => new DateTime(utcDay).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

  static long ParseDay(string name) => DateTime.ParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture).Ticks;

  /// <summary>The first day whose salt is kept by date: the previous UTC day.</summary>
  static long KeptFrom(long utcNowTicks) => utcNowTicks - utcNowTicks % TimeSpan.TicksPerDay - TimeSpan.TicksPerDay;

  byte[] GetSalt(long utcDay)
  {
    var now = DateTime.UtcNow.Ticks;
    var older = utcDay < KeptFrom(now);
    if (salts.TryGetValue(utcDay, out var salt) &&
        (!older || (uses.TryGetValue(utcDay, out var used) && now - used < UseRefreshTicks)))
      return salt;
    lock (saltLock)
    {
      var name = SaltPrefix + DayName(utcDay);
      var created = false;
      if (!salts.TryGetValue(utcDay, out salt))
      {
        var stored = meta.Get(name);
        created = stored == null;
        salt = created ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(stored);
      }
      var recordUse = older && (!uses.TryGetValue(utcDay, out var lastUse) || now - lastUse >= UseRefreshTicks);
      if (created || recordUse)
      {
        meta.Update(entries =>
        {
          if (created)
            entries[name] = Convert.ToBase64String(salt);
          if (recordUse)
            entries[SaltUsePrefix + DayName(utcDay)] = now.ToString(CultureInfo.InvariantCulture);
        });
      }
      if (recordUse)
        uses[utcDay] = now;
      salts[utcDay] = salt;
      return salt;
    }
  }

  /// <summary>
  /// Destroys the salts older than the previous UTC day that were not used during the last day.
  /// </summary>
  public void RotateSalts(DateTime utcNow)
  {
    var keepFrom = KeptFrom(utcNow.Ticks);
    var idleFrom = utcNow.Ticks - TimeSpan.TicksPerDay;
    lock (saltLock)
    {
      var lastUses = meta.ListByPrefix(SaltUsePrefix).ToDictionary(
          x => ParseDay(x.Key[SaltUsePrefix.Length..]), x => long.Parse(x.Value, CultureInfo.InvariantCulture));
      var days = meta.ListByPrefix(SaltPrefix).Select(x => ParseDay(x.Key[SaltPrefix.Length..]))
          .Concat(salts.Keys).Concat(lastUses.Keys).ToHashSet();
      var expired = days.Where(day => day < keepFrom &&
          Math.Max(lastUses.GetValueOrDefault(day, long.MinValue), uses.GetValueOrDefault(day, long.MinValue)) < idleFrom).ToList();
      if (expired.Count == 0)
        return;
      meta.Update(entries =>
      {
        foreach (var day in expired)
        {
          entries.Remove(SaltPrefix + DayName(day));
          entries.Remove(SaltUsePrefix + DayName(day));
        }
      });
      foreach (var day in expired)
      {
        salts.TryRemove(day, out _);
        uses.TryRemove(day, out _);
      }
    }
  }

  // Reused per thread: a stack buffer of up to a few hundred bytes would be zeroed on every call.
  [ThreadStatic]
  static byte[] threadBuffer;

  static ulong Hash(byte[] key, string kind, string site, string a, string b)
  {
    var maxBytes = Encoding.UTF8.GetMaxByteCount(
        kind.Length + site.Length + a.Length + (b?.Length ?? 0)) + 3;
    var buffer = threadBuffer;
    if (buffer == null || buffer.Length < maxBytes)
      threadBuffer = buffer = new byte[Math.Max(maxBytes, 1024)];
    var length = Encoding.UTF8.GetBytes(kind, buffer);
    buffer[length++] = 0;
    length += Encoding.UTF8.GetBytes(site, buffer.AsSpan(length));
    buffer[length++] = 0;
    length += Encoding.UTF8.GetBytes(a, buffer.AsSpan(length));
    if (b != null)
    {
      buffer[length++] = 0;
      length += Encoding.UTF8.GetBytes(b, buffer.AsSpan(length));
    }
    Span<byte> mac = stackalloc byte[32];
    HMACSHA256.HashData(key, buffer.AsSpan(0, length), mac);
    var hash = BinaryPrimitives.ReadUInt64LittleEndian(mac);
    return hash == 0 ? 1 : hash;
  }
}

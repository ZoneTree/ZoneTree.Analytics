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
/// Salts are random, kept only for the current and previous day, then destroyed,
/// so hashes cannot be recomputed or linked across days.</item>
/// </list>
/// </remarks>
sealed class VisitorHasher
{
  const string SecretKey = "visitor-secret";

  const string SaltPrefix = "salt:";

  readonly VisitorIdentification mode;

  readonly byte[] secret;

  readonly MetaStore meta;

  readonly ConcurrentDictionary<long, byte[]> salts = new();

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

  byte[] GetSalt(long utcDay)
  {
    if (salts.TryGetValue(utcDay, out var salt))
      return salt;
    lock (saltLock)
    {
      if (salts.TryGetValue(utcDay, out salt))
        return salt;
      var name = SaltPrefix + new DateTime(utcDay).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
      var stored = meta.Get(name);
      if (stored != null)
      {
        salt = Convert.FromBase64String(stored);
      }
      else
      {
        salt = RandomNumberGenerator.GetBytes(32);
        meta.Set(name, Convert.ToBase64String(salt));
      }
      salts[utcDay] = salt;
      return salt;
    }
  }

  /// <summary>
  /// Destroys salts older than the previous UTC day.
  /// </summary>
  public void RotateSalts(DateTime utcNow)
  {
    var keepFrom = utcNow.Date.AddDays(-1).Ticks;
    lock (saltLock)
    {
      foreach (var day in salts.Keys)
      {
        if (day < keepFrom)
          salts.TryRemove(day, out _);
      }
      var expired = meta.ListByPrefix(SaltPrefix)
          .Select(x => x.Key)
          .Where(name => DateTime.ParseExact(name[SaltPrefix.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture).Ticks < keepFrom)
          .ToList();
      if (expired.Count > 0)
      {
        meta.Update(entries =>
        {
          foreach (var name in expired)
            entries.Remove(name);
        });
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

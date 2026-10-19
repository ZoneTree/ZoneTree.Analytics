using System.Globalization;

sealed record AuditEntry(string Id, DateTime Time, string Actor, string Action, string Detail, string IpAddress);

// Who changed what, newest first. Entries are kept under audit/{utc ticks}-{sequence}.
sealed class AuditLog(SystemStore store)
{
  const string Prefix = "audit/";
  long sequence;

  sealed record Record(DateTime Time, string Actor, string Action, string Detail, string IpAddress);

  public void Write(string actor, string action, string detail, HttpContext context = null)
  {
    var now = DateTime.UtcNow;
    var key = Prefix + now.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-"
        + Interlocked.Increment(ref sequence).ToString("D10", CultureInfo.InvariantCulture);
    store.Put(key, new Record(now, actor, action, detail, context?.Connection.RemoteIpAddress?.ToString()));
  }

  // Up to take entries older than the entry named before, newest first.
  public List<AuditEntry> List(string before, int take) =>
      [.. store.ListDescending<Record>(Prefix, before == null ? null : Prefix + before, Math.Clamp(take, 1, 500))
          .Select(entry => new AuditEntry(entry.Key[Prefix.Length..], entry.Value.Time, entry.Value.Actor,
              entry.Value.Action, entry.Value.Detail, entry.Value.IpAddress))];
}

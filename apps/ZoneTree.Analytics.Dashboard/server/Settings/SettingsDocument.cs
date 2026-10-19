using System.Text.Json;

sealed record SettingChange(string Key, string Label, SettingTier Tier, JsonElement Before, JsonElement After);

// Reads settings by schema key and compares two settings documents field by field.
static class SettingsDocument
{
  public static object Read(DashboardSettings settings, string key)
  {
    object current = settings;
    foreach (var segment in key.Split('.'))
    {
      if (current == null) return null;
      var property = current.GetType().GetProperties()
          .FirstOrDefault(property => SettingsSchema.Camel(property.Name) == segment)
          ?? throw new ArgumentException($"Unknown setting '{key}'.");
      current = property.GetValue(current);
    }
    return current;
  }

  public static DashboardSettings Clone(DashboardSettings settings) =>
      JsonSerializer.Deserialize<DashboardSettings>(JsonSerializer.Serialize(settings, SystemStore.Json), SystemStore.Json);

  public static List<SettingChange> Diff(DashboardSettings before, DashboardSettings after)
  {
    var changes = new List<SettingChange>();
    foreach (var section in SettingsSchema.Document.Sections)
      foreach (var field in section.Fields)
        Compare(field, before, after, changes);
    return changes;
  }

  static void Compare(SettingField field, DashboardSettings before, DashboardSettings after, List<SettingChange> changes)
  {
    if (field.Fields != null)
    {
      foreach (var child in field.Fields) Compare(child, before, after, changes);
      return;
    }
    var previous = JsonSerializer.SerializeToElement(Read(before, field.Key), SystemStore.Json);
    var next = JsonSerializer.SerializeToElement(Read(after, field.Key), SystemStore.Json);
    if (previous.GetRawText() != next.GetRawText())
      changes.Add(new(field.Key, field.Label, field.Tier, previous, next));
  }
}

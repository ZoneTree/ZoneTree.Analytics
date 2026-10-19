using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;

// When a changed setting takes effect: at once, after the engine reopens, after the host
// restarts, or never (chosen during setup and stored with the data).
enum SettingTier { Live, Reopen, Restart, Fixed }

[AttributeUsage(AttributeTargets.Property)]
sealed class SectionAttribute(string label) : Attribute
{
  public string Label { get; } = label;
  public string Help { get; init; }
}

[AttributeUsage(AttributeTargets.Property)]
sealed class SettingAttribute(string label) : Attribute
{
  public string Label { get; } = label;
  public string Help { get; init; }
  public string Unit { get; init; }
  public double Min { get; init; } = double.NaN;
  public double Max { get; init; } = double.NaN;
  public SettingTier Tier { get; init; } = SettingTier.Reopen;
  public bool Advanced { get; init; }
  // The choice that stores null, for example "Keep forever".
  public string EmptyLabel { get; init; }
  // Fixed choices: "value:label|value:label".
  public string Options { get; init; }
  // A dedicated editor instead of the field type's default.
  public string Editor { get; init; }
  public string KeyLabel { get; init; }
  public string ValueLabel { get; init; }
}

sealed record SettingOption(object Value, string Label);

sealed record SettingField(string Key, string Label, string Help, string Type, string Unit, double? Min, double? Max,
    bool Nullable, string EmptyLabel, IReadOnlyList<SettingOption> Options, SettingTier Tier, bool Advanced,
    string KeyLabel, string ValueLabel, IReadOnlyList<SettingField> Fields);

sealed record SettingSection(string Key, string Label, string Help, IReadOnlyList<SettingField> Fields);

sealed record SettingsPreset(string Group, string Key, string Label, string Description, IReadOnlyDictionary<string, object> Values);

sealed record SettingsSchemaDocument(IReadOnlyList<SettingSection> Sections, IReadOnlyList<SettingsPreset> Presets);

// Describes DashboardSettings from its [Section] and [Setting] metadata. Keys are the camel-case
// JSON paths of the settings document, for example "privacy.geoPrecision".
static class SettingsSchema
{
  static readonly Lazy<SettingsSchemaDocument> document = new(Build);
  static readonly ConcurrentDictionary<string, SettingField> byKey = new(StringComparer.Ordinal);

  public static SettingsSchemaDocument Document => document.Value;

  public static SettingField Find(string key)
  {
    _ = document.Value;
    return byKey.TryGetValue(key, out var field) ? field : null;
  }

  static SettingsSchemaDocument Build()
  {
    var sections = new List<SettingSection>();
    foreach (var property in typeof(DashboardSettings).GetProperties())
    {
      var section = property.GetCustomAttribute<SectionAttribute>();
      if (section == null) continue;
      var key = Camel(property.Name);
      sections.Add(new(key, section.Label, section.Help, Fields(property.PropertyType, key)));
    }
    return new(sections, SettingsPresets.All);
  }

  static List<SettingField> Fields(Type type, string prefix)
  {
    var fields = new List<SettingField>();
    foreach (var property in type.GetProperties())
    {
      var setting = property.GetCustomAttribute<SettingAttribute>();
      if (setting == null) continue;
      var key = prefix + "." + Camel(property.Name);
      var propertyType = property.PropertyType;
      var underlying = System.Nullable.GetUnderlyingType(propertyType);
      var valueType = underlying ?? propertyType;
      var nullable = underlying != null || (setting.EmptyLabel != null && valueType == typeof(string));
      IReadOnlyList<SettingField> children = null;
      IReadOnlyList<SettingOption> options = null;
      string fieldType;
      if (setting.Editor != null) fieldType = setting.Editor;
      else if (valueType == typeof(bool)) fieldType = "toggle";
      else if (valueType.IsEnum) fieldType = "select";
      else if (valueType == typeof(int) || valueType == typeof(long) || valueType == typeof(double)) fieldType = "number";
      else if (valueType == typeof(string)) fieldType = "text";
      else
      {
        fieldType = "group";
        children = Fields(valueType, key);
      }
      if (setting.Options != null)
      {
        fieldType = "select";
        options = [.. setting.Options.Split('|').Select(option =>
        {
          var separator = option.IndexOf(':');
          var value = option[..separator];
          return new SettingOption(int.TryParse(value, out var number) ? number : value, option[(separator + 1)..]);
        })];
      }
      else if (valueType.IsEnum)
        options = EnumOptions(valueType);
      else if (propertyType.IsGenericType && propertyType.GetGenericArguments()[0] is { IsEnum: true } itemType)
        options = EnumOptions(itemType);
      var field = new SettingField(key, setting.Label, setting.Help, fieldType, setting.Unit,
          double.IsNaN(setting.Min) ? null : setting.Min, double.IsNaN(setting.Max) ? null : setting.Max,
          nullable, setting.EmptyLabel, options, setting.Tier, setting.Advanced, setting.KeyLabel, setting.ValueLabel, children);
      byKey[key] = field;
      fields.Add(field);
    }
    return fields;
  }

  static List<SettingOption> EnumOptions(Type type) =>
      [.. Enum.GetNames(type).Select(name => new SettingOption(name, Humanize(name)))];

  public static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

  // "DailyRotatingHash" → "Daily rotating hash".
  static string Humanize(string name)
  {
    var builder = new StringBuilder(name.Length + 8);
    for (var i = 0; i < name.Length; i++)
    {
      var c = name[i];
      if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) builder.Append(' ').Append(char.ToLowerInvariant(c));
      else builder.Append(c);
    }
    return builder.ToString();
  }
}

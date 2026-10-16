using System.Globalization;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// The versions of every persisted layout, in one place.
/// </summary>
/// <remarks>
/// A version increases with every incompatible change of its layout. <see cref="Require(string, int, string)"/>
/// refuses data written in any other version. Within a version, readers reject header bits,
/// flags and trailing bytes they do not know.
/// </remarks>
static class StorageFormats
{
  /// <summary>The data directory: raw event, rollup and session payloads, keys and metadata.</summary>
  public const int Dataset = 2;

  /// <summary>The metadata key of <see cref="Dataset"/>.</summary>
  public const string DatasetKey = "format-version";

  /// <summary>The shared covering dimension-index store: its key layout and metadata.</summary>
  public const int DimensionIndex = 2;

  /// <summary>The metadata key of <see cref="DimensionIndex"/>.</summary>
  public const string DimensionIndexKey = "dimension-index.format";

  /// <summary>The covering rollup-index stores: their key layout and metadata.</summary>
  public const int RollupIndex = 2;

  /// <summary>The metadata key of <see cref="RollupIndex"/>.</summary>
  public const string RollupIndexKey = "rollup-index.format";

  /// <summary>
  /// The body of the historical joint-rollup backfill journal, whose redo rows hold serialized
  /// rollup values.
  /// </summary>
  public const int JointBackfillJournal = 2;

  /// <summary>The text a format is persisted as in metadata.</summary>
  public static string Text(int version) => version.ToString(CultureInfo.InvariantCulture);

  /// <summary>
  /// Refuses a stored format other than the current one. <paramref name="subject"/> names the
  /// data, such as "The analytics data directory".
  /// </summary>
  /// <exception cref="AnalyticsConfigurationException">The stored format is not the current one.</exception>
  public static void Require(string stored, int current, string subject)
  {
    if (!int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
      throw new AnalyticsConfigurationException(
          $"{subject} has an unknown storage format '{stored}'; this release reads format {current}.");
    Require(version, current, subject);
  }

  /// <inheritdoc cref="Require(string, int, string)"/>
  public static void Require(int stored, int current, string subject)
  {
    if (stored > current)
      throw new AnalyticsConfigurationException(
          $"{subject} uses storage format {stored}, written by a newer release; this release reads format {current}.");
    if (stored < current)
      throw new AnalyticsConfigurationException(
          $"{subject} uses storage format {stored}, written by an older release; this release reads format {current}. " +
          "Start with a fresh analytics data directory.");
  }
}

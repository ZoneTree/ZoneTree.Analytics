using ZoneTree.Analytics;

// The IP country database of the primary engine, loaded once at startup. `yarn get-ip-location-db`
// downloads it to the default path. Without it, countries come only from event dimensions.
sealed class IpLocation(IGeoLocator locator)
{
  public const string DefaultPath = "data/ip-location/user-country.mmdb";

  public static readonly IpLocation None = new(null);

  public IGeoLocator Locator { get; } = locator;

  public static IpLocation Load(string path, ILogger logger)
  {
    if (!File.Exists(path))
    {
      logger.LogWarning("No IP location database at {Path}: visitor countries are not resolved. " +
          "Download it with `yarn get-ip-location-db`, or set Dashboard:IpLocationDatabase.", path);
      return None;
    }
    try
    {
      var database = IpCountryDatabase.Load(path);
      logger.LogInformation("IP location database {Path} loaded: {Type}, built {BuildTime:yyyy-MM-dd}.",
          path, database.DatabaseType, database.BuildTime);
      return new(database);
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
    {
      logger.LogError(exception, "The IP location database {Path} could not be loaded: visitor countries are not resolved.", path);
      return None;
    }
  }
}

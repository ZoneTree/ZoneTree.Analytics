namespace ZoneTree.Analytics.Geo;

static class IpLocationFiles
{
  /// <summary>
  /// Reads a whole file. Other processes may replace or delete it meanwhile: on Windows a file
  /// opened without delete sharing cannot be replaced by a move.
  /// </summary>
  public static byte[] Read(string path)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    var length = stream.Length;
    if (length > Array.MaxLength)
      throw new InvalidDataException($"{path} is too large for a database file.");
    var file = new byte[length];
    stream.ReadExactly(file);
    return file;
  }
}

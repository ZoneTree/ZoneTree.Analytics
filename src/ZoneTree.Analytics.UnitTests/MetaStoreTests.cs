using ZoneTree.Analytics.Storage;

namespace ZoneTree.Analytics.UnitTests;

public sealed class MetaStoreTests : DirectoryTest
{
  string[] Files => [Path.Combine(directory, "meta-a.bin"), Path.Combine(directory, "meta-b.bin")];

  // The generation follows the 8-byte magic.
  static long GenerationOf(string path) => BitConverter.ToInt64(File.ReadAllBytes(path), 8);

  string NewestFile => Files.Where(File.Exists).OrderByDescending(GenerationOf).First();

  [Test]
  public void EntriesSurviveReopening()
  {
    var meta = new MetaStore(directory);
    meta.Set("salt:2026-10-08", "a");
    meta.Set("salt:2026-10-09", "b");
    meta.Update(entries =>
    {
      entries["format-version"] = "1";
      entries.Remove("salt:2026-10-08");
    });

    var reopened = new MetaStore(directory);
    Assert.Multiple(() =>
    {
      Assert.That(reopened.Get("format-version"), Is.EqualTo("1"));
      Assert.That(reopened.Get("salt:2026-10-08"), Is.Null);
      Assert.That(reopened.ListByPrefix("salt:").Select(x => x.Key), Is.EqualTo(new[] { "salt:2026-10-09" }));
    });
  }

  [Test]
  public void ATornWriteFallsBackToThePreviousGeneration()
  {
    var meta = new MetaStore(directory);
    meta.Set("raw-sequence", "1");
    meta.Set("raw-sequence", "2");
    // A crash while writing the newest file leaves it truncated.
    var newest = NewestFile;
    var bytes = File.ReadAllBytes(newest);
    File.WriteAllBytes(newest, bytes[..(bytes.Length / 2)]);

    Assert.That(new MetaStore(directory).Get("raw-sequence"), Is.EqualTo("1"));
  }

  [Test]
  public void AChangedFileIsDetected()
  {
    var meta = new MetaStore(directory);
    meta.Set("time-zone", "UTC");
    meta.Set("raw-sequence", "1");
    var newest = NewestFile;
    var bytes = File.ReadAllBytes(newest);
    var at = bytes.AsSpan().IndexOf("UTC"u8);
    "CET"u8.CopyTo(bytes.AsSpan(at));
    File.WriteAllBytes(newest, bytes);

    // The checksum rejects the edited file; the previous generation still has the setting.
    var reopened = new MetaStore(directory);
    Assert.Multiple(() =>
    {
      Assert.That(reopened.Get("time-zone"), Is.EqualTo("UTC"));
      Assert.That(reopened.Get("raw-sequence"), Is.Null);
    });
  }

  [Test]
  public void DamagedMetadataIsNeverReplacedSilently()
  {
    var meta = new MetaStore(directory);
    meta.Set("visitor-secret", "secret");
    meta.Set("raw-sequence", "1");
    foreach (var file in Files)
      File.WriteAllText(file, "garbage");

    Assert.Throws<AnalyticsConfigurationException>(() => _ = new MetaStore(directory));
  }

  [Test]
  public void RemovedEntriesLeaveBothFiles()
  {
    var meta = new MetaStore(directory);
    meta.Set("salt:2026-10-01", "destroy-me");
    meta.Set("raw-sequence", "1");
    meta.Delete("salt:2026-10-01");

    Assert.That(Files.Select(File.ReadAllText), Has.None.Contains("destroy-me"));
  }

  [Test]
  public void UnchangedValuesAreNotWritten()
  {
    var meta = new MetaStore(directory);
    meta.Set("time-zone", "UTC");
    var generation = GenerationOf(NewestFile);
    meta.Set("time-zone", "UTC");
    meta.Update(_ => { });

    Assert.That(GenerationOf(NewestFile), Is.EqualTo(generation));
  }

  [Test]
  public void ADataDirectoryHasOneEngineAtATime()
  {
    var engine = TestEngine.Create(directory);
    var exception = Assert.Throws<IOException>(() => TestEngine.Create(directory).Dispose());
    engine.Dispose();

    Assert.Multiple(() =>
    {
      Assert.That(exception.Message, Does.Contain("in use by another engine"));
      Assert.DoesNotThrow(() => TestEngine.Create(directory).Dispose());
    });
  }
}

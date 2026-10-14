var hourly = analytics.Storage.GetRollups(Granularity.Hour);
if (hourly != null)
{
    foreach (var name in hourly.GetPartitions())
    {
        using var partition = await hourly.AcquireAsync(name);
        if (partition == null) continue;

        using var iterator = partition.ZoneTree.CreateIterator();
        while (iterator.Next())
        {
            var key = StorageKeyCodec.DecodeRollup(iterator.CurrentKey.Span);
            var bucketStart = key.GetBucketStart(Granularity.Hour, analytics.TimeZone);
            Console.WriteLine($"{bucketStart:u}: {key.Site} / {key.Event}");
        }
    }
}


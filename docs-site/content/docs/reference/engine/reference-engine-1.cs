using ZoneTree.Analytics.Storage;

var raw = analytics.Storage.RawEvents; // null when raw event storage is disabled
if (raw != null)
{
    foreach (var name in raw.GetPartitions())
    {
        using var partition = await raw.AcquireAsync(name);
        if (partition == null) continue; // retention removed it after the list was read

        var tree = partition.ZoneTree; // IZoneTree<Memory<byte>, Memory<byte>>
        var maintainer = partition.Maintainer; // the engine's existing IMaintainer
        using var iterator = tree.CreateIterator();
        while (iterator.Next())
        {
            var stored = RawEventCodec.Decode(iterator.CurrentKey.Span, iterator.CurrentValue.Span);
            if (stored == null) continue; // deletion marker
            Console.WriteLine($"{stored.Site}: {stored.Timestamp:u} {stored.Name}");
            if (stored.TryGetDimension("page", out var page)) Console.WriteLine(page);
        }
    }
}

var hourly = analytics.Storage.GetRollups(Granularity.Hour); // null when disabled
// hourly.Acquire(name).ZoneTree is IZoneTree<Memory<byte>, RollupValue>.
var sessions = analytics.Storage.Sessions; // IZoneTree<ulong, SessionState>, null when disabled
var sessionsMaintainer = analytics.Storage.SessionsMaintainer;


# Open questions

## Size-based partitions

Each tier (raw events, minute, hour and day rollups) is a sequence of ZoneTrees. Each tree holds a contiguous time range. Housekeeping starts a new partition when the latest one holds `Storage.Partitioning.<Tier>` records (10M by default). The new partition starts at the day boundary at or after `now + MaxFutureSkew + 1 day`.

1. **Backfills ignore the limit.** Events route by their own time, so a historic import goes into the partition that covers that time. Importing a year of data therefore fills one partition, whatever its size. Options:
   - Accept this and document it (current behavior).
   - Also roll over by event time during imports, starting a new partition at a day boundary after the newest imported record. This needs care, because older partitions must never receive records beyond their range.

2. **Counts are approximate.** The rollover check uses `TotalRecordCount`, which includes deleted records that merges have not removed yet. Expired records leave disk daily, so the error is small. A tier with heavy erasures (`EraseVisitorAsync`) could roll over early.

3. **Only an open latest partition is checked.** A closed latest partition receives no records, so it needs no rollover. After a restart, the check starts once the first write reopens the partition.

4. **Overshoot.** The latest partition keeps receiving events until the new partition's start, so partitions exceed the limit by up to about two days of traffic. Is a tighter boundary worth tracking the maximum event time per partition?

5. **Rollup merge cost grows with the tree.** Rollup keys are ordered by series, then time (`site · event · dimset · bucket · values`), so each new hour's rows spread over the whole key range. Multipart disk segments bound what a merge rewrites to the parts that receive keys, and with many series that is most of them. The 10M limit caps the cost. Is a time-major rollup key layout worth evaluating?

6. **Rollup mutable segment default (100k).** Measured with one hourly rollup tree:

   | Events | 20k | 100k | 200k |
   | --- | ---: | ---: | ---: |
   | 3M | 61.0 µs | 56.5 µs | 54.1 µs |
   | 1M | 52.0 µs | 50.2 µs | |

   Values are CPU per event, compaction included. The 1M runs are the warm ones; the cold first run is left out. 100k used about 300 MB more working set than 20k at 1M events. Rows carry HyperLogLog sketches, so a dense sketch can be ~16 KB. Is 100k the right default?

## Retention

7. **Replace the ForceDelete trick with `IsRangeDeleted`.** Today each retention pass deletes the first expired key of every key range (per site for raw events, per series for rollups), so the next merge reads and drops the expired parts. A ZoneTree `IsRangeDeleted` delegate would let merges drop expired ranges directly, without marker records or the extra pass.

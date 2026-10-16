
# Storage format

ZoneTree.Analytics stores a dataset as several ordered ZoneTree stores plus small, checksummed metadata files. This reference describes the format implemented by the current source: dataset `format-version` **2**, dimension-index format **2**, and rollup-index format **2**.

Use this page to inspect a dataset, understand storage costs, or review a codec change. For application code, prefer the public [storage accessors and codecs](../docs-site/content/docs/reference/engine/engine.mdx#advanced-storage-access). Those APIs return typed records without requiring callers to reproduce byte offsets.

## Format boundaries and compatibility

There are two layers to the persisted format:

| Layer | Defined by | Includes |
| --- | --- | --- |
| Analytics records | ZoneTree.Analytics | Directory routing, keys, event payloads, rollup and session serializers, metadata, index namespaces, and recovery checkpoints |
| Storage engine files | ZoneTree | Tree metadata, segments, write-ahead logs, compression, block layout, and merge machinery |

The byte layouts below describe the serialized keys and values passed to ZoneTree. They are not offsets into segment or WAL files. Segment mode and compression can be configured through `StoreOptions`; opening trees through the matching ZoneTree version handles that layer. The current core project references ZoneTree **2.0.1**.

On dataset creation, the engine persists these settings and requires exact matches on every reopen:

| Metadata key | Stored value | Why it must match |
| --- | --- | --- |
| `format-version` | `"2"` | Selects the dataset format understood by the engine; it increases with every incompatible layout change |
| `time-zone` | `AnalyticsOptions.TimeZone.Id` | Daily rollup keys encode local calendar dates |
| `hll-precision` | Decimal `Rollups.HyperLogLogPrecision` | Sketches merged together must use compatible precision |
| `wal-directory` | Absolute configured WAL root, or `""` | Unflushed records can live outside the data directory |

A mismatch raises `AnalyticsConfigurationException`. Index formats have their own versions, persisted as `dimension-index.format` and `rollup-index.format`, and the historical joint-rollup journal carries one in its body. They follow the same rule as `format-version`: each increases with an incompatible change of its layout, and a stored version other than the current one causes startup to fail with the same older, newer, or unknown explanation.

`RollupValueSerializer` and `SessionStateSerializer` type names are recorded in ZoneTree metadata and must remain stable. Changes to field order, primitive encodings, positional session dimensions, hashing, and key comparison also need compatibility review.

`format-version` is an increasing number: every incompatible change to a persisted layout increases it. A release refuses a directory written in another format, and the error says whether an older or a newer release wrote it. Within a format, readers reject what they do not recognize instead of skipping it: unknown rollup header bits, unknown raw-event flags, unknown session headers, and trailing bytes raise `FormatException`. An older reader therefore never silently drops a part that a newer layout added.

## Dataset directory

Paths in this table are relative to `AnalyticsOptions.DataDirectory`. A partition directory contains one ZoneTree. Optional stores appear according to the engine configuration and workload; a tier can exist before it has any partitions.

| Path | Contents |
| --- | --- |
| `engine.lock` | Exclusive handle held for the engine's lifetime |
| `meta/meta-a.json` and `meta/meta-b.json` | Alternating generations of dataset metadata |
| `events/yyyy-MM-dd/` | Raw event partitions, when `StoreRawEvents` is enabled |
| `rollups/minute/yyyy-MM-dd/` | Minute aggregate partitions, when enabled |
| `rollups/hour/yyyy-MM-dd/` | Hour aggregate partitions, when enabled |
| `rollups/day/yyyy-MM-dd/` | Day aggregate partitions, when enabled |
| `sessions/` | One unpartitioned tree of active sessions, when sessions are enabled |
| `indexes/yyyy-MM-dd/` | Shared covering dimension-index partitions |
| `rollup-indexes/minute/yyyy-MM-dd/` | Shared covering rollup-index partitions for the minute tier |
| `rollup-indexes/hour/yyyy-MM-dd/` | Shared covering rollup-index partitions for the hour tier |
| `rollup-indexes/day/yyyy-MM-dd/` | Shared covering rollup-index partitions for the day tier |
| `high-water` in each partitioned store's root | The latest time written to each partition of that store, one `name ticks` line per partition |
| `backfill/joint-rollups/journal-a.bin` and `journal-b.bin` | Alternating checkpoints of historical joint-rollup reconstruction, present while one runs |

Sites share these trees. A site name is part of each event and rollup key; it is not a subdirectory. Logical dimension indexes share `indexes/`, and logical rollup indexes share one physical store per enabled tier.

When `Storage.WriteAheadLogDirectory` is set, every tree's WAL directory uses the same relative path under that separate root. For example, data in `events/2026-10-15/` uses logs under `events/2026-10-15/` beneath the WAL root. Metadata files remain under the data root.

The existence of `engine.lock` does not indicate a stale owner. Ownership is the open exclusive handle, released when the engine closes or its process exits.

## Partition names and time routing

Partitions are sized by approximate record count. They are not daily tables, even though their names use invariant `yyyy-MM-dd` dates.

For ordered starts S0, S1, and S2, routing works as follows:

| Partition | Routed time range |
| --- | --- |
| S0 | Every timestamp before S1, including timestamps before S0 |
| S1 | S1 inclusive to S2 exclusive |
| S2 | S2 inclusive and every later timestamp |

The first record creates a partition named for its day. The map is reconstructed from date-named subdirectories at startup. There is no partition-map file; the store's `high-water` file only records the latest time written to each partition, its high-water mark.

When the partition receiving new records reaches the target, rollover creates a new partition at the latest of these day boundaries:

- The first boundary at or after the housekeeping clock plus `Ingestion.MaxFutureSkew` plus one day. With the default one-hour skew, a new partition is usually named two days ahead.
- One day after the receiving partition's start.
- The day after the receiving partition's high-water mark.

Every record already written therefore keeps its routing, including records written while a clock ran ahead of the housekeeping clock. Normally the receiving partition is the latest one. When a clock that ran ahead started an empty partition in the future, records still go to an earlier partition. Rollover then inserts the new partition before the future one, provided the earlier partition's high-water mark is known and the boundary fits before the future start.

High-water marks are saved at housekeeping, about once a minute, and at shutdown. A crash loses only marks raised since the last save; the boundary's day margin covers them unless a clock ran ahead during that minute. The target is not a hard capacity limit: checks and day-boundary routing can let a partition exceed it. Approximate counts can include deleted records that merges have not removed yet.

Each tier routes on the timestamp it stores: raw events and dimension indexes use event UTC ticks; minute and hour rollups use UTC bucket ticks; day rollups use local calendar-day ticks. Index stores roll over independently from their source stores, so a copied record need not have the same partition name as its source.

The core defaults target **10,000,000 records per partition** for raw events, each rollup tier, and each shared index store. Index targets count all copied rows across logical namespaces. Changing a target affects future rollover; it does not repartition existing records. Explicit historical timestamps route back into the partition covering their time.

Queries visit overlapping partitions using half-open ranges. Partitions open on demand and close after the idle timeout, which defaults to ten minutes. A storage lease pins a partition against idle closure and defers its physical deletion, while record expiration continues to apply.

## Binary primitives

Layouts below list fields in serialization order. Repeated groups occur exactly as many times as their preceding count specifies. There is no implicit alignment or padding in the Analytics payloads.

| Notation | Encoding |
| --- | --- |
| `u8` | One byte |
| `u64be` | Eight-byte unsigned integer, most significant byte first |
| `u64le` | Eight-byte unsigned integer, least significant byte first |
| `f64le` | IEEE 754 binary64 bits in eight little-endian bytes |
| `vu` | Unsigned base-128 variable-length integer: low seven bits first, high bit set when another byte follows; at most ten bytes for a 64-bit value |
| `vi` | Signed 64-bit integer mapped with zig-zag, then encoded as `vu` |
| `str` | `vu(UTF-8 byte length + 1)` followed by UTF-8 bytes; `vu(0)` means null, `vu(1)` means empty |
| `segment` | UTF-8 bytes followed by `00`; no length prefix |
| `bytes` | Exact byte sequence, with length determined by the surrounding layout |

Zig-zag maps 0 to 0, -1 to 1, 1 to 2, and -2 to 3. For example, unsigned 300 encodes as `AC 02`, while signed -1 encodes as `01`. `str("A")` is `02 41`; a segment containing `A` is `41 00`. String lengths count UTF-8 bytes, not .NET characters.

Time values use .NET ticks: 100 nanoseconds since `0001-01-01`. Event timestamps and minute/hour buckets are nonnegative UTC ticks. A daily bucket uses the ticks of the reporting zone's local midnight date, without a UTC offset encoded in the key. Interpret it with the persisted reporting time zone, including daylight-saving transitions; it is not a UTC midnight instant.

Binary-key trees use `ByteArrayLexicographicComparerAscending`. Bytes compare lexicographically, and a key sorts before any longer key sharing it as a prefix. Big-endian time fields preserve chronological ordering within a fixed preceding prefix. UTF-8 segments sort by their encoded bytes, without culture-aware collation.

### Names, missing values, and empty values

Ingestion sanitizes names by removing control characters, trimming, and truncating to 128 .NET characters without splitting a surrogate pair. Dimension values replace control characters with spaces, trim, and use the configured length limit. Both replace each unpaired surrogate with U+FFFD, the character its UTF-8 encoding stores, so hashes and in-memory keys agree with the stored bytes. Ingestion ignores dimensions with a null value or a sanitized name that is empty or contains a comma. Public codecs operate on stored names and values and do not repeat ingestion normalization.

Keys reserve `00` as the segment terminator. Rollup dimension tuples additionally reserve `1F` as their value separator and `01` as the missing-dimension sentinel. Public rollup encoders reject control characters in real dimension values and reject commas in dimension names, keeping those representations unambiguous. The public raw encoder and decoder also reject control characters in dimension values, so a raw event can always be aggregated into a rollup key.

The three empty or absent cases differ:

| Context | Missing | Explicitly empty |
| --- | --- | --- |
| Length-prefixed string | `00` | `01` |
| One dimension value in a rollup key | Raw byte `01` | Zero bytes for that value part |
| Raw event dimension list | Dimension can be absent; the binary codec also supports a null string | Present pair with an empty string |

## Raw events

The primary raw store uses `Memory<byte>` keys and values. It contains enriched event data; it does not persist the original `AnalyticsEvent` object or its raw IP address and user-agent string as dedicated fields.

### Raw key

| Order | Field | Encoding |
| --- | --- | --- |
| 1 | Site | `segment` |
| 2 | Event timestamp | `u64be`, UTC ticks |
| 3 | Sequence | `u64be`, nonnegative sequence |

For a site whose UTF-8 length is N, the key is N + 17 bytes. Events sort by site, then timestamp, then sequence. Event name is in the value, so scanning one site's time range can include several event types.

The engine obtains sequences with an atomic increment. At startup its counter begins at the larger of current UTC ticks and the persisted `raw-sequence`; the counter is saved every minute and at shutdown. Sequence is a tie-breaker for equal timestamps, not an event count or the event timestamp itself.

### Raw value

| Order | Field | Encoding |
| --- | --- | --- |
| 1 | Event name | `str` |
| 2 | Visitor hash | `u64le` |
| 3 | Session ID | `u64le`; zero means no session |
| 4 | Session start offset | `vi(event ticks - session start ticks)`; zero when there is no session |
| 5 | Flags | `u8`; bit `01` means interactive; every reader rejects other bits |
| 6 | Dimension count | `vu` |
| 7 | Each dimension | Name `str`, then value `str` |
| 8 | Measured value count | `vu` |
| 9 | Each measured value | Name `str`, then number `f64le` |

Site, event timestamp, and sequence are supplied by the key and do not repeat in the payload. Recover the session start by subtracting the signed offset from the event timestamp. With session ID zero, the decoded session start has no semantic meaning; use `RawEvent.SessionStart`, which returns null in that case.

Dimension and measured-value pairs preserve encoded order. There is no requirement that raw pairs have the canonical order used by rollup keys. Visitor is the engine's stored hash, not the original visitor identifier. Derived query-time dimensions such as local hour and weekday are calculated when needed.

An empty raw value is a tombstone. A live event value has no version or presence byte; its first bytes are the event name's length-prefixed string. `RawEventCodec.Encode(null)` returns an empty value.

## Rollups

Each enabled granularity has its own partitioned `Memory<byte>` / `RollupValue` store. Granularity is determined by that store's path, not by a field in the key or value.

### Rollup key

| Order | Field | Encoding |
| --- | --- | --- |
| 1 | Site | `segment` |
| 2 | Event name | `segment` |
| 3 | Dimension-set identifier | `segment`; empty for totals |
| 4 | Bucket | `u64be` |
| 5 | Combined dimension values | Raw UTF-8, without a length prefix or final terminator |

Dimension names are distinct and ordered with `StringComparer.Ordinal`. Their comma-joined names form the dimension-set identifier. Values follow that same order, separated by byte `1F`. A missing dimension contributes sentinel `01`; an explicitly empty dimension contributes an empty part. Totals use an empty identifier and no combined-value bytes.

For dimensions supplied as `source=GitHub` and `country=DE`, the identifier is `country,source` and the trailing tuple is `DE`, byte `1F`, then `GitHub`. For `country=null` and `source=""`, the tuple is exactly `01 1F`. A one-dimension empty value has no trailing value bytes; its nonempty dimension-set identifier distinguishes it from totals.

Keys order by site, event, dimension set, bucket, then combined values. All values in a series and time range therefore form one contiguous range in each overlapping partition. To scan, seek to the three-segment series prefix plus the inclusive starting bucket; stop when the prefix changes or the bucket reaches the exclusive end.

### Rollup value header

`RollupValueSerializer` begins with one byte:

| Value or bit | Meaning |
| --- | --- |
| `00` | Deleted row; the only byte |
| `01` | Present bit, set for every live row |
| `02` | Visitor sketch section |
| `04` | Value detail section |
| `08` | Distinct-dimension sketch section |
| `10` | Rebuilt part |

Live flags combine with bitwise OR. Readers reject other bits, a live header without the present bit, and trailing bytes. A live row containing no sketches, measured values, or optional details still has a header, event count, and zero measured-value count. Count zero does not make a row a tombstone: session and page metrics can contribute sum-only updates without adding another event.

### Rollup value body

| Order | Field | Encoding and condition |
| --- | --- | --- |
| 1 | Header | `u8` |
| 2 | Event/contribution count | `vi` |
| 3 | Measured value count | `vu` |
| 4 | Each scalar measured value | Scalar layout below |
| 5 | Visitor section | `vu` byte length, then the HLL layout below; only with header bit `02` |
| 6 | Detail section | `vu` byte length, then `vu` detail count equal to the measured value count, then per measured value a `vu` entry length and its detail record; length `0` means the value has no details; only with header bit `04` |
| 7 | Distinct section | `vu` byte length, then `vu` dimension count, then per dimension its name `str`, a nonzero `vu` entry length, and its HLL sketch; only with header bit `08` |
| 8 | Rebuilt part | `vu` byte length, then a live value in this same layout, without a rebuilt part of its own; only with header bit `10` |

Sections are nonempty and appear in this order, after every scalar field. The reader decodes the count and the scalar values with the row, checks each section's length and the detail count, and keeps the sections as stored bytes. When the value arrives as a slice of a larger buffer, such as a decompressed disk block, the sections are first copied into a buffer of their own, so a cached or merged row never keeps the block alive.

Decoding happens one part at a time, the first time a query or a merge reads it: the visitor section, then a single value detail or a single distinct sketch. Reading a section's entry lengths and dimension names does not decode any sketch. A query that reads only counts and sums therefore never decodes a sketch, and a query for one distinct dimension or one value's percentiles decodes only that entry. A merge or index copy writes every section and entry it leaves unchanged back from its stored bytes, without re-encoding it.

A rebuilt part remains in a row while historical joint-rollup reconstruction runs or its output awaits folding, including across restarts. It holds the contributions the reconstruction has made to the row so far, apart from what ingestion merges into the row meanwhile, and readers ignore it. Ingestion keeps it unchanged, and the reconstruction replaces it with absolute values, so each can repeat its writes after a failure without undoing the other's. A row holding only a rebuilt part has no ordinary data and is skipped by queries. After reconstruction completes, the rollup index build merges the rebuilt parts into their rows and removes the rebuilt sections. A build also folds pending rows of retained definitions whose indexes were removed.

Measured-value names and distinct-dimension names are stored in ordinal order by the engine. Empty visitor sketches are omitted. A distinct-dimension sketch represents distinct values of that named dimension, independently of the row's visitor sketch.

Each scalar measured value has this layout:

| Order | Field | Encoding and condition |
| --- | --- | --- |
| 1 | Name | `str` |
| 2 | Sum | `f64le` |
| 3 | Measured count | `vi` |
| 4 | Minimum | `f64le`, when measured count is nonzero |
| 5 | Maximum | `f64le`, when measured count is nonzero |
| 6 | Mean | `f64le`, when measured count exceeds one |
| 7 | Population standard deviation | `f64le`, when measured count exceeds one |

Measured count zero denotes a sum-only delta, such as a session metric correction. Minimum, maximum, mean, and standard deviation are omitted in that case. With one measurement, the reader reconstructs mean from minimum and standard deviation as zero. The final field is standard deviation, not variance or an M2 accumulator; `ValueStats.Variance` squares it when requested.

### Optional value details

When header bit `04` is set, the detail section holds an entry for every scalar value, in the same order. A value without optional data has an entry of length zero. A nonempty entry holds this record, whose `u8` flags enable first/last with bit `01` and a quantile sketch with bit `02`; at least one of them is set.

| Order | Field | Encoding and condition |
| --- | --- | --- |
| 1 | Detail flags | `u8` |
| 2 | First value | `f64le`, with bit `01` |
| 3 | First timestamp | `vi`, absolute UTC ticks, with bit `01` |
| 4 | Last value | `f64le`, with bit `01` |
| 5 | Last timestamp offset | `vi(last ticks - first ticks)`, with bit `01` |
| 6 | Quantile sketch | Layout below, with bit `02` |

First/last timestamps identify measurements, not bucket boundaries. Equal-timestamp ties select the smaller first value and larger last value so merge order does not change the result.

## Sketch encodings

In a rollup value, every sketch is length-prefixed, so a reader can skip it without decoding it: the visitor sketch fills its section, each distinct sketch and each value detail has its own entry length, and a quantile sketch lies inside its detail. Within those bounds, a sketch's internal counts determine where its fields end.

### HyperLogLog

| Order | Field | Encoding |
| --- | --- | --- |
| 1 | Precision P | `u8`, supported range 8 through 18 |
| 2 | Representation | `u8`: `01` sparse, `02` dense |
| 3, sparse | Entry count | `vu` |
| 4, sparse | Each sorted entry's difference from the previous entry | `vu`; previous begins at zero |
| 3, dense | Register array | Exactly 2^P bytes, one byte per register |

Sparse entries are the top 32 bits of the internally mixed 64-bit hashes, sorted and deduplicated. Reconstruct each entry by adding its delta to the preceding entry. They are hashes, not visitor IDs. Sparse counts avoid estimation error except for collisions in those 32-bit values. The implementation switches larger sets to dense storage, with an exact-count limit of 2^(P - 2).

Default precision 14 gives 16,384 dense registers, an embedded dense payload of 16,386 bytes including precision and representation, and approximately 0.81% standard error. A rollup with several visitor or distinct sketches can therefore be much larger than its scalar fields suggest, before ZoneTree compression.

Dimension distinct counts begin with the stable FNV-1a hash implemented by `Text.StableHash`: offset basis 14695981039346656037, XOR each UTF-16 code unit, then multiply by 1099511628211 with 64-bit wrapping. This is a hash over UTF-16 code units, rather than UTF-8 bytes. HLL then applies its own SplitMix64 finalizer. Changing either hash changes the meaning of persisted sketches.

### Quantile sketch

The quantile payload stores an unsigned zero count, then a positive bucket store, then a negative bucket store. Each sign store uses:

| Order | Field | Encoding and condition |
| --- | --- | --- |
| 1 | Contiguous bucket span length | `vu`; zero ends an empty store |
| 2 | Minimum bucket index | `vi`, only for a nonempty store |
| 3 | Count for every index in the span | `vu` per bucket, including zero counts |

Indices use the DDSketch mapping `ceil(log(magnitude) / log(gamma))`, with `gamma = (1 + 0.01) / (1 - 0.01)`. Magnitudes strictly below `1e-9` use the zero count. Each sign store retains at most 2,048 contiguous buckets; when a wider span is needed, the smallest magnitudes collapse into the lowest retained bucket. The usual 1% relative-error bound is subject to that collapse and the near-zero bucket.

The payload does not store an accuracy parameter, gamma, or minimum magnitude. Those constants are part of the implementation's format assumptions.

## Active sessions

Sessions use an unpartitioned `IZoneTree<ulong, SessionState>` keyed by visitor hash. The key is a typed `ulong` handled by ZoneTree's default comparer and serializer, rather than an Analytics binary key with a site prefix. Visitor hashing includes site scope. The session ID is a separate field in the value.

`SessionStateSerializer` writes:

| Order | Field | Encoding |
| --- | --- | --- |
| 1 | Presence | `u8`: `00` deleted, then no further bytes; `01` live |
| 2 | Session ID | `vu` |
| 3 | Start ticks | `vi`, absolute UTC ticks |
| 4 | Last-event offset | `vi(last ticks - start ticks)` |
| 5 | Expires-at ticks | `vi`, absolute wall-clock UTC ticks |
| 6 | Page views | `vi` |
| 7 | Interactions | `vi` |
| 8 | Events | `vi` |
| 9 | Entry page | `str` |
| 10 | Entry hostname | `str` |
| 11 | Last page | `str` |
| 12 | Last hostname | `str` |
| 13 | Last-page offset | `vi(last page ticks - start ticks)` |
| 14 | Standard dimension values | Sixteen `str` fields in the fixed order below; no count or names |
| 15 | Custom dimension count | `vu` |
| 16 | Each custom dimension | Name `str`, then value `str` |

The sixteen positional names are `source`, `referrer`, `channel`, `utm_source`, `utm_medium`, `utm_campaign`, `utm_term`, `utm_content`, `country`, `region`, `city`, `browser`, `browser_version`, `os`, `os_version`, and `device`, in that order.

Null means a session dimension was not collected; empty means collected but unknown. Positions missing from a shorter `Dimensions` array, such as the default empty one, are written as null. Only nonnull custom dimensions are written. On reopen, the reader maps custom names into the current session schema and ignores names no longer configured. This permits custom session dimensions to change between runs without changing the sixteen standard positions. The reader rejects any other presence byte and trailing bytes.

The worker's cached site, pending session-start state, and pending metric deltas are transient and are not serialized here. A stored session is hidden when `ExpiresAtUtcTicks` is at or before current wall-clock UTC ticks; normal merges reclaim its bytes. This TTL is independent of the historical timestamps of events in a backfill.

## Covering dimension indexes

All configured dimension indexes share a partitioned `Memory<byte>` / `Memory<byte>` store. Each event with a nonnull value for an indexed dimension contributes a row; missing or null values contribute none, while empty strings can be indexed.

| Order | Key field | Encoding |
| --- | --- | --- |
| 1 | Indexed dimension name | `segment` |
| 2 | Site | `segment` |
| 3 | Equality value | `str` |
| 4 | Event ticks | `u64be`, UTC |
| 5 | Original raw sequence | `u64be` |

The value is the **complete original raw payload**, unchanged. It is not a raw-key pointer. Event name remains in that payload, and time and sequence remain in the index key's final sixteen bytes.

The logical namespace returned by `GetDimensionIndex(dimension).KeyPrefix` is only the first segment. To read one equality range, append the site segment, encoded equality value, and starting timestamp. Stop when the equality prefix changes or the timestamp reaches the exclusive end.

The equality value is length-prefixed, so global ordering across different values is not ordinary string ordering. The useful contiguous range is for a fixed dimension, site, and exact value. Do not pass a full index key to `StorageKeyCodec.DecodeRawEvent`: it has an additional leading namespace and value. Reconstruct the primary raw key from site and the final time/sequence fields before using the public raw key/value decoder.

One event carrying four indexed dimensions can store four full payload copies in addition to its primary raw record. The shared partition target counts those four rows.

## Covering rollup indexes

Each enabled tier has one shared `Memory<byte>` / `RollupValue` index store. A logical index identifies an equality-leading dimension and a joint rollup definition.

| Order | Key field | Encoding |
| --- | --- | --- |
| 1 | Indexed dimension | `segment` |
| 2 | Joint dimension-set identifier | `segment`, canonical comma-joined names |
| 3 | Site | `segment` |
| 4 | Event name | `segment` |
| 5 | Equality value | `str` |
| 6 | Bucket | `u64be` |
| 7 | Original primary rollup key | Complete key bytes through its combined dimension values |

The value is the **complete current primary `RollupValue`**, serialized exactly like a normal rollup row. Index updates copy absolute replacements after primary merges. They do not apply another additive delta to the copied value.

`GetRollupIndex(...).KeyPrefix` contains the first two segments. The remaining fields give a contiguous scan for one site, event, exact value, and bucket range. Appending the original key distinguishes rows that share the equality value and bucket but have different remaining group values. Bucket, site, and event consequently occur both in the index prefix and in the appended original key.

A missing rollup dimension is converted to the `01` sentinel string before encoding the equality value as `str`; this differs from a null length-prefixed string. An empty dimension is an empty `str`. To use `StorageKeyCodec.DecodeRollup`, first locate the appended original key; passing the entire index key does not decode a primary rollup key.

## Metadata file format

Each `meta-a.json` or `meta-b.json` is a UTF-8 header line, a line-feed byte, and an indented JSON object. Despite the extension, the complete file is not a standalone JSON document.

The header consists of three space-separated tokens: `zonetree-analytics-meta`, a positive decimal generation, and the uppercase hexadecimal SHA-256 digest of **all bytes after the first LF**. The digest covers the JSON bytes exactly as written, including whitespace. It does not cover the header.

The JSON body maps string keys to string values, with keys emitted in ordinal order. Numbers, booleans, manifests, and base64 are represented as strings at this layer. The writer escapes some characters, for example `+` in base64 as `+`, the `1F` separator as `\u001F`, and non-ASCII text, so read the file with a JSON parser rather than by text search.

| Key or prefix | Meaning |
| --- | --- |
| `format-version`, `time-zone`, `hll-precision`, `wal-directory` | Reopen compatibility checks |
| `raw-sequence` | Last saved sequence counter |
| `visitor-secret` | Base64 generated 32-byte identity secret; an explicitly configured secret can be supplied externally instead |
| `salt:yyyy-MM-dd` | Base64 daily 32-byte salt for anonymous identity hashing, by UTC date. Kept for the current and previous day; the salt of an older day, created for events with historical timestamps, is kept until a day after its last use |
| `salt-used:yyyy-MM-dd` | UTC ticks of the last use of an older day's salt, saved at most once an hour |
| `rollup:` | Rollup definition activation ticks; event-restricted definitions also encode their event list in the key |
| `aggregate:first-last:` and `aggregate:percentiles:` | Measured-value aggregate activation ticks |
| `distinct:` | Distinct-dimension activation ticks |
| `granularity:` | Activation ticks for an enabled time tier |
| `dimension-index.format` | Dimension-index format, currently `"2"` |
| `dimension-index.dimensions` | Ordinally sorted, newline-separated indexed names |
| `dimension-index.clean` | Whether the completed index checkpoint is reusable with durable source and destination stores |
| `dimension-index.partitions.shared` | Comma-separated shared partition names |
| `dimension-index.dirty.` | Day-tick suffixes for durable pending reconciliation intents |
| `dimension-index.backfill.` | Copy-backfill configuration, phase, cursor, and count |
| `rollup-index.format` | Rollup-index format, currently `"2"` |
| `rollup-index.config` | Sorted, newline-separated registrations; each joins dimension, definition ID, and granularity using `1F` |
| `rollup-index.clean` | Completed reusable rollup-index checkpoint |
| `rollup-index.partitions.` | Per-tier shared partition manifests |
| `rollup-index.dirty.` | Per-tier day intents, with tier and day ticks in the key suffix |
| `rollup-index.backfill.` | Rollup copy-backfill configuration, phase, cursor, and count |
| `rollup-index.backfill-pending` | Newline-separated pending historical joint-rollup definitions |
| `rollup-index.folding` | Completed reconstructions whose rebuilt parts the rollup index build folds in: per line, the coverage start ticks, a tab, and the definition's activation key |

### Generations and writes

A metadata update creates the next generation by replacing the older file in place, then flushing the stream to disk. Generation N goes to `meta-a.json` when N is even and `meta-b.json` when N is odd. Startup validates available headers and checksums and selects the valid file with the highest generation. A missing or checksum-invalid generation can therefore leave the other one usable; existing metadata files with no valid generation cause startup to fail rather than create a new identity secret or reset format settings.

When keys are removed, the store writes two successive generations so the removed entries disappear from both files. This applies to expired salts as well as completed recovery checkpoints. It is removal from the live metadata files, not secure erasure of historical backups or underlying storage media.

Activation metadata records historical coverage. Definitions and aggregates present at dataset creation start at tick zero. A newly enabled definition, aggregate, or tier on an existing dataset starts at its activation time; removing and later restoring it does not claim continuous coverage of the gap. Eligible historical joint-rollup reconstruction can publish earlier coverage after its durable backfill completes and its rows are folded in. Existing row bytes alone are insufficient to infer complete coverage.

## Writes, durability, and recovery

An accepted event first enters the ingestion queue. Raw records, session snapshots, rollups, and index copies are then maintained through different write paths. There is no dataset-wide transaction committing all of them together.

`StoreOptions.WriteAheadLogMode` defaults to `AsyncCompressed`. Persistent tree writes follow the configured ZoneTree WAL mode; in that mode a process that dies can lose its most recent writes. A successful tracking call reports queue acceptance, and a crash can lose queued events and buffered aggregates. `Flush` drains prior accepted ingestion work and writes buffered aggregates and session state; it does not make every configured WAL mode synchronous. Clean disposal drains work, flushes, closes stores, and releases the directory lock. `Compact` additionally moves open trees toward merged disk storage.

Ingestion workers do not write rollup rows themselves. On flush, a worker hands its buffered rows to dedicated rollup writers (`Ingestion.RollupWriterCount`), each of which owns a share of the rows by key. A writer merges the rows of all workers that flushed since its last write, writes each row once, and only then lets those workers persist their session states. Rollup writes serialize the updates of each row. A row's key selects one of 64 lock stripes of its partition, so writers update different rows of a partition in parallel and the same row one at a time; its covering index copies are written under the same lock, in order. The writer merges a delta with the existing value once, retains the absolute replacement for retries, and finishes a pending replacement before applying a newer delta in its stripe. This prevents retrying a failed upsert from applying the same contribution twice within that write path. It does not provide crash-atomic coordination between raw events and every aggregate tier.

### Index reconciliation

The primary raw store is authoritative for dimension copies; primary joint rollups are authoritative for rollup copies. Before changing a source/copy pair, the engine persists a dirty-day intent in metadata. The day is obtained by flooring that record's time coordinate to a whole day; minute/hour/raw coordinates are UTC, while day-rollup coordinates represent local calendar dates.

Opening the engine checks the indexes and decides the work; the work runs on one background thread once ingestion has started, in this order: dimension indexes, historical joint-rollup reconstruction, rollup indexes. Queries do not use an index until its work completes.

An index copy failure leaves the affected indexes unavailable and their checkpoint incomplete. Maintenance continues with the remaining jobs, then fails `WaitForIndexesAsync` with `AnalyticsStorageException`. Reopening the engine resumes the failed build. `IndexMaintenance` is null whenever no job is running, including after such a failure.

| State | Behavior |
| --- | --- |
| Matching configuration, format, retained partitions, and clean checkpoint | Reuse completed indexes |
| Dirty-day intents with an otherwise reusable checkpoint | Remove affected copies and reconstruct those days from authoritative records, including interrupted deletions |
| Interrupted build of the same configuration | Resume from its checkpoint, then repair the days written since it began |
| Configuration change, new indexes, or missing retained index partitions | Drop the copies when the engine opens, then rebuild the shared index stores |
| Source or destination WAL disabled | Rebuild instead of trusting a durable completed checkpoint |
| Explicit incompatible index format | Fail the format check |

Ingestion keeps writing its own copies while a build runs. A dimension-index build reads the raw events up to the sequence stored before the engine opened; ingestion copied the later ones. Copying an event again writes the same key and value. Erasure and each chunk of a build take turns, and every chunk reads the raw events again, so a build never copies an erased event back. A rollup-index build writes each copy under the row lock of the flushes, from the row's current value.

Copy backfills use metadata keys ending in `.config`, `.phase`, `.partition`, `.key`, and `.count`. Phase is `clear` or `copy`; the cursor key is base64, and the count is decimal text. When a new build has dropped the old copies, the index format, configuration, and empty partition manifest are recorded with the index marked unclean, so an interrupted build resumes. Checkpoints advance after their writes return, every 100,000 input records and at partition boundaries. Absolute upserts make replay of an unfinished chunk idempotent. With the default asynchronous write-ahead log, copy and reconstruction writes can still be queued when the checkpoint advances. A crash can lose them even when their source raw events were already durable, leaving historical aggregates without those contributions. Synchronous logs for the stores involved make checkpoint recovery exact.

Historical reconstruction of joint rollups has a separate binary journal, `backfill/joint-rollups/journal-a.bin` and `journal-b.bin`, written alternately like the metadata files:

| Order | Field | Encoding |
| --- | --- | --- |
| 1 | Magic | The 8 ASCII bytes `ZTAJRNL1` |
| 2 | Generation | `u64le`, positive |
| 3 | Body length | `u64le` |
| 4 | Body checksum | 32-byte SHA-256 of the body |
| 5 | Body | The checkpoint |

The header is written last, so a torn write never validates; startup reads the valid file with the highest generation. The body holds the configuration signature, coverage, the fence, the raw partitions when the reconstruction began, the site/partition/key cursor, progress counters, active reconstructed sessions, and a redo list of absolute rebuilt parts. Redo records include granularity, partition, primary key bytes, and the serialized rebuilt part, stored as bytes rather than text.

The reconstruction runs while ingestion writes to the same rows. It reads the raw events whose sequence is at most the fence, the raw event sequence when it began; ingestion writes every later event, late ones included, so each event counts once. Its contributions go to the rows' rebuilt parts under the row locks of the flushes. Replaying the absolute rebuilt parts avoids adding the same contributions twice after a crash and never undoes a flush. When the reconstruction begins, it deletes the rows the definitions kept from an earlier activation, before ingestion starts. Raw retention does not pass the coverage start until it completes. Then the journal files are deleted, the definitions are listed in `rollup-index.folding`, and the rollup index build folds the rebuilt parts in before the coverage is published.

Recovery of derived indexes cannot recover raw events lost before persistence or history already removed by retention. Ordinary index reconciliation also does not imply that all ordinary raw history is replayed into every primary rollup after a crash.

## Deletion, expiration, and storage reclamation

| Store | Explicit deletion representation | Expiration rule |
| --- | --- | --- |
| Raw events and dimension indexes | Empty `Memory<byte>` value | Event ticks older than the raw retention cutoff |
| Rollups and rollup indexes | Null `RollupValue`, serialized as byte `00` | Bucket older than its tier's cutoff; day cutoffs use local-day ticks |
| Active sessions | Null `SessionState`, serialized as byte `00` | Expires-at ticks at or before wall-clock UTC ticks |

The store deletion predicates hide expired records as retention cutoffs advance. A lease does not restore expired records. Logical absence can therefore precede physical removal of the serialized bytes. Iterators configured to include deleted records are a lower-level diagnostic view.

Maintenance drops fully expired partitions and merges partially expired trees. Whole segment ranges can be discarded only when the key range proves that every record has expired; series-leading and equality-leading layouts require that check within the appropriate prefix. Bottom-segment reclamation follows ZoneTree's merge schedule. A directory size is consequently not an exact measure of currently queryable data.

Defaults retain raw history for 90 days, minute rollups for two days when enabled, hourly rollups for 400 days, and daily rollups indefinitely. The default enabled tiers are Hour and Day. Null retention means keep indefinitely. Retention is rounded in whole UTC days, or whole hours for durations under one day; daily aggregate comparisons use the corresponding reporting-zone date.

Visitor erasure removes retained raw events, their dimension-index copies, and the active session for an explicit visitor. It does not subtract contributions from existing aggregate rollups or their covering indexes. See [retention and erasure](../docs-site/content/docs/operations/retention-and-erasure/retention-and-erasure.mdx) for the public operation.

## Inspecting and transferring a dataset

Use `analytics.Storage.RawEvents`, `GetRollups`, `GetDimensionIndex`, and `GetRollupIndex` to list partitions and acquire disposable leases. `Sessions` exposes its typed tree directly. Metadata files are not exposed as a ZoneTree. Accessors return the engine's actual trees and existing maintainers, and remain valid only while the engine is open.

For raw records, decode the iterator's complete primary key/value pair with `RawEventCodec.Decode`. For rollups, use `StorageKeyCodec.DecodeRollup` and interpret the bucket with `GetBucketStart(granularity, analytics.TimeZone)`. Public key encoders restore canonical layouts. The public raw decoder validates UTF-8, lengths, names, control characters in values, flags, timestamp ranges, and trailing bytes and raises `FormatException` for malformed records. The rollup serializer checks the header, section framing, and trailing bytes when a row is read, and a section's contents when it is decoded; malformed bytes raise `FormatException`. Neither it nor the session serializer validates every field, so do not treat them as general untrusted-file validators.

Index accessors expose a logical namespace inside a shared tree. Restrict iterators to `KeyPrefix`, seek into that prefix, and stop as soon as it no longer matches. Their keys use the index layouts above, not the public primary-key decoder layouts.

Treat stored instances, arrays, and sketches as immutable. Direct tree writes bypass ingestion, aggregation, index coordination, session caches, and coverage metadata. Do not dispose or drop engine-owned trees or maintainers, or create an additional maintainer. Release leases before engine disposal.

For a coordinated file copy, close the engine cleanly and copy the entire data root plus the separate WAL root, if configured. Preserve metadata, sessions, all tiers, and any active recovery journal together. If the identity secret is supplied in configuration, retain that configuration securely as well. Reopening requires the persisted format settings, including the same configured absolute WAL root; relocation of that root is not supported by simply changing the option. Test a restored copy in isolation before depending on it. [Deployment and lifecycle](../docs-site/content/docs/operations/deployment/deployment.mdx) covers operational ownership and backups.

## Implementation map

These source files define the contracts described here:

| Contract | Source |
| --- | --- |
| Every format version and its check | [StorageFormats.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/StorageFormats.cs) |
| Dataset settings checks and directory ownership | [AnalyticsEngine.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/AnalyticsEngine.cs) |
| Store types, comparers, serializers, deletion predicates, and WAL routing | [StoreFactory.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/StoreFactory.cs) |
| Partition discovery, time routing, and high-water marks | [PartitionedStore.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/PartitionedStore.cs) |
| Visitor hashing and salt lifetime | [VisitorHasher.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Privacy/VisitorHasher.cs) |
| Primitive binary encoding | [ByteWriter.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Internal/ByteWriter.cs) |
| Primary key layouts and public key codecs | [KeyCodec.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/KeyCodec.cs), [StorageKeyCodec.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/StorageKeyCodec.cs) |
| Raw payloads | [RawEventCodec.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/RawEventCodec.cs) |
| Aggregate payloads and optional value details | [RollupValue.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/RollupValue.cs) |
| Session payloads | [SessionState.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/SessionState.cs), [StandardNames.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/StandardNames.cs) |
| Embedded sketches | [HyperLogLog.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Sketches/HyperLogLog.cs), [QuantileSketch.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Sketches/QuantileSketch.cs) |
| Index key layouts | [DimensionIndexKeyCodec.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/DimensionIndexKeyCodec.cs), [RollupIndexKeyCodec.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/RollupIndexKeyCodec.cs) |
| Metadata generations and historical coverage | [MetaStore.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/MetaStore.cs), [RollupActivations.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/RollupActivations.cs) |
| Background index work and its order | [IndexMaintenance.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/IndexMaintenance.cs) |
| Copy recovery, historical reconstruction, and its journal | [IndexRecovery.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/IndexRecovery.cs), [JointRollupReconstruction.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/JointRollupReconstruction.cs), [JointBackfillCheckpoint.cs](https://github.com/ZoneTree/ZoneTree.Analytics/blob/main/src/ZoneTree.Analytics/Storage/JointBackfillCheckpoint.cs) |

## Related pages

- [Engine API and advanced storage access](../docs-site/content/docs/reference/engine/engine.mdx)
- [Secondary indexes and backfilling](../docs-site/content/docs/performance/secondary-indexes/secondary-indexes.mdx)
- [Configuration reference](../docs-site/content/docs/reference/configuration/configuration.mdx)
- [Time and accuracy](../docs-site/content/docs/concepts/time-and-accuracy/time-and-accuracy.mdx)
- [Deployment and lifecycle](../docs-site/content/docs/operations/deployment/deployment.mdx)

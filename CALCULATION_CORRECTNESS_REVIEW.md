# Calculation correctness review

Originally reviewed 2026-10-11, revision `263f6ec`, using the local Release build on .NET 10. The findings below describe that original revision.

## Implemented corrections

All seven findings have been addressed:

- Calendar boundaries refine crossing storage buckets using minute/raw data. When exact data is unavailable, missing coverage is reported. Compatible daily rollups remain one contiguous read.
- Window funnels retain the latest viable start for each step prefix, preserving overlapping attempts without allowing one event to complete multiple steps.
- Rollup coverage starts at its aligned availability boundary, while only the unavailable portion is refined.
- Raw session replay recognizes the actual start independently of earlier late events and carries that information across parallel slices. Timestamp ties preserve the first ingested event.
- Base-event row restrictions prepare the base source independently of selected/conditional metrics, and require actual base-event counts. Totals retain their documented semantics.
- Numeric aggregates store the mean and population standard deviation independently of the sum, and merge spread using a scaled norm. Representable means and spreads survive overflowing sums and intermediate squared moments.
- Quantile representatives use logarithmic scaling and stay finite at extreme input magnitudes. Histogram bins also retain those finite values.

The library has one storage representation for its unreleased format. No legacy reader or migration path was added. The existing format identifier is advanced to 4 to identify the revised scalar layout. A measured aggregate with more than one value stores mean and standard deviation instead of M2, adding eight bytes per measured value in such a row.

The original audit cases now run as ordinary regression tests. Additional cases cover overlapping/expired funnel attempts, 64 identical funnel steps, half- and quarter-hour offsets, day/week/month/year boundaries, retention/activation coverage boundaries, timestamp ties, parallel replay, extreme signed numeric magnitudes, and flush/compaction/reopen followed by further merges.

This is an empirical code review and test audit, not a proof of correctness for every input or runtime.

## Final validation

- Full Release test suite on .NET 10: **422 passed, 0 failed, 0 skipped**, including all original audit cases and the new boundary/oracle tests.
- Release solution build for .NET 8, 9 and 10: **0 warnings, 0 errors**.
- `git diff --check`: clean.
- Tests exercise the revised single storage format through serialization, flush, compaction, reopen and subsequent ingestion. No format migration or compatibility implementation is included.

## Original audit validation

- Existing test suite: **380 passed, 0 failed** before adding the audit fixture.
- Original additional audit fixture: **12 tests, 3 passed, 9 failed** before corrections. The failures demonstrated **seven distinct defects**; retention planning and quantile overflow each had both an internal and an end-to-end reproduction.
- Independent oracle: 300 deterministic events, four groups, recurring visitors, signed values, distinct dimensions, three ingestion workers. Raw, rollup and automatic queries matched independently calculated event/visitor counts, sum, average, population variance, minimum, maximum, first/last values and distinct counts. Raw and automatic queries also exercised unaligned range edges. Rollup-only queries used aligned bounds because whole-bucket widening is documented behavior.
- Additional controls passed: idle ingestion flushes through the engine timer, and repeated constant `1e16` values have zero variance without overflow.
- Existing tests cover sessions, page flow, conditional metrics, formulas, series functions, comparisons, funnels, sketch serialization and parallel scans. Their passing results do not cover the newly reproduced cases.

To run the calculation regression fixtures and planner/storage checks:

```powershell
dotnet test src/ZoneTree.Analytics.UnitTests/ZoneTree.Analytics.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~Calculation|FullyQualifiedName~QueryRangePlannerTests|FullyQualifiedName~StorageCodecTests"
```

Fixtures: `src/ZoneTree.Analytics.UnitTests/CalculationCorrectnessAuditTests.cs` and `CalculationFixRegressionTests.cs`.
Machine-readable full-suite results: `src/ZoneTree.Analytics.UnitTests/TestResults/calculation-full-suite.trx` (ignored build artifact).

## Original findings

### 1. P1: hourly rollups misassign calendar buckets in non-whole-hour time zones

Location: `Query/QueryEngine.cs:169–182` and `Query/QueryEngine.cs:248–257`.

With UTC storage and a reporting override of UTC+05:30, track events at 18:15 and 18:45 UTC. These belong to different local dates. A daily automatic query returns counts `[2, 0, 0]`, whereas raw events correctly return `[1, 1, 0]` over the same range. The first two rows should each contain one event.

Hourly rollups remain eligible for calendar intervals, and a whole hourly row is assigned using its starting timestamp. The hour containing local midnight therefore sends events after midnight to the preceding date. Refining only the outer range edges does not solve internal output-bucket boundaries. This also affects other aggregates and calendar boundaries for week/month/year intervals when an eligible rollup crosses them.

Suggested correction: ensure every chosen storage bucket fits entirely within one output bucket. Split at reporting boundaries and use minute/raw data for crossing hours, or exclude an incompatible tier. When finer data is unavailable, expose the approximation explicitly.

Reproduction: `DailyBucketsInHalfHourTimeZoneAgreeWithRawEvents`.

### 2. P1: a valid funnel completion is lost after a repeated first step

Location: `Query/FunnelEngine.cs:222–241`.

For steps A → B → C and a ten-minute window, one visitor produces `A@0, A@5, B@6, C@11`. The sequence starting at minute 5 completes in six minutes, so expected step visitors are `[1, 1, 1]`. Actual visitors are `[1, 1, 0]`.

The evaluator retains progress from the first start until its window expires. It then restarts at the most recent A but resets progress to one, discarding B even though B occurred after that A. Dashboard conversion and drop-off results are incorrect for this ordinary pattern.

Suggested correction: retain valid progress for overlapping attempts or the information necessary to reconstruct their matched steps. Simply restarting on every A is insufficient: a repeated A after B can also discard an otherwise valid sequence.

Reproduction: `RepeatedFirstStepPreservesValidFunnelCompletion`.

### 3. P1: automatic planning discards retained rollups when the range starts before their coverage

Location: `Query/QueryRangePlanner.cs:89–95`.

Configure hourly rollups with 100-day retention, raw events with ten-day retention, and no daily tier. Track an event approximately 54 days ago and flush. An approximately 204-day-to-four-day-ago query returns **1** from rollups but **0** with `QuerySource.Auto`, despite the event being retained in the hourly tier.

The planner skips a tier for the entire remaining range when its first candidate bucket precedes `AvailableFrom`. It never divides the range at the coverage boundary. Raw fallback then loses the retained rollup-only data. The raw-retention warning describes missing history, but the omission is avoidable and extends into history that is available. The same planning structure applies to activation boundaries.

Suggested correction: divide the remaining range at the tier's aligned availability boundary, use rollups for the covered portion, and refine only uncovered portions.

Reproductions: `PlannerUsesAvailableRollupsAfterTheirRetentionBoundary`, `AutoQueryKeepsRetainedRollupDataWhenRangeStartsBeforeRetention`.

### 4. P2: a late event preceding the session start removes raw entrance counts

Location: `Query/RawSessionReader.cs:145–166`.

For one explicit visitor, ingest page views in this order: `/entry@10:10`, `/late@10:05`, `/exit@10:15`. All retain the same session ID. Over the full day, rollups report **1 entrance**, while raw queries report **0**.

Raw scanning orders the late event first. It initializes the reconstructed session dimensions, so the subsequent actual session-start event cannot satisfy the additional `Dimensions == null` condition used to recognize an entrance. Raw fallback can therefore change the answer to the same query.

Suggested correction: recognize the session-start event independently of whether another event initialized reconstruction state. Establish and test a consistent policy for earlier late events across ingestion and raw replay.

Reproduction: `EarlierLatePageViewDoesNotRemoveRawEntrance`.

### 5. P2: OnlyRowsWithBaseEvent is ignored when no selected metric reads the base source

Location: `Query/QueryResultBuilder.cs:20–24`; source preparation in `Query/QueryPreparation.cs:117–133`.

Track one signup and no page views. Query the default page-view base with `OnlyRowsWithBaseEvent = true` and only `Metric.EventsOf("signup")`. Expected rows: empty. Actual: one row with a signup count of 1.

The restriction depends on a base source already existing, but source preparation only creates sources required by metrics and HAVING. The documented row restriction changes behavior depending on metric selection.

Suggested correction: prepare/read an unconditioned base source whenever this restriction requires it, independently of selected metrics. Preserve the documented totals behavior.

Reproduction: `BaseEventRowRestrictionWorksWithoutSelectingBaseMetric`.

### 6. P2: finite inputs can produce infinite average and variance

Location: `Storage/RollupValue.cs:20` and `Storage/RollupValue.cs:35–49`.

Aggregate three identical values of `1e308`. The mathematically representable average is `1e308` and the variance is zero; actual average and variance are both positive infinity.

The sum overflows, and subsequent mean/variance merging derives means from that sum. An overflowing sum is unavoidable in a double for this example, but it need not invalidate the representable mean and variance. These results are returned directly by aggregate evaluation; the formula overflow-to-zero behavior does not protect them.

Suggested correction: maintain the mean independently using numerically robust updates, and define an explicit policy for aggregate overflow. Alternatively, enforce and document supported numerical limits at ingestion.

Reproduction: `ConstantLargeValuesHaveZeroVarianceAndFiniteAverage`.

### 7. P2: quantile representative arithmetic overflows and violates the accuracy claim

Location: `Sketches/QuantileSketch.cs:100`; clamping in `Query/Metric.cs:915–928`.

For values `[1e308, double.MaxValue]`, the sketch's median representative is infinity. The public median metric clamps it to `double.MaxValue`, yielding approximately **79.8% error** instead of the documented 1% relative accuracy for the selected rank.

The expression `2 * Math.Pow(Gamma, index) / (Gamma + 1)` can overflow during multiplication even when its final representative should be finite. Clamping masks the overflow but selects the wrong observed extreme.

Suggested correction: use overflow-safe representative arithmetic, including buckets near the largest finite double. Test both quantiles and histogram bins at extreme finite magnitudes.

Reproductions: `QuantileRepresentativeForLargestFiniteValuesStaysFinite`, `PublicPercentileOfLargeFiniteValuesMeetsAccuracy`.

## Accuracy qualifications

The internal quantile implementation has a near-zero bucket and collapses small magnitudes when its 2,048-bucket store is exhausted. The README and public metric documentation now include these qualifications. HyperLogLog's approximately 0.8% figure is a standard error, not a maximum error; sparse “exact” counting uses 32-bit hash prefixes and remains subject to collisions, as its internal comments acknowledge.

A true sum or variance outside the finite range of double still yields infinity. The mean and standard deviation are independent of those overflows. Validation uses the local ZoneTree project reference; the final packaged dependency has not been independently evaluated here.

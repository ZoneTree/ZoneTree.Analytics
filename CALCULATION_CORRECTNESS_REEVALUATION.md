# Calculation correctness reevaluation

Originally evaluated the corrected working tree on 2026-10-11, using the local Release build on .NET 10. The findings below describe the implementation before this second round of corrections.

## Completed corrections

All four defects and the sparse-series contract mismatch have been addressed:

- Raw session measures read earlier assigned late events within the session timeout, collecting only the already discovered session IDs. Ordinary event counts stay within the requested range, and session segments remain in force. If the necessary lookback reaches outside raw retention, the plan reports incomplete data. Session identity/distinct queries do not require that lookback.
- Raw session queries decode the required distinct dimensions and populate their sketches. Standard/custom session dimensions, conditions, entry pages, missing values and known empty values agree across raw, rollup and automatic sources. Unsupported synthetic-session dimensions are rejected consistently.
- Moving averages use a bounded tree of scaled, compensated current-window sums. Recomputing affected branches removes errors left by departed outliers, and power-of-two scales prevent avoidable overflow and rescaling roundoff. The supported 10,000-bucket window is covered by regression tests.
- Linear interpolation uses a finite convex sum for opposite-sign endpoints, avoiding an overflowing endpoint subtraction.
- Lag and lead use interval positions rather than observed-row offsets. The same interval axis defines moving-average windows; with `FillMode.None`, omitted positions contribute zero, and initial windows are clipped to the query range. Day/week/month/year and fixed-width positions work across gaps and DST. The README and public metric documentation specify these semantics.

This phase does not change raw/rollup storage layouts or introduce format migration or compatibility handling.

## Final validation

- Full Release suite on .NET 10: **455 passed, 0 failed, 0 skipped**. The second review's formerly explicit cases are now regular regression tests.
- Release solution build for .NET 8, 9 and 10: **0 warnings, 0 errors**.
- Additional end-to-end checks exercise timeout boundary assignment, exclusion of unrelated sessions, session segments, compaction/reopen, forward continuation, retention warnings, multi-site distinct counts, custom/missing dimensions, sparse calendar shifts, DST, signed cancellation against decimal arithmetic and the maximum window.
- An ASP.NET Core reporting API test verifies corrected moving averages, interpolation, sparse lag and synthetic-session distinct counts through JSON serialization.
- `git diff --check`: clean.

These checks validate the reported cases and their surrounding paths; they do not constitute a proof of correctness for every floating-point input or runtime.

## Original reevaluation evidence

- Existing suite rerun: **422 passed, 0 failed**. This includes the original audit cases and the regression coverage added with the fixes.
- New explicitly selected review fixture: **11 cases, 4 passed, 7 failed**. Failures demonstrate **four additional defects and one sparse-series contract mismatch**. Two cases cover the same late-session defect under different parallelism settings, and two cover different failures of the moving-average implementation.
- Independent traffic model: 20 explicit visitors, 40 sessions over two days, randomized page-view counts, non-interactive heartbeats and three ingestion workers. Session, visitor, page-view, bounce, duration, entrance and exit calculations agreed with the model for unbucketed, hourly and daily queries using raw, rollup and automatic sources.
- A randomized planner oracle checked 500 ranges with independently varied daily/hourly/minute availability boundaries. All exact-read plans covered the requested range continuously, without overlaps, and used rollups only after their availability boundaries.

The passing controls supported ordinary ordered traffic and the corrected coverage planner. The failing cases described below have since been converted into passing regressions.

## 1. P1: raw sessions omit assigned late events before the query's start

Locations: `Query/QueryEngine.cs:327–358`, `Query/QueryEngine.cs:369`, `Query/QueryEngine.cs:444–462`; replay in `Query/RawSessionReader.cs:89`.

One explicit visitor first sends a page view at 00:05, then sends a late page view timestamped 23:55 on the preceding day. Ingestion assigns both events to the session that started at 00:05. Query the start day's session metrics:

| Metric | Rollups / independently expected | Raw events |
| --- | ---: | ---: |
| Sessions | 1 | 1 |
| Bounces | 0 | 1 |
| Page views per session | 2 | 1 |

This reproduces with raw-scan parallelism of both 1 and 4. Retention is disabled in the fixture, so the earlier event is available. The query is aligned to a whole day, avoiding rollup-bound widening.

Raw replay scans the requested range and follows sessions forward. It never reads earlier assigned events outside that range. Rollup session metrics, however, include every assigned event and attribute them to the session-start bucket. Choosing raw data or using a query that falls back to it therefore changes bounce and page-view-per-session results.

Correction direction: complete reconstructed sessions in both directions within the ingestion assignment policy, or revise that policy consistently across ingestion and replay. Any lookback must avoid adding out-of-range events to ordinary event counts and preserve start-timestamp tie behavior.

Reproduction: `SessionMetricsIncludeAssignedLateEventsBeforeTheQueryRange`.

## 2. P2: raw distinct counts on the synthetic session event are always missing

Locations: `Query/QueryEngine.cs:318–319` and `Query/QueryEngine.cs:444–462`.

Configure distinct rollups for `country`, then ingest one session from DE and one from US. Query the base event `StandardEvents.Session` with `Metric.Distinct("country")`:

- Rollups return **2**, as expected.
- Raw events return **0**.

The public query accepts this metric and event combination. Raw source preparation only selects distinct dimensions for ordinary event sources, and `AddRawSession` does not add distinct sketches at all. The session source consequently has no distinct aggregate to evaluate. Without the opt-in rollup, automatic fallback also uses the deficient raw path.

Correction direction: include the required session dimensions in decoding and populate distinct sketches from reconstructed session dimensions, or explicitly reject a combination the API does not intend to support. Returning zero is incorrect.

Reproduction: `RawDistinctSessionDimensionsAgreeWithRollups`.

## 3. P2: moving-average state can overflow or retain cancellation errors after values leave the window

Location: `Query/MetricEvaluator.cs:216–229`.

Two independent reproductions use `Metric.MovingAverage(Metric.Last("x"), 2)` with daily buckets:

1. Gauge values `[1e308, 1e308, 1e308]` should average to `1e308` in every row. Later rows instead produce infinity. The internal sum overflows before division even though the actual mean is representable.
2. Gauge values `[1e16, 1, 1, 1]` should give means of **1** after the first observation leaves the window. Those later rows instead return **0**. Adding small values loses them in the large rolling sum, and removing the large observation leaves a permanently incorrect accumulator.

The new stable aggregate moments do not protect the separate moving-average implementation. The second example affects later windows containing only small, exactly representable values, rather than merely the window containing the outlier.

Correction direction: calculate window means with overflow-safe and cancellation-resistant arithmetic. Test recovery after removal, not just precision while large values remain in the window.

Reproductions: `MovingAverageOfLargeConstantGaugesStaysFinite`, `MovingAverageRecoversAfterAnExtremeObservationLeavesTheWindow`.

## 4. P2: linear gap filling overflows between finite endpoints

Location: `Query/MetricEvaluator.cs:159`.

Track a gauge of `-1e308` on the first day and `1e308` on the third day. With `FillMode.Linear`, the missing middle day's value should be **0**. It instead becomes positive infinity.

The subtraction `to - from` overflows before interpolation. Both endpoints and the expected interpolated value are finite; this is avoidable intermediate overflow.

Correction direction: use a numerically stable convex interpolation formula that handles endpoints of opposite signs, and retain endpoint and constant-value behavior.

Reproduction: `LinearInterpolationOfOppositeLargeGaugesStaysFinite`.

## 5. P2 contract mismatch: sparse lag counts observed rows rather than interval positions

Locations: `Query/MetricEvaluator.cs:244`; public contract in `Query/Metric.cs:557–568` and README series-function table.

For daily gauge values of 10 on day 1 and 20 on day 3, with `FillMode.None`, `Lag(..., 1)` on day 3 returns **10**. That value belongs to day 1, two daily intervals earlier. Under the documented “one bucket earlier, zero when there is no such bucket” interpretation, the expected value is **0** because day 2 is absent.

The implementation shifts positions in the list of observed rows. The same positional design applies to lead; moving-average windows likewise follow available rows when gaps are omitted. This behavior is well-defined as a row-based operation, but differs from the documented time-bucket interpretation and can make a seven-bucket comparison span more than seven days.

Resolution: shifts now use interval-position lookup. Moving-average windows also count interval positions, with omitted buckets contributing zero under `FillMode.None`. `Difference` and `Increase` retain consecutive-observation semantics when gaps are omitted; the README makes this distinction explicit.

Reproduction: `LagOnSparseSeriesUsesTheRequestedCalendarBucket`.

## Reproducing this review

The fixture now runs in the normal suite. To run the review cases and additional end-to-end checks:

```powershell
dotnet test src/ZoneTree.Analytics.UnitTests/ZoneTree.Analytics.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~CalculationReevaluationTests|FullyQualifiedName~CalculationEndToEndTests|FullyQualifiedName~ReportingApiSerializesCorrectedSeriesAndSessionDistinctCounts"
```

Sources: `src/ZoneTree.Analytics.UnitTests/CalculationReevaluationTests.cs`, `CalculationEndToEndTests.cs` and `AspNetCoreTests.cs`.
Full-suite results: `src/ZoneTree.Analytics.UnitTests/TestResults/calculation-final-suite.trx` (ignored artifact).

The original reevaluation added probes without changing production code. The follow-up implements the corrections described above and retains them as regular regression coverage.

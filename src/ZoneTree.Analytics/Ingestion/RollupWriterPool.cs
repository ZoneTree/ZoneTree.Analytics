using ZoneTree.Analytics.Internal;
using ZoneTree.Analytics.Storage;
using ZoneTree.Logger;

namespace ZoneTree.Analytics.Ingestion;

/// <summary>
/// One worker's flush through every rollup writer: the outcome of its piece for each writer.
/// Owned and reused by its worker; a worker has at most one flush in flight.
/// </summary>
sealed class RollupHandoff(int writers)
{
  readonly object sync = new();

  int remaining;

  public readonly bool[] Accepted = new bool[writers];

  public readonly Exception[] Failures = new Exception[writers];

  public void Begin()
  {
    Array.Clear(Accepted);
    Array.Clear(Failures);
    remaining = Accepted.Length;
  }

  public void Complete(int writer, bool accepted, Exception failure)
  {
    lock (sync)
    {
      Accepted[writer] = accepted;
      Failures[writer] = failure;
      if (--remaining == 0)
        Monitor.PulseAll(sync);
    }
  }

  public void Wait()
  {
    lock (sync)
    {
      while (remaining > 0)
        Monitor.Wait(sync);
    }
  }
}

/// <summary>A worker's rows for one rollup writer.</summary>
sealed record RollupPiece(RollupHandoff Handoff, int Writer, Dictionary<RollupBufferKey, RollupAccumulator> Rows);

/// <summary>
/// Dedicated threads that write the rollup rows of every worker. Each writer owns the rows
/// whose key hashes to it, so a row is written by one writer only.
/// </summary>
/// <remarks>
/// <para>
/// Workers are routed by visitor, so the busy rows of a bucket, such as its totals, are buffered
/// by every worker. A writer merges the pieces of all workers that flushed since its last write,
/// and writes each row once instead of once per worker. Flushes of all workers start together
/// on the flush timer, so their pieces meet.
/// </para>
/// <para>
/// A worker waits for every writer before it persists its session states, as when it wrote its
/// rollups itself. A writer that accepts a piece owns its rows: when their write fails, it keeps
/// them and retries them before any later piece. While that retry fails, it rejects new pieces,
/// so their workers keep their rows, and the buffers they bound, until storage recovers.
/// </para>
/// </remarks>
sealed class RollupWriterPool : IDisposable
{
  /// <summary>The rows of a piece without rows. Never modified: merges skip empty pieces.</summary>
  static readonly Dictionary<RollupBufferKey, RollupAccumulator> NoRows = [];

  readonly RollupStore rollups;

  readonly ILogger logger;

  readonly Writer[] writers;

  public RollupWriterPool(RollupStore rollups, int count, ILogger logger)
  {
    this.rollups = rollups;
    this.logger = logger;
    writers = new Writer[count];
    for (var i = 0; i < count; ++i)
      writers[i] = new Writer(this, i);
  }

  public int Count => writers.Length;

  long rowsWritten;

  /// <summary>The rows the writers have written, each merged row once.</summary>
  internal long RowsWritten => Interlocked.Read(ref rowsWritten);

  /// <summary>The pieces waiting for a writer.</summary>
  internal int QueuedPieces => writers.Sum(writer => writer.Queued);

  /// <summary>The writer that owns a row.</summary>
  int WriterOf(in RollupBufferKey key) => (int)((uint)key.GetHashCode() % (uint)writers.Length);

  /// <summary>
  /// Writes a worker's flushed rows with its rows kept from failed flushes, and waits. Rows a
  /// writer did not accept stay in <paramref name="kept"/>, by writer, for the next flush.
  /// </summary>
  /// <exception cref="AnalyticsStorageException">
  /// A writer failed; its rows are retained for a retry. Failures of further writers are logged.
  /// </exception>
  public void Write(Dictionary<RollupBufferKey, RollupAccumulator> rows,
      Dictionary<RollupBufferKey, RollupAccumulator>[] kept, RollupHandoff handoff)
  {
    if (writers.Length == 1 && kept[0] == null)
    {
      // The only writer takes the swapped buffer as it is.
      kept[0] = rows;
    }
    else
    {
      foreach (var (key, accumulator) in rows)
        RollupAccumulator.MergeInto(kept[WriterOf(key)] ??= [], key, accumulator);
    }
    handoff.Begin();
    // Every writer answers, even without rows: its answer also tells whether the rows it
    // accepted from an earlier flush have been written since.
    for (var i = 0; i < writers.Length; ++i)
      writers[i].Post(new RollupPiece(handoff, i, kept[i] ?? NoRows));
    handoff.Wait();
    Exception failure = null;
    for (var i = 0; i < writers.Length; ++i)
    {
      if (handoff.Accepted[i])
        kept[i] = null;
      var writerFailure = handoff.Failures[i];
      if (writerFailure == null || ReferenceEquals(writerFailure, failure))
        continue;
      if (failure == null)
        failure = writerFailure;
      else
        logger.LogError(writerFailure);
    }
    // A writer completes every piece of a failed batch with the same failure. Each worker throws
    // an exception of its own: throwing one instance on several threads mixes their stack traces.
    if (failure != null)
      throw new AnalyticsStorageException("Writing rollups failed: " + failure.Message, failure);
  }

  public void Dispose()
  {
    foreach (var writer in writers)
      writer.Stop();
    var failures = new List<Exception>();
    foreach (var writer in writers)
    {
      writer.Join();
      if (writer.ShutdownFailure != null)
        failures.Add(writer.ShutdownFailure);
    }
    Disposal.ThrowFailures(failures);
  }

  sealed class Writer
  {
    readonly RollupWriterPool pool;

    readonly object sync = new();

    readonly Thread thread;

    List<RollupPiece> queue = [];

    bool stopping;

    // Accepted rows whose write failed, retried before any later piece. Changed by the writer
    // thread only; hasFailed publishes whether it is set to workers posting empty pieces.
    RollupWriteBatch failed;

    volatile bool hasFailed;

    /// <summary>A failure to release the rows still failing at shutdown, reported by Dispose.</summary>
    public Exception ShutdownFailure { get; private set; }

    public Writer(RollupWriterPool pool, int index)
    {
      this.pool = pool;
      thread = new Thread(Run) { IsBackground = true, Name = "ZoneTree.Analytics rollup writer " + index };
      thread.Start();
    }

    public void Post(RollupPiece piece)
    {
      lock (sync)
      {
        if (stopping)
        {
          piece.Handoff.Complete(piece.Writer, false, new ObjectDisposedException(nameof(RollupWriterPool)));
          return;
        }
        // A worker's earlier rows are written before it flushes again, or kept as the failed
        // batch: without rows and without a failed batch, there is nothing to wait for.
        if (piece.Rows.Count == 0 && !hasFailed)
        {
          piece.Handoff.Complete(piece.Writer, true, null);
          return;
        }
        queue.Add(piece);
        Monitor.Pulse(sync);
      }
    }

    public void Stop()
    {
      lock (sync)
      {
        stopping = true;
        Monitor.Pulse(sync);
      }
    }

    public void Join() => thread.Join();

    public int Queued
    {
      get
      {
        lock (sync)
          return queue.Count;
      }
    }

    void Run()
    {
      while (true)
      {
        List<RollupPiece> pieces;
        lock (sync)
        {
          while (queue.Count == 0 && !stopping)
            Monitor.Wait(sync);
          if (queue.Count == 0)
            break;
          pieces = queue;
          queue = [];
        }
        Process(pieces);
      }
      // Rows still failing at shutdown are lost, as a worker's retained rows are. Nothing may
      // escape this thread: an unhandled exception would end the process.
      try
      {
        failed?.Dispose();
      }
      catch (Exception exception)
      {
        ShutdownFailure = exception;
      }
      failed = null;
      hasFailed = false;
    }

    /// <summary>Writes the pieces; every piece is completed, whatever fails.</summary>
    void Process(List<RollupPiece> pieces)
    {
      // Until their rows are in a batch, rejected pieces stay with their workers.
      var accepted = false;
      Exception failure = null;
      try
      {
        if (failed != null)
        {
          // While storage is still failing, the exception keeps the new rows with the workers.
          pool.rollups.Write(failed);
          failed.Dispose();
          failed = null;
          hasFailed = false;
        }
        var batch = Merge(pieces, out var rows);
        accepted = true;
        if (batch != null)
        {
          try
          {
            pool.rollups.Write(batch);
            batch.Dispose();
            Interlocked.Add(ref pool.rowsWritten, rows);
          }
          catch
          {
            // Published before the pieces complete, so their workers' next empty pieces wait.
            failed = batch;
            hasFailed = true;
            throw;
          }
        }
      }
      catch (Exception exception)
      {
        failure = exception;
      }
      foreach (var piece in pieces)
        piece.Handoff.Complete(piece.Writer, accepted, failure);
    }

    /// <summary>One batch of the rows of all pieces, each row once; null when they have none.</summary>
    static RollupWriteBatch Merge(List<RollupPiece> pieces, out int rows)
    {
      rows = 0;
      Dictionary<RollupBufferKey, RollupAccumulator> merged = null;
      foreach (var piece in pieces)
      {
        if (piece.Rows.Count == 0)
          continue;
        if (merged == null)
        {
          merged = piece.Rows;
          continue;
        }
        foreach (var (key, accumulator) in piece.Rows)
          RollupAccumulator.MergeInto(merged, key, accumulator);
      }
      if (merged == null)
        return null;
      rows = merged.Count;
      return new RollupWriteBatch(merged);
    }
  }
}

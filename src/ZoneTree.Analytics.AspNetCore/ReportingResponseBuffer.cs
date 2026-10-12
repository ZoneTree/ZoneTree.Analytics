namespace ZoneTree.Analytics.AspNetCore;

sealed class ReportingResponseTooLargeException : Exception;

// Keep both the logical length and the allocated capacity within the response budget.
sealed class ReportingResponseBuffer(int limit) : MemoryStream
{
  void Reserve(int count)
  {
    var required = Position + count;
    if (required > limit)
      throw new ReportingResponseTooLargeException();
    if (required > Capacity)
      Capacity = (int)Math.Min(limit, Math.Max(required, Math.Max(256, Capacity * 2L)));
  }

  public override void Write(byte[] buffer, int offset, int count)
  {
    Reserve(count);
    base.Write(buffer, offset, count);
  }

  public override void Write(ReadOnlySpan<byte> buffer)
  {
    Reserve(buffer.Length);
    base.Write(buffer);
  }

  public override void WriteByte(byte value)
  {
    Reserve(1);
    base.WriteByte(value);
  }

  public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Write(buffer, offset, count);
    return Task.CompletedTask;
  }

  public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Write(buffer.Span);
    return ValueTask.CompletedTask;
  }
}

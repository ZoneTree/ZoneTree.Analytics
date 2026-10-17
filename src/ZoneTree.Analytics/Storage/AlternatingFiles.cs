using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZoneTree.Analytics.Storage;

/// <summary>
/// Two files written in turn, each holding a generation and the checksum of its body. A write
/// replaces the older file, so a crash can only damage the file being written; loading picks the
/// valid file with the highest generation. No rename and no directory flush are involved, which
/// makes the scheme equally durable on every platform.
/// </summary>
/// <remarks>
/// File layout: <c>magic(8) generation(8, LE) body length(8, LE) sha256 of body(32) body</c>.
/// The header is written last, so a torn write never carries a matching checksum.
/// </remarks>
sealed class AlternatingFiles
{
  const int HeaderLength = 56;

  readonly byte[] magic;

  readonly string[] paths;

  long generation;

  /// <param name="pathA">The file of even generations.</param>
  /// <param name="pathB">The file of odd generations.</param>
  /// <param name="magic">The 8 bytes that start every file of this kind.</param>
  public AlternatingFiles(string pathA, string pathB, ReadOnlySpan<byte> magic)
  {
    this.magic = magic.ToArray();
    paths = [pathA, pathB];
  }

  /// <summary>Whether either file exists, valid or not.</summary>
  public bool Exist => paths.Any(File.Exists);

  /// <summary>
  /// Reads the body of the valid file with the highest generation; the default value when no
  /// file is valid. Later writes go after that generation.
  /// </summary>
  public T Load<T>(Func<Stream, T> read)
  {
    string newest = null;
    foreach (var path in paths)
    {
      if (!File.Exists(path))
        continue;
      using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
      if (TryReadHeader(stream, out var fileGeneration) && fileGeneration > generation)
      {
        generation = fileGeneration;
        newest = path;
      }
    }
    if (newest == null)
      return default;
    using var body = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    body.Position = HeaderLength;
    return read(body);
  }

  /// <summary>Writes the next generation over the older file and flushes it to disk.</summary>
  public void Save(Action<Stream> write)
  {
    var path = paths[(generation + 1) % 2];
    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
    {
      stream.Write(new byte[HeaderLength]);
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      using (var body = new HashingStream(stream, hash))
        write(body);
      var length = stream.Position - HeaderLength;
      var header = new byte[HeaderLength];
      magic.CopyTo(header, 0);
      BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), generation + 1);
      BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), length);
      hash.GetHashAndReset().CopyTo(header, 24);
      stream.Position = 0;
      stream.Write(header);
      stream.Flush(flushToDisk: true);
    }
    ++generation;
  }

  /// <summary>Deletes both files.</summary>
  public void Delete()
  {
    foreach (var path in paths)
      File.Delete(path);
    generation = 0;
  }

  /// <summary>Validates the header and the checksum of the whole body.</summary>
  bool TryReadHeader(FileStream stream, out long fileGeneration)
  {
    fileGeneration = 0;
    var header = new byte[HeaderLength];
    if (stream.Length < HeaderLength || stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) != HeaderLength ||
        !header.AsSpan(0, 8).SequenceEqual(magic))
      return false;
    fileGeneration = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
    var length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
    if (fileGeneration < 1 || length != stream.Length - HeaderLength)
      return false;
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = new byte[1 << 16];
    int read;
    while ((read = stream.Read(buffer)) > 0)
      hash.AppendData(buffer, 0, read);
    return hash.GetHashAndReset().AsSpan().SequenceEqual(header.AsSpan(24, 32));
  }

  /// <summary>Forwards writes to a stream and hashes them.</summary>
  sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
  {
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
      hash.AppendData(buffer);
      inner.Write(buffer);
    }
  }
}

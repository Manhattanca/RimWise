namespace RimWise.Integration.Game;

// A byte-count snapshot, not a size quota. It includes every byte present when opened,
// without chasing a live writer indefinitely or allocating the file's full length.
internal sealed class LogSnapshotStream(Stream source, long length) : Stream
{
    private long remaining = length;
    private readonly long snapshotLength = length;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => snapshotLength;
    public override long Position { get => snapshotLength - remaining; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (remaining == 0 || buffer.Length == 0) return 0;
        var count = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        if (count == 0) throw new IOException("本轮游戏日志在读取期间缩短，不能确认日志完整性。");
        remaining -= count;
        return count;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

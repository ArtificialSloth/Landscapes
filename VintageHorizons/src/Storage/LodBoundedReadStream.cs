namespace VintageHorizons;

/// <summary>
/// A read-only view of another stream that refuses to deliver more than a set number of bytes,
/// so a small compressed blob cannot unpack into an unbounded amount of memory (pre-release
/// review S4). Reading past the limit throws <see cref="InvalidDataException"/>, which the
/// section reader already turns into "not a usable section".
/// </summary>
internal sealed class LodBoundedReadStream : Stream
{
    readonly Stream inner;
    readonly long limit;

    public LodBoundedReadStream(Stream inner, long limit)
    {
        this.inner = inner;
        this.limit = limit;
    }

    /// <summary>Bytes delivered so far.</summary>
    public long Consumed { get; private set; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        long room = limit - Consumed;
        if (room <= 0)
        {
            // Only a stream with more to give is over the limit; one that ends here is fine.
            Span<byte> probe = stackalloc byte[1];
            if (inner.Read(probe) == 0) return 0;
            throw new InvalidDataException($"more than {limit} bytes unpacked");
        }
        int read = inner.Read(buffer[..(int)Math.Min(buffer.Length, room)]);
        Consumed += read;
        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => Consumed;
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

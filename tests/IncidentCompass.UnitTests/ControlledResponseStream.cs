using System.Text;
using System.Threading.Channels;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A response body the test writes to piece by piece. Each <see cref="Write(string)" /> is delivered by
/// a read of its own, and <see cref="WaitForIdleReadAsync" /> completes once the reader has taken every
/// piece written so far and is blocked waiting for the next one, which is the moment a test may
/// advance a manual clock without racing the code under test.
/// </summary>
internal sealed class ControlledResponseStream : Stream
{
    private readonly object gate = new();
    private readonly Channel<byte[]> pieces = Channel.CreateUnbounded<byte[]>();
    private TaskCompletionSource idleRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ReadOnlyMemory<byte> remainder;
    private int written;
    private int taken;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Write(byte[] bytes)
    {
        lock (gate)
        {
            written++;
            if (idleRead.Task.IsCompleted)
            {
                idleRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            pieces.Writer.TryWrite(bytes);
        }
    }

    /// <summary>Ends the body: the reader sees end of stream once it has taken every piece.</summary>
    public void Complete()
    {
        lock (gate)
        {
            pieces.Writer.TryComplete();
        }
    }

    public Task WaitForIdleReadAsync()
    {
        lock (gate)
        {
            return idleRead.Task;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (remainder.IsEmpty)
        {
            lock (gate)
            {
                if (pieces.Reader.TryRead(out var piece))
                {
                    taken++;
                    remainder = piece;
                    continue;
                }

                if (taken == written)
                {
                    idleRead.TrySetResult();
                }
            }

            if (!await pieces.Reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }
        }

        var count = Math.Min(buffer.Length, remainder.Length);
        remainder[..count].CopyTo(buffer);
        remainder = remainder[count..];
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

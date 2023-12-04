using System.Buffers;
using System.Buffers.Binary;
using System.Net.Quic;
using System.Text;

namespace QuicFun.App;

public sealed class QuicReaderActor : ReceiveActor, IWithTimers
{
    private readonly QuicStream _stream;
    private readonly ILoggingAdapter _log = Context.GetLogger();
    
    private sealed class DoRead : INoSerializationVerificationNeeded
    {
        public static DoRead Instance { get; } = new();

        private DoRead()
        {
        }
    }
    
    /// <summary>
    /// Occurs when the socket was closed
    /// </summary>
    private sealed class ReadClosed : INoSerializationVerificationNeeded
    {
        public static ReadClosed Instance { get; } = new();

        private ReadClosed()
        {
        }
    }
    
    private sealed class ReadFailed : INoSerializationVerificationNeeded
    {
        public ReadFailed(Exception exception)
        {
            Exception = exception;
        }
        
        public Exception Exception { get; }
    }
    
    private sealed class ReadCompleted : INoSerializationVerificationNeeded
    {
        public ReadCompleted(IMemoryOwner<byte> buffer)
        {
            Buffer = buffer;
        }
        
        public IMemoryOwner<byte> Buffer { get; }
    }
    
    private const string ReadTimerKey = "read-timer";

    public QuicReaderActor(QuicStream stream)
    {
        _stream = stream;
        _stream.ReadTimeout = 10 * 1000; // 10 seconds 
        Reading();
    }

    private void Reading()
    {
        Receive<DoRead>(_ =>
        {
            if (_stream.CanRead)
            {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                AttemptRead();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            }
            else
            {
                // can't read right now, try again later
                EnsureReadTimer();
            }
        });
        
        Receive<ReadFailed>(failed =>
        {
            _log.Error(failed.Exception, "Error while reading from QUIC stream");
            Context.Stop(Self);
        });
        
        Receive<ReadClosed>(_ =>
        {
            _log.Info("Reads closed - closing stream.");
            Context.Stop(Self);
        });
    }

    protected override void PostStop()
    {
        _stream.Dispose();
    }

    public ITimerScheduler Timers { get; set; }
    
    private async ValueTask AttemptRead()
    {
        // we go with smaller buffers here, because we don't want to greedily read everything
        try
        {
            var buffer = MemoryPool<byte>.Shared.Rent(1024);
            var read = await _stream.ReadAsync(buffer.Memory);
            if (read == 0)
            {
                // end of stream
                Self.Tell(ReadClosed.Instance);
                return;
            }
            
            // decode messages in the buffer using the frame length encoding scheme we developed previously
            var (msgs, remaining) = DecodeBufferedMessages(buffer.Memory[..read]);

            if (remaining?.Memory.Length > 0)
            {
                // partial message, we need to buffer it
                _log.Warning("Partial message received - additional {0} bytes", remaining.Memory.Length);
            }
            
            // work on next read
            Self.Tell(DoRead.Instance);
            
            // echo server functionality
            foreach (var msg in msgs)
            {
                Context.Parent.Tell(msg);
            }
        }
        catch (Exception ex)
        {
            Self.Tell(new ReadFailed(ex));
        }
    }
    
    private static (IReadOnlyList<QuicNetworkProtocol.WriteMsg> msgs, IMemoryOwner<byte>? remaining) DecodeBufferedMessages(ReadOnlyMemory<byte> buffer)
    {
        var span = buffer.Span;
        var msgs = new List<QuicNetworkProtocol.WriteMsg>();
        while (span.Length > 0)
        {
            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(span[..4]);
            if(frameLength > span.Length)
                break; // not enough data in the buffer to read the next message
            
            var recipient = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(4, 8));
            var msg = Encoding.UTF8.GetString(span[12..]);
            msgs.Add(new QuicNetworkProtocol.WriteMsg(recipient, msg));
            
            var spanAdjustmentLength = frameLength + 4;
            span = span[spanAdjustmentLength..];
        }

        if (span.Length > 0)
        {
            var newBuffer = MemoryPool<byte>.Shared.Rent(span.Length);
            span.CopyTo(newBuffer.Memory.Span);
            return (msgs, newBuffer);
        }

        return (msgs, null);
    }
    
    private void EnsureReadTimer()
    {
        if (!Timers.IsTimerActive(ReadTimerKey))
        {
            Timers.StartSingleTimer(ReadTimerKey, DoRead.Instance, TimeSpan.FromMilliseconds(20));
        }
    }
    
    protected override void PreStart()
    {
        // start the read loop
        Self.Tell(DoRead.Instance);
        
        var self = Self;
        _stream.ReadsClosed.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _log.Error(t.Exception, "Error while reading from QUIC stream");
                self.Tell(ReadClosed.Instance);
            }
            else
            {
                _log.Info("Reads closed");
                self.Tell(ReadClosed.Instance);
            }
        });
    }
}
using System.Buffers;
using System.Buffers.Binary;
using System.Net.Quic;
using System.Text;

namespace QuicFun.App;

/// <summary>
/// These are duplex streams, but you never want to have input and output from the same process
/// passing through the same inbox, so we split them into two actors.
/// </summary>
internal sealed class QuicWriterActor : ReceiveActor, IWithTimers
{
    private readonly QuicStream _stream;
    private readonly ILoggingAdapter _log = Context.GetLogger();

    public const int DoubleSize = 8; // doubles take 8 bytes
    public const int FrameLength = 4; // frames are 4 bytes
    
    /// <summary>
    /// used for buffering when writing is unavailable
    /// </summary>
    private readonly Queue<QuicNetworkProtocol.WriteMsg> _writeQueue = new();
    
    public sealed class CloseStream : INoSerializationVerificationNeeded
    {
        public static CloseStream Instance { get; } = new();

        private CloseStream()
        {
        }
    }
    
    private sealed class StreamClosed : INoSerializationVerificationNeeded
    {
        public static StreamClosed Instance { get; } = new();

        private StreamClosed()
        {
        }
    }
    
    private sealed class TryToFlushBufferedWrites : INoSerializationVerificationNeeded
    {
        public static TryToFlushBufferedWrites Instance { get; } = new();

        private TryToFlushBufferedWrites()
        {
        }
    }
    
    private sealed class WriteCompleted : INoSerializationVerificationNeeded
    {
        public static WriteCompleted Instance { get; } = new();

        private WriteCompleted()
        {
        }
    }
    
    private sealed class WriteFailed : INoSerializationVerificationNeeded
    {
        public WriteFailed(Exception exception)
        {
            Exception = exception;
        }

        public Exception Exception { get; }
    }

    public const int DefaultBufferSize = 65536;
    public const string WriteTimerKey = "write-timer";

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public QuicWriterActor(QuicStream stream)
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    {
        _stream = stream;
        _stream.WriteTimeout = 500; // 500ms timeout
        Sending();
    }

    private void Sending()
    {
        Receive<QuicNetworkProtocol.WriteMsg>( msg =>
        {
            /*
            * Algorithm:
            *
            * 1. Buffered messages are written first - if there are any.
            * 2. When there are buffered messages, keep emptying them until the stream says we can't any more.
            * 3. If there are no buffered messages, write the message directly to the stream.
            * 4. If the stream says we can't write, buffer the message.
            * 5. If we can't write to the stream, use a timer to check if we can write again in 20ms.
            */
           
            if(_writeQueue.Count > 0)
            {
                // we can't write to the stream, so we need to buffer this message
                _writeQueue.Enqueue(msg);
               
                // we have buffered messages - have to send those first
                AttemptToWriteBufferedMessages();
            }
            else
            {
                // no buffered messages, so we can write directly to the stream
                if (_stream.CanWrite)
                {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    AttemptToWriteSingleMessage(msg);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
                else // can't write to stream right now, have to buffer
                {
                    _writeQueue.Enqueue(msg);
                    EnsureBufferFlush();
                }
            }
        });
        
        Receive<TryToFlushBufferedWrites>(_ =>
        {
            AttemptToWriteBufferedMessages();
        });
        
        Receive<StreamClosed>(_ =>
        {
            _log.Info("Stream is closed for writing.");
            Context.Stop(Self);
        });
    }

    private void EnsureBufferFlush()
    {
        if (!Timers.IsTimerActive(WriteTimerKey))
        {
            Timers.StartPeriodicTimer(WriteTimerKey, TryToFlushBufferedWrites.Instance, TimeSpan.FromMilliseconds(20));
        }
    }

    private void AttemptToWriteBufferedMessages()
    {
        if (_stream.CanWrite)
        {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            WriteBufferedMessages(); // kick off writes to the stream asynchronously
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }
        else
        {
            EnsureBufferFlush();
        }
    }

    // create a WriteBytes implementation for multiple messages
    private async ValueTask WriteBufferedMessages()
    {
        var self = Self;
        // there are buffered messages, so we need to write them first
        var (bufferOwner, writtenBytes) = EncodeBufferedMessages();
        try
        {
            await _stream.WriteAsync(bufferOwner.Memory[..writtenBytes]);
            self.Tell(WriteCompleted.Instance);
        }
        catch (Exception ex)
        {
            self.Tell(new WriteFailed(ex));
        }
        finally
        {
            bufferOwner.Dispose();
        }
    }

    /// <summary>
    /// Method is going to mutate the queue, so it's not thread safe.
    /// </summary>
    /// <param name="msgs">Unprocessed messages</param>
    /// <param name="pool"></param>
    /// <returns></returns>
    private  (IMemoryOwner<byte> memory, int writtenBytes) EncodeBufferedMessages()
    {
        // rent a 64k buffer
        var bufferOwner = MemoryPool<byte>.Shared.Rent(DefaultBufferSize);
        var span = bufferOwner.Memory.Span;
        
        // Write frame length (total size - 4 bytes of the length itself)
        var bytesWritten = 0;
        while(_writeQueue.TryPeek(out var msg))
        {
            var byteLength = ComputeMessageLength(msg);
            var frameLength = byteLength + FrameLength; // need to add the extra 4 bytes
            if (bytesWritten + frameLength > bufferOwner.Memory.Length)
            {
                // flush the buffer
                return (bufferOwner, bytesWritten);
            }
            
            span = span[bytesWritten..];
            
            FlushToBuffer(msg, ref span, frameLength);
            bytesWritten += frameLength;
            _writeQueue.Dequeue(); // remove the message from the queue
        }
        
        return (bufferOwner, bytesWritten);
    }

    private ValueTask WriteSingleMessage(QuicNetworkProtocol.WriteMsg msg)
    {
        var msgSize = ComputeMessageLength(msg);
        var framedSize = msgSize + FrameLength;
        var pool = MemoryPool<byte>.Shared;
        using var buffer = pool.Rent(framedSize);
        var span = buffer.Memory.Span;
        FlushToBuffer(msg, ref span, framedSize);
        return _stream.WriteAsync(buffer.Memory); //internal write timeout will handle this
    }

    private async ValueTask AttemptToWriteSingleMessage(QuicNetworkProtocol.WriteMsg msg)
    {
        var self = Self;
        
        try
        {
            await WriteSingleMessage(msg);
            self.Tell(WriteCompleted.Instance);
        }
        catch (Exception ex)
        {
            self.Tell(new WriteFailed(ex));
        }
        
    }

    private static void FlushToBuffer(QuicNetworkProtocol.WriteMsg msg, ref Span<byte> span, int messageLength)
    {
        // Write frame length (total size - 4 bytes of the length itself)
        BinaryPrimitives.WriteInt32LittleEndian(span, messageLength);

        // Write Destination
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(4, 8), msg.Destination);

        // Write Data
        Encoding.UTF8.GetBytes(msg.Data, span[12..]);
    }
    
    private static int ComputeMessageLength(QuicNetworkProtocol.WriteMsg msg)
    {
        var msgLength = Encoding.UTF8.GetByteCount(msg.Data);
        var byteLength = msgLength + DoubleSize;
        return byteLength;
    }

    protected override void PreStart()
    {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        HandleWriteClosed();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        
        async Task HandleWriteClosed()
        {
            var self = Self;
            try
            {
                await _stream.WritesClosed;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error while QUIC stream was closing");
            }
            finally
            {
                self.Tell(StreamClosed.Instance);
            }
        }
    }

    public ITimerScheduler Timers { get; set; }
}
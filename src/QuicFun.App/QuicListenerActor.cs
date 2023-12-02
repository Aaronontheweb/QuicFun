using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Quic;
using System.Text;

namespace QuicFun.App;

public static class QuicNetworkProtocol
{
    public interface IQuickNetworkProtocolMessage : INoSerializationVerificationNeeded
    {
    }

    public sealed class WriteMsg : IQuickNetworkProtocolMessage
    {
        public WriteMsg(long destination, string data)
        {
            Destination = destination;
            Data = data;
        }

        public string Data { get; }

        /// <summary>
        /// Imagine a 64bit hash of an IActorRef - this is the destination of the message.
        /// </summary>
        public long Destination { get; }
    }
}

public class QuicListenerActor : ReceiveActor, IWithStash
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly TimeSpan _bindTimeout = TimeSpan.FromSeconds(3);
    private readonly CancellationTokenSource _terminationCts = new();
    private readonly QuicListenerOptions _options;
    private readonly QuicServerConnectionOptions _serverConnectionOptions;
    private readonly int _maxConcurrentStreams = 10;

    private readonly Dictionary<IPEndPoint, IActorRef> _connectionActors = new();
    private readonly Dictionary<IActorRef, IPEndPoint> _actorsToEndpoints = new();

    /// <summary>
    /// Bound once the listener is bound to a local endpoint.
    /// </summary>
    private QuicListener? _listener;

    private sealed class QuicListenerBound : INoSerializationVerificationNeeded
    {
        public QuicListenerBound(QuicListener listener)
        {
            Listener = listener;
        }

        public QuicListener Listener { get; }
    }

    private sealed class QuicBindFailed : INoSerializationVerificationNeeded
    {
        public QuicBindFailed(Exception exception)
        {
            Exception = exception;
        }

        public Exception Exception { get; }
    }

    private sealed class ShutdownQuicListener : INoSerializationVerificationNeeded
    {
        public static ShutdownQuicListener Instance { get; } = new();

        private ShutdownQuicListener()
        {
        }
    }

    private sealed class AcceptConnections : INoSerializationVerificationNeeded
    {
        public static AcceptConnections Instance { get; } = new();

        private AcceptConnections()
        {
        }
    }

    private sealed class ConnectionAccepted : INoSerializationVerificationNeeded
    {
        public ConnectionAccepted(QuicConnection connection)
        {
            Connection = connection;
        }

        public QuicConnection Connection { get; }
    }

    private sealed class FailedToAcceptConnection(Exception exception) : INoSerializationVerificationNeeded
    {
        public Exception Exception { get; } = exception;
    }

    private sealed class ConnectionClosed : INoSerializationVerificationNeeded
    {
        public ConnectionClosed(IPEndPoint remoteEndPoint)
        {
            RemoteEndPoint = remoteEndPoint;
        }

        public IPEndPoint RemoteEndPoint { get; }
    }

    public sealed class GetHandle : INoSerializationVerificationNeeded
    {
        public GetHandle(IPEndPoint remoteEndPoint)
        {
            RemoteEndPoint = remoteEndPoint;
        }

        public IPEndPoint RemoteEndPoint { get; }
    }

    public interface IHandleResponse : INoSerializationVerificationNeeded
    {
        bool Success { get; }
    }

    public sealed class FoundHandle : IHandleResponse
    {
        public FoundHandle(IActorRef handle)
        {
            Handle = handle;
        }

        public IActorRef Handle { get; }
        public bool Success => true;
    }

    public sealed class HandleNotFound : IHandleResponse
    {
        public static HandleNotFound Instance { get; } = new();

        private HandleNotFound()
        {
        }

        public bool Success => false;
    }

    public QuicListenerActor(IPEndPoint serverEndpoint, QuicServerConnectionOptions serverConnectionOptions)
    {
        _serverConnectionOptions = serverConnectionOptions;
        _options = new QuicListenerOptions
        {
            ListenEndPoint = serverEndpoint,
            ConnectionOptionsCallback = (_, _, _) =>
                new ValueTask<QuicServerConnectionOptions>(_serverConnectionOptions)
        };

        WaitingForBinding();
    }

    private void WaitingForBinding()
    {
        Receive<QuicListenerBound>(bound =>
        {
            _listener = bound.Listener;
            _log.Info("QuicListener bound to {Endpoint}", _listener.LocalEndPoint);
            Become(Listening);
            Stash.UnstashAll();
            Self.Tell(AcceptConnections.Instance);
        });

        Receive<QuicBindFailed>(failed =>
        {
            _log.Error(failed.Exception, "Failed to bind QUIC listener");
            Context.System.Terminate(); // shut down application
        });

        Receive<ShutdownQuicListener>(_ =>
        {
            _log.Info("Shutting down QuicListener");
            Context.System.Terminate();
        });

        ReceiveAny(_ => Stash.Stash());
    }

    private void Listening()
    {
        Receive<GetHandle>(handle =>
        {
            var remoteEndPoint = handle.RemoteEndPoint;
            if (_connectionActors.TryGetValue(remoteEndPoint, out var actor))
            {
                Sender.Tell(new FoundHandle(actor));
            }
            else
            {
                Sender.Tell(HandleNotFound.Instance);
                // TODO: establish outbound connection here - probably want to do this in a separate actor
            }
        });

        Receive<AcceptConnections>(_ =>
        {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
#pragma warning disable CA2012
            AcceptingConnections();
#pragma warning restore CA2012
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        });

        Receive<ConnectionAccepted>(accepted =>
        {
            _log.Info("Connection accepted from {RemoteEndPoint}", accepted.Connection.RemoteEndPoint);
            var connectionActor =
                Context.ActorOf(
                    Props.Create(() => new QuicConnectionActor(accepted.Connection, _maxConcurrentStreams)),
                    $"connection-{accepted.Connection.RemoteEndPoint}");

            Context.WatchWith(connectionActor, new ConnectionClosed(accepted.Connection.RemoteEndPoint));

            Self.Tell(AcceptConnections.Instance);
        });

        Receive<FailedToAcceptConnection>(failed =>
        {
            _log.Error(failed.Exception, "Failed to accept connection");
            Self.Tell(AcceptConnections.Instance);
        });

        ReceiveAsync<ShutdownQuicListener>(async _ =>
        {
            _log.Info("Shutting down QuicListener");

            await _terminationCts.CancelAsync();
            if (_listener != null)
            {
                await _listener.DisposeAsync();
            }

            Context.Stop(Self);
        });

        Receive<ConnectionClosed>(closed =>
        {
            _log.Info("Connection closed from {RemoteEndPoint}", closed.RemoteEndPoint);
            _connectionActors.Remove(closed.RemoteEndPoint, out var actor);
            if (actor != null) _actorsToEndpoints.Remove(actor, out _);
        });
    }

    private async ValueTask AcceptingConnections()
    {
        System.Diagnostics.Debug.Assert(_listener != null, nameof(_listener) + " != null");
        var self = Self;
        try
        {
            var accepted = await _listener.AcceptConnectionAsync(_terminationCts.Token);
            self.Tell(new ConnectionAccepted(accepted));
        }
        catch (Exception ex)
        {
            self.Tell(new FailedToAcceptConnection(ex));
        }
    }

    protected override void PreStart()
    {
        if (!QuicListener.IsSupported)
        {
            // shut down application
            Context.System.Terminate();
            throw new NotSupportedException("QUIC is not supported on this platform.");
        }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        BindQuic();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

        return;

        async Task BindQuic()
        {
            using var timeoutCts = new CancellationTokenSource(_bindTimeout);
            var linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _terminationCts.Token);

            _log.Info("QuicListenerActor started");
            var self = Self;

            try
            {
                var listener = await QuicListener.ListenAsync(_options, linkedCts.Token);
                self.Tell(new QuicListenerBound(listener));
            }
            catch (Exception ex)
            {
                self.Tell(new QuicBindFailed(ex));
            }
        }
    }

    protected override void PostStop()
    {
        if (!_terminationCts
                .IsCancellationRequested) // in the event of an uncontrolled shutdown, do our best to clean up
        {
            _terminationCts.Cancel();
            _terminationCts.Dispose();
#pragma warning disable CA2012
            _listener?.DisposeAsync();
#pragma warning restore CA2012
        }
    }

    public IStash Stash { get; set; }
}

internal class QuicConnectionActor : UntypedActor
{
    private readonly int _maxConcurrentStreams;
    private readonly QuicConnection _acceptedConnection;
    private readonly CancellationTokenSource _terminationCts = new();

    public QuicConnectionActor(QuicConnection acceptedConnection, int maxConcurrentStreams)
    {
        _maxConcurrentStreams = maxConcurrentStreams;
        _acceptedConnection = acceptedConnection;
    }

    protected override void OnReceive(object message)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// These are duplex streams, but you never want to have input and output from the same process
/// passing through the same inbox, so we split them into two actors.
/// </summary>
internal sealed class QuicWriterActor : ReceiveActor, IWithTimers
{
    private readonly QuicStream _stream;
    private readonly CancellationTokenSource _terminationCts;
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

    public QuicWriterActor(QuicStream stream, CancellationTokenSource terminationCts)
    {
        _stream = stream;
        _terminationCts = terminationCts;
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
                   WriteSingleMessage(msg);
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
            await _stream.WritesClosed;
            self.Tell(StreamClosed.Instance);
        }
    }

    public ITimerScheduler Timers { get; set; }
}
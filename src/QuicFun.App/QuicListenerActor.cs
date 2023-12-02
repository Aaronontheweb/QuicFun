using System.Net;
using System.Net.Quic;
using Debug = System.Diagnostics.Debug;

namespace QuicFun.App;

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
        private ShutdownQuicListener() { }
    }
    
    private sealed class AcceptConnections : INoSerializationVerificationNeeded
    {
        public static AcceptConnections Instance { get; } = new();
        private AcceptConnections() { }
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
        private HandleNotFound() { }
        public bool Success => false;
    }

    public QuicListenerActor(IPEndPoint serverEndpoint, QuicServerConnectionOptions serverConnectionOptions)
    {
        _serverConnectionOptions = serverConnectionOptions;
        _options = new QuicListenerOptions
        {
            ListenEndPoint = serverEndpoint,
            ConnectionOptionsCallback = (_, _, _) => new ValueTask<QuicServerConnectionOptions>(_serverConnectionOptions) 
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
            var connectionActor = Context.ActorOf(Props.Create(() => new QuicConnectionActor(accepted.Connection, _maxConcurrentStreams)), $"connection-{accepted.Connection.RemoteEndPoint}");
            
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
        Debug.Assert(_listener != null, nameof(_listener) + " != null");
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
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _terminationCts.Token);
            
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
        if (!_terminationCts.IsCancellationRequested) // in the event of an uncontrolled shutdown, do our best to clean up
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
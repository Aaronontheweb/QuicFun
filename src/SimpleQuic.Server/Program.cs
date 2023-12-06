// See https://aka.ms/new-console-template for more information

using System.Net;
using System.Net.Quic;
using System.Net.Security;

var quicListener = await QuicListener.ListenAsync(new QuicListenerOptions()
{
    ListenEndPoint = new IPEndPoint(IPAddress.Any, 45505),
    ApplicationProtocols = new List<SslApplicationProtocol>()
    {
        new SslApplicationProtocol("simplequic")
    },
    ConnectionOptionsCallback = (connection, info, CancellationToken) => new ValueTask<QuicServerConnectionOptions>(
        new QuicServerConnectionOptions()
        {
            DefaultCloseErrorCode = 0x0B,
            DefaultStreamErrorCode = 0x0A,
            IdleTimeout = TimeSpan.FromSeconds(10),
            MaxInboundBidirectionalStreams = 10,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions()
            {
                ApplicationProtocols = new List<SslApplicationProtocol>()
                {
                  new SslApplicationProtocol("simplequic")   
                }
            }
        })
});

Console.WriteLine($"QUIC-Server: accepting connections {quicListener.LocalEndPoint}");

while (true)
{
    var newConnection = await quicListener.AcceptConnectionAsync();
    Console.WriteLine($"QUIC-Server: accepted new connection from [{newConnection.RemoteEndPoint}]");

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    ProcessConnection();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

    async Task ProcessConnection()
    {
        try
        {
            var openStreams = new List<Task>();
            do
            {
                var newStream = await newConnection.AcceptInboundStreamAsync();
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                openStreams.Add(ProcessStream(newStream));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            } while (!openStreams.TrueForAll(c => c.IsCompleted));

            await newConnection.CloseAsync(0x00);
            await newConnection.DisposeAsync();
        }
        catch
        {
            // connection might have been closed
            
        }
        
    }

    async Task ProcessStream(QuicStream stream)
    {
        var buffer = new byte[4];
        while (!stream.ReadsClosed.IsCompleted)
        {
            await stream.ReadExactlyAsync(buffer);
            await stream.WriteAsync(buffer);
        }

        stream.CompleteWrites();
        stream.Close();
    }
}
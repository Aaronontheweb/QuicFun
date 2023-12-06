
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Threading.Channels;
using Microsoft.VisualBasic;

var quicConnection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions()
{
    DefaultCloseErrorCode = 0x0B,
    DefaultStreamErrorCode = 0x0A,
    IdleTimeout = TimeSpan.FromSeconds(10),
    MaxInboundBidirectionalStreams = 10,
    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 45505),
    ClientAuthenticationOptions = new SslClientAuthenticationOptions()
    {
        ApplicationProtocols = new List<SslApplicationProtocol>()
        {
            new SslApplicationProtocol("simplequic")
        },
        C
    }
});

Console.WriteLine($"QUIC-Client: Connected to {quicConnection.RemoteEndPoint} via {quicConnection.LocalEndPoint}");

const int MAX_ITEMS = 10_000;
List<Task> CloseOps = new List<Task>();

for (var i = 0; i < 10; i++)
{
    try
    {
        var stream = await quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);

        var writeBuffer = new byte[4];
        var readBuffer = new byte[4];

        async Task BeginWritingChannel()
        {
            foreach (var c in Enumerable.Range(0, MAX_ITEMS))
            {
                while (!stream.CanWrite)
                    await Task.Delay(20); // spin 20ms
                
                BinaryPrimitives.WriteInt32LittleEndian(writeBuffer, i);
                await stream.WriteAsync(writeBuffer);
            }
            
            // complete writing the stream
            stream.CompleteWrites();
        }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        BeginWritingChannel();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

        async Task BeginReadingChannel()
        {
            var curCount = 0;
            while (curCount < MAX_ITEMS)
            {
                await stream.ReadExactlyAsync(readBuffer);
                var val = BinaryPrimitives.ReadInt32LittleEndian(readBuffer);
                Console.WriteLine($"Received [{val}] on channel [{i}]");
                curCount++;
            }
            
            stream.Close();
        }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        CloseOps.Add(BeginReadingChannel());
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to open stream due to {ex.Message}");
        return;
    }
}

await Task.WhenAll(CloseOps);
await quicConnection.CloseAsync(0x00);


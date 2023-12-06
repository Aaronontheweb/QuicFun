// See https://aka.ms/new-console-template for more information

using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

var certificate = CreateSelfSignedCertificate();

var quicListener = await QuicListener.ListenAsync(new QuicListenerOptions
{
    ListenEndPoint = new IPEndPoint(IPAddress.Any, 45505),
    ApplicationProtocols = new List<SslApplicationProtocol>
    {
        new("simplequic")
    },
    ConnectionOptionsCallback = (connection, info, CancellationToken) => new ValueTask<QuicServerConnectionOptions>(
        new QuicServerConnectionOptions
        {
            DefaultCloseErrorCode = 0x0B,
            DefaultStreamErrorCode = 0x0A,
            IdleTimeout = TimeSpan.FromSeconds(10),
            MaxInboundBidirectionalStreams = 10,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    new("simplequic")
                },
                ServerCertificate = certificate,
                ClientCertificateRequired = false
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
        }
        catch
        {
            // connection might have been closed
        }
        finally
        {
            await newConnection.DisposeAsync();
        }

        Console.WriteLine($"QUIC-Server: connection from [{newConnection.RemoteEndPoint}] closed.");
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

// From https://github.com/dotnet/runtime/issues/92669#issuecomment-1737844302
static X509Certificate2 CreateSelfSignedCertificate()
{
    using var rsa = RSA.Create();
    var certificateRequest =
        new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    certificateRequest.CertificateExtensions.Add(
        new X509BasicConstraintsExtension(
            false,
            false,
            0,
            true
        )
    );
    certificateRequest.CertificateExtensions.Add(
        new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment |
            X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.KeyCertSign,
            false
        )
    );
    certificateRequest.CertificateExtensions.Add(
        new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                //new Oid("1.3.6.1.5.5.7.3.2"), // TLS Client auth
                new("1.3.6.1.5.5.7.3.1") // TLS Server auth
            },
            false));

    certificateRequest.CertificateExtensions.Add(
        new X509SubjectKeyIdentifierExtension(
            certificateRequest.PublicKey,
            false
        )
    );

    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName("localhost");
    certificateRequest.CertificateExtensions.Add(sanBuilder.Build());

    var cert = certificateRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));

    // windows only
    return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
}
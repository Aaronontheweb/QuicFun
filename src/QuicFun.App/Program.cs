using System.Net;
using System.Net.Quic;
using Akka.Hosting;
using QuicFun.App;
using Microsoft.Extensions.Hosting;

var hostBuilder = new HostBuilder();

hostBuilder.ConfigureServices((context, services) =>
{
    services.AddAkka("MyActorSystem", (builder, sp) =>
    {
        builder
            .WithActors((system, registry, resolver) =>
            {
                var helloActor = system.ActorOf(Props.Create(() => new QuicListenerActor(new IPEndPoint(IPAddress.Loopback, 45550), new QuicServerConnectionOptions()
                {
                    DefaultStreamErrorCode = 0x100,
                    IdleTimeout = TimeSpan.FromSeconds(10),
                    MaxInboundBidirectionalStreams = 10
                })), "hello-actor");
                registry.Register<QuicListenerActor>(helloActor);
            });
    });
});

var host = hostBuilder.Build();

await host.RunAsync();
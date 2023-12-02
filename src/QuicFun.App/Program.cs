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
                var helloActor = system.ActorOf(Props.Create(() => new QuicListenerActor()), "hello-actor");
                registry.Register<QuicListenerActor>(helloActor);
            });
    });
});

var host = hostBuilder.Build();

await host.RunAsync();
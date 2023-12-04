# QuicFun

Lightweight experiments working with the QUIC protocol via .NET 8's [System.Net.Quic APIs](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview) using [Akka.NET actors](https://getakka.net/) to build a responsive, concurrent Quic server that can handle dynamic multi-plexing initiated by clients.

## Motiviation

Quic is a reliable, stream-oriented messaging protocol built on top of UDP that eliminates some of the pain caused by TCP, namely head-of-line blocking. It powers HTTP/3, but it can also be used to be build custom networking protocols that can leverage multiplexing for greater total throughput.

## Current Status

* Building a basic echo protocol server **not working yet, still coding**;
* Determining what all needs dealing with (i.e. partial reads, framing, keep-alive, etc) at application layer vs. what can be natively handled by the underlying protocol itself;
* Seeing how much of this can be done with as little zero-copy as possible;
* Are UDP datagram sizes an issue with `QuicStream`s?
* Which parties, from a partical stand point, should be opening channels in a _peer-to-peer_ protocol?
* On the actor front, how best to dynamically route messages to channels in order to preserve message ordering? Current thinking is `Murmur3` hashing a unique identifer (i.e. string or long integer representation of destination `IActorRef`) and distributing that to appropriate open channel.

## Get Involved / Contact

Pull requests are welcome! Feel free to contact me in the [Akka.NET Discord](https://discord.com/invite/GSCfPwhbWP) (`#dotnet` or `#contributors` channels are fine.)

## Further Reading

* [MsQuic](https://github.com/microsoft/msquic) - native implementation of QUIC protocol built into Windows and distributed by Microsoft to other non-Windows platforms.
* [IETF QUIC specification](https://datatracker.ietf.org/wg/quic/about/) - official protocol specification for QUIC.

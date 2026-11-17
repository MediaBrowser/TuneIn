using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class TransportTests
    {
        private const string PreRoll = "https://tunein-ondemand.cdnstream1.com/multi_bump_sonic_pre_pre.mp3";

        [Theory]
        [InlineData("127.0.0.1", false)]
        [InlineData("10.1.2.3", false)]
        [InlineData("172.31.1.1", false)]
        [InlineData("192.168.1.1", false)]
        [InlineData("169.254.169.254", false)]
        [InlineData("100.64.1.1", false)]
        [InlineData("198.18.0.1", false)]
        [InlineData("192.0.2.1", false)]
        [InlineData("224.0.0.1", false)]
        [InlineData("::1", false)]
        [InlineData("::ffff:127.0.0.1", false)]
        [InlineData("fe80::1", false)]
        [InlineData("fc00::1", false)]
        [InlineData("2001:db8::1", false)]
        [InlineData("2002:7f00:1::", false)]
        [InlineData("8.8.8.8", true)]
        [InlineData("2606:4700:4700::1111", true)]
        public void SpecialUseDestinationsAreRejected(string address, bool expected) =>
            Assert.Equal(expected, PublicAddressPolicy.IsPublic(IPAddress.Parse(address)));

        [Fact]
        public async Task MixedPublicAndPrivateDnsFailsBeforeConnecting()
        {
            var connections = 0;
            var transport = new SafeHttpTransport(_ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }),
                (_, __, ___) => { connections++; throw new Exception("Must not connect"); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
            Assert.Equal(0, connections);
        }

        [Fact]
        public async Task RedirectRevalidatesDnsBeforeSecondConnection()
        {
            var connections = 0;
            var transport = new SafeHttpTransport(host => Task.FromResult(new[] {
                host == "radio.test" ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback }),
                (_, __, ___) =>
                {
                    connections++;
                    return Task.FromResult<Stream>(new DuplexStream("HTTP/1.1 302 Found\r\nLocation: https://127.0.0.1/secret\r\n\r\n"));
                });
            await Assert.ThrowsAsync<InvalidOperationException>(() => transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
            Assert.Equal(1, connections);
        }

        [Theory]
        [InlineData("http://opml.radiotime.com/Tune.ashx")]
        [InlineData("https://evil.test/Tune.ashx")]
        public async Task CatalogueRedirectCannotDowngradeOrLeaveAllowlist(string target)
        {
            var transport = Transport(_ => new DuplexStream("HTTP/1.1 302 Found\r\nLocation: " + target + "\r\n\r\n"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                transport.OpenAsync("https://opml.radiotime.com/Tune.ashx?id=s1", true, CancellationToken.None));
        }

        [Fact]
        public async Task RedirectLimitClosesEveryConnection()
        {
            var connections = new System.Collections.Generic.List<DuplexStream>();
            var transport = Transport(_ =>
            {
                var stream = new DuplexStream("HTTP/1.1 302 Found\r\nLocation: /again\r\n\r\n");
                connections.Add(stream);
                return stream;
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => transport.OpenAsync("https://radio.test/live", false, CancellationToken.None));
            Assert.Equal(6, connections.Count);
            Assert.All(connections, stream => Assert.True(stream.Disposed));
        }

        [Fact]
        public async Task AddressIsPinnedCaseIsPreservedAndChunksAreDecoded()
        {
            var lookups = 0;
            DuplexStream connection = null;
            var transport = new SafeHttpTransport(_ =>
            {
                lookups++;
                return Task.FromResult(new[] { lookups == 1 ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback });
            }, (address, uri, token) =>
            {
                Assert.Equal("8.8.8.8", address.ToString());
                Assert.Equal("radio.test", uri.DnsSafeHost);
                connection = new DuplexStream("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: audio/mpeg\r\n\r\n4\r\nTest\r\n3\r\n123\r\n0\r\n\r\n");
                return Task.FromResult<Stream>(connection);
            });
            using (var response = await transport.OpenAsync("https://radio.test/Case?Token=AbC", false, CancellationToken.None))
            {
                Assert.Equal("Test123", await Text(response.Body));
                Assert.Contains("GET /Case?Token=AbC HTTP/1.1", connection.Request);
                Assert.Contains("Host: radio.test", connection.Request);
                Assert.DoesNotContain("Fork", connection.Request);
            }
            Assert.True(connection.Disposed);
            Assert.Equal(1, lookups);
        }

        [Theory]
        [InlineData("\r\n")]
        [InlineData("\n")]
        public async Task RadioAcceptsLegacyLinesAndRepeatedAdvisoryHeaders(string newline)
        {
            var transport = Transport(_ => new DuplexStream(string.Join(newline, new[] {
                "HTTP/1.1 200 OK", "Content-Type: audio/aacp", "icy-br: 128", "Icy-Br: 128",
                "Access-Control-Allow-Origin: *", "Access-Control-Allow-Origin: *", "Content-Length: 4", "", "LIVE" })));
            using (var response = await transport.OpenAsync("http://radio.test/live", false, CancellationToken.None))
            {
                Assert.Equal("audio/aacp", response.ContentType);
                Assert.Equal("LIVE", await Text(response.Body));
            }
        }

        [Theory]
        [InlineData("http://[2606:4700:4700::1111]/live", "[2606:4700:4700::1111]")]
        [InlineData("http://[2606:4700:4700::1111]:8080/live", "[2606:4700:4700::1111]:8080")]
        public async Task Ipv6HostHeadersUseBracketsAndPreservePorts(string url, string host)
        {
            var connection = Reply("audio/mpeg", "LIVE");
            using (var response = await Transport(_ => connection).OpenAsync(url, false, CancellationToken.None))
                Assert.Contains("\r\nHost: " + host + "\r\n", connection.Request);
        }

        [Theory]
        [InlineData("Content-Length: 4\r\ncontent-length: 4")]
        [InlineData("Transfer-Encoding: chunked\r\ntransfer-encoding: chunked")]
        [InlineData("Content-Encoding: identity\r\ncontent-encoding: identity")]
        [InlineData("Content-Type: audio/mpeg\r\ncontent-type: audio/mpeg")]
        [InlineData("Location: /one\r\nlocation: /two")]
        [InlineData("Content-Length : 4")]
        [InlineData("icy-name: bad\rvalue")]
        [InlineData("Transfer-Encoding: identity\r\nContent-Length: 4")]
        [InlineData("Transfer-Encoding: chunked\r\nContent-Length: 4")]
        [InlineData("Transfer-Encoding: identity, chunked")]
        [InlineData("Transfer-Encoding: gzip")]
        [InlineData("Content-Encoding: gzip")]
        public async Task AmbiguousOrUnsupportedFramingIsRejected(string headers)
        {
            var connection = new DuplexStream("HTTP/1.1 200 OK\r\n" + headers + "\r\n\r\nLIVE");
            var transport = Transport(_ => connection);
            await Assert.ThrowsAsync<InvalidDataException>(() => transport.OpenAsync("http://radio.test/live", false, CancellationToken.None));
            Assert.True(connection.Disposed);
        }

        [Fact]
        public async Task IdentityTransferIsCloseDelimitedButCatalogueAndChunksRemainStrict()
        {
            var transport = Transport(_ => new DuplexStream("HTTP/1.1 200 OK\nTransfer-Encoding: identity\n\nLIVE"));
            using (var response = await transport.OpenAsync("http://radio.test/live", false, CancellationToken.None))
                Assert.Equal("LIVE", await Text(response.Body));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                transport.OpenAsync("https://opml.radiotime.com/Tune.ashx", true, CancellationToken.None));
            transport = Transport(_ => new DuplexStream("HTTP/1.1 200 OK\nTransfer-Encoding: chunked\n\n4\nLIVE\n0\n\n"));
            using (var response = await transport.OpenAsync("http://radio.test/live", false, CancellationToken.None))
                await Assert.ThrowsAsync<InvalidDataException>(() => Text(response.Body));
        }

        [Fact]
        public async Task TruncationAndSizeLimitsAreErrors()
        {
            var transport = Transport(_ => new DuplexStream("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nshort"));
            using (var response = await transport.OpenAsync("http://radio.test/live", false, CancellationToken.None))
                await Assert.ThrowsAsync<EndOfStreamException>(() => Text(response.Body));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                TuneInRelay.ReadBoundedAsync(new MemoryStream(new byte[101]), 100, CancellationToken.None));
            transport = Transport(_ => new DuplexStream("HTTP/1.1 200 OK\r\nicy-name: " + new string('a', 8193) + "\r\n\r\n"));
            await Assert.ThrowsAsync<InvalidDataException>(() => transport.OpenAsync("http://radio.test/live", false, CancellationToken.None));
        }

        [Fact]
        public async Task PlsResolvesAlternativesWithWarningsAndNoLowercasing()
        {
            var requests = 0;
            var warnings = 0;
            var transport = Transport(uri =>
            {
                requests++;
                if (uri.Host == "opml.radiotime.com") return Reply("text/plain", "https://radio.test/playlist.pls");
                if (uri.AbsolutePath == "/playlist.pls") return Reply("audio/x-scpls",
                    "[playlist]\nFile1=https://radio.test/dead\nFile2=https://radio.test/CaseLive?Token=Ab_C");
                if (uri.AbsolutePath == "/dead") return new DuplexStream("HTTP/1.1 503 Unavailable\r\n\r\n");
                Assert.Equal("/CaseLive?Token=Ab_C", uri.PathAndQuery);
                return Reply("audio/mpeg", "LIVE");
            });
            using (var response = await new TuneInRelay(transport).OpenAsync("s1", CancellationToken.None, message =>
            {
                warnings++;
                Assert.DoesNotContain("https://", message);
            }))
                Assert.Equal("LIVE", await Text(response.Body));
            Assert.Equal(4, requests);
            Assert.Equal(1, warnings);
        }

        [Fact]
        public async Task PrivateAlternativeIsFatalNotPermissionToFallback()
        {
            var requests = 0;
            var transport = new SafeHttpTransport(host => Task.FromResult(new[] {
                host == "127.0.0.1" ? IPAddress.Loopback : IPAddress.Parse("8.8.8.8") }),
                (_, __, ___) =>
                {
                    requests++;
                    return Task.FromResult<Stream>(Reply("text/plain", "https://127.0.0.1/secret\nhttps://radio.test/live"));
                });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new TuneInRelay(transport).OpenAsync("s1", CancellationToken.None, _ => throw new Exception("No security fallback")));
            Assert.Equal(1, requests);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PreRollFailsExplicitlyInsteadOfSkippingOrConcatenating(bool nested)
        {
            var audioRequests = 0;
            var transport = Transport(uri =>
            {
                if (uri.Host == "opml.radiotime.com") return Reply("text/plain",
                    nested ? "https://radio.test/list.m3u" : PreRoll + "\nhttps://radio.test/live");
                if (uri.AbsolutePath == "/list.m3u") return Reply("audio/x-mpegurl", PreRoll + "\nhttps://radio.test/live");
                audioRequests++;
                return Reply("audio/mpeg", "AD");
            });
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                new TuneInRelay(transport).OpenAsync("s1", CancellationToken.None, _ => { }));
            Assert.Equal(0, audioRequests);
        }

        [Fact]
        public async Task RedirectToPreRollCannotEvadeSequenceGuard()
        {
            var transport = Transport(uri => uri.Host == "opml.radiotime.com"
                ? Reply("text/plain", "https://radio.test/redirect\nhttps://radio.test/live")
                : new DuplexStream("HTTP/1.1 302 Found\r\nLocation: " + PreRoll + "\r\n\r\n"));
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                new TuneInRelay(transport).OpenAsync("s1", CancellationToken.None, _ => { }));
        }

        [Theory]
        [InlineData("https://cdn-cms.tunein.com/service/Audio/nostream.enUS.mp3")]
        [InlineData("https://cdn-cms.tunein.com/service/Audio/notcompatible.mp3")]
        public void ProviderFaultUrlsAreErrorsEvenInsideHttp200(string url) =>
            Assert.Throws<InvalidDataException>(() => TuneInRelay.PlaylistUrls(url).ToList());

        [Fact]
        public async Task PlaylistSizeNestingAndAttemptBudgetsAreBounded()
        {
            var requests = 0;
            var relay = new TuneInRelay(Transport(uri =>
            {
                requests++;
                return Reply("audio/x-mpegurl", "https://radio.test/loop.m3u");
            }));
            await Assert.ThrowsAsync<InvalidDataException>(() => relay.OpenAsync("s1", CancellationToken.None, _ => { }));
            Assert.Equal(6, requests);
            relay = new TuneInRelay(Transport(_ => Reply("text/plain", new string('a', 256 * 1024 + 1))));
            await Assert.ThrowsAsync<InvalidDataException>(() => relay.OpenAsync("s1", CancellationToken.None, _ => { }));
            requests = 0;
            relay = new TuneInRelay(Transport(uri =>
            {
                requests++;
                if (uri.Host == "opml.radiotime.com") return Reply("text/plain", "https://radio.test/list.m3u");
                return Reply("audio/x-mpegurl", string.Join("\n", Enumerable.Repeat("https://radio.test/list.m3u", 8)));
            }));
            await Assert.ThrowsAsync<InvalidDataException>(() => relay.OpenAsync("s1", CancellationToken.None, _ => { }));
            Assert.Equal(17, requests);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RelayTransfersOwnershipOrClosesStreamOnFactoryFailure(bool factoryFails)
        {
            var audio = Reply("audio/aac", "LIVE");
            var relay = new TuneInRelay(Transport(uri => uri.Host == "opml.radiotime.com"
                ? Reply("text/plain", "https://radio.test/live") : audio));
            var factory = DispatchProxy.Create<IHttpResultFactory, FactoryProxy>();
            ((FactoryProxy)(object)factory).Fail = factoryFails;
            var service = new TuneInService(factory, DispatchProxy.Create<ILogManager, HostProxy>(), relay)
            { Request = Request(IPAddress.Loopback, true) };
            if (factoryFails)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.Get(new GetTuneInRelay { Id = "s1" }));
                Assert.True(audio.Disposed);
            }
            else
            {
                using (var stream = Assert.IsAssignableFrom<Stream>(await service.Get(new GetTuneInRelay { Id = "s1" })))
                {
                    Assert.False(audio.Disposed);
                    Assert.Equal("LIVE", await Text(stream));
                }
                Assert.True(audio.Disposed);
            }
        }

        [Theory]
        [InlineData("8.8.8.8", true)]
        [InlineData("127.0.0.1", false)]
        [InlineData("192.168.1.1", true)]
        public async Task RelayRequiresBothLoopbackAddressAndLocalSocket(string address, bool local)
        {
            var service = new TuneInService(DispatchProxy.Create<IHttpResultFactory, FactoryProxy>(),
                DispatchProxy.Create<ILogManager, HostProxy>(),
                new TuneInRelay(new SafeHttpTransport(_ => throw new Exception("Must not reach DNS"))))
            { Request = Request(IPAddress.Parse(address), local) };
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.Get(new GetTuneInRelay { Id = "s1" }));
        }

        [Fact]
        public async Task CancellationAfterOpeningStillClosesUpstreamAndCancelsReads()
        {
            var audio = Reply("audio/mpeg", "LIVE");
            using (var cancellation = new CancellationTokenSource())
            using (var response = await new TuneInRelay(Transport(uri => uri.Host == "opml.radiotime.com"
                ? Reply("text/plain", "http://radio.test/live") : audio)).OpenAsync("s1", cancellation.Token, _ => { }))
            {
                cancellation.Cancel();
                Assert.True(audio.Disposed);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Text(response.Body));
            }
        }

        [Fact]
        public async Task PrecancelledRequestsDoNotReachDns()
        {
            var relay = new TuneInRelay(new SafeHttpTransport(_ => throw new Exception("Must not reach DNS")));
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.OpenAsync("s1", cancellation.Token, _ => { }));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CancellingBlockedBodyReadClosesEvenStreamsThatIgnoreReadCancellation(bool whileOpening)
        {
            var blocked = new BlockingBodyStream();
            var relay = new TuneInRelay(Transport(uri => whileOpening || uri.Host != "opml.radiotime.com"
                ? (Stream)blocked : Reply("text/plain", "https://radio.test/live")));
            using (var cancellation = new CancellationTokenSource())
            {
                if (whileOpening)
                {
                    var opening = relay.OpenAsync("s1", cancellation.Token, _ => { });
                    await blocked.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
                }
                else
                {
                    using (var response = await relay.OpenAsync("s1", CancellationToken.None, _ => { }))
                    {
                        var read = response.Body.ReadAsync(new byte[1], 0, 1, cancellation.Token);
                        await blocked.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        cancellation.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
                    }
                }
                Assert.True(blocked.Disposed);
            }
        }

        [Theory]
        [InlineData("stream_http://evil.test/Tune.ashx?id=s1")]
        [InlineData("stream_http://opml.radiotime.com/Other.ashx?id=s1")]
        [InlineData("stream_http://user@opml.radiotime.com/Tune.ashx?id=s1")]
        [InlineData("stream_http://opml.radiotime.com:8443/Tune.ashx?id=s1")]
        [InlineData("stream_http://opml.radiotime.com/Tune.ashx?id=s1#fragment")]
        [InlineData("stream_http://opml.radiotime.com/Tune.ashx?id=s1&id=s2")]
        [InlineData("stream_http://opml.radiotime.com/Tune.ashx?id=s1%0a")]
        public void LegacyPlaybackIdsCannotSupplyArbitraryEndpoints(string value) =>
            Assert.ThrowsAny<Exception>(() => TuneInRelay.StationId(value));

        [Fact]
        public void PreparedMediaSourceUsesOnlyLoopbackTranscodingWithoutGuessedCodec()
        {
            var source = TuneInRelay.CreateMediaSource(DispatchProxy.Create<IServerApplicationHost, HostProxy>(),
                "stream_http://opml.radiotime.com/Tune.ashx?token=Ab_C&amp;id=s123");
            Assert.Equal("http://127.0.0.1:8096/emby/TuneIn/Relay/s123", source.Path);
            Assert.False(source.SupportsDirectPlay);
            Assert.False(source.SupportsDirectStream);
            Assert.True(source.SupportsTranscoding);
            Assert.Empty(source.MediaStreams);
            Assert.Null(source.Container);
        }

        [Theory]
        [InlineData("4.6.0.50", false)]
        [InlineData("4.9.1.80", false)]
        [InlineData("4.9.1.81", true)]
        [InlineData("4.9.1.90", true)]
        [InlineData("4.9.2.0", false)]
        [InlineData("4.9.2.6", false)]
        [InlineData("4.9.2.7", true)]
        [InlineData("4.9.3.0", true)]
        public void HostVersionGateRejectsAffectedStableAndBetaServers(string version, bool expected) =>
            Assert.Equal(expected, Plugin.SupportsServerVersion(Version.Parse(version)));

        private static SafeHttpTransport Transport(Func<Uri, Stream> response) => new SafeHttpTransport(
            _ => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
            (_, uri, __) => Task.FromResult(response(uri)));

        private static DuplexStream Reply(string type, string body) => new DuplexStream(
            "HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nContent-Length: "
            + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);

        private static async Task<string> Text(Stream stream) =>
            Encoding.UTF8.GetString(await TuneInRelay.ReadBoundedAsync(stream, 1000, CancellationToken.None));

        private static IRequest Request(IPAddress address, bool local)
        {
            var request = DispatchProxy.Create<IRequest, HostProxy>();
            var proxy = (HostProxy)(object)request;
            proxy.Address = address;
            proxy.Local = local;
            return request;
        }

        public class HostProxy : DispatchProxy
        {
            public IPAddress Address { get; set; }
            public bool Local { get; set; }

            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "GetLogger") return null;
                if (method.Name == "get_RemoteIp") return Address;
                if (method.Name == "get_IsSocketConnectionLocal") return Local;
                if (method.Name == "get_CancellationToken") return CancellationToken.None;
                if (method.Name == "GetLocalApiUrl")
                {
                    Assert.Equal(IPAddress.Loopback, args[0]);
                    return "http://127.0.0.1:8096/emby";
                }
                throw new InvalidOperationException("Unexpected dependency: " + method.Name);
            }
        }

        public class FactoryProxy : DispatchProxy
        {
            public bool Fail { get; set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                Assert.Equal("GetResult", method.Name);
                Assert.Equal("audio/aac", args[2]);
                if (Fail) throw new InvalidOperationException("Synthetic result failure.");
                return args[1];
            }
        }

        private sealed class DuplexStream : MemoryStream
        {
            private readonly MemoryStream output = new MemoryStream();
            public bool Disposed { get; private set; }
            public DuplexStream(string response) : base(Encoding.UTF8.GetBytes(response)) { }
            public string Request => Encoding.ASCII.GetString(output.ToArray());
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                output.WriteAsync(buffer, offset, count, token);
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Disposed = true; output.Dispose(); }
                base.Dispose(disposing);
            }

        }

        private sealed class BlockingBodyStream : Stream
        {
            private readonly MemoryStream headers = new MemoryStream(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\n\r\n"));
            private readonly TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Reading { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Disposed { get; private set; }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (headers.Position < headers.Length) return headers.Read(buffer, offset, count);
                Reading.TrySetResult(true);
                await closed.Task;
                throw new ObjectDisposedException(nameof(BlockingBodyStream));
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Disposed = true; closed.TrySetResult(true); }
                base.Dispose(disposing);
            }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) { }
        }
    }
}

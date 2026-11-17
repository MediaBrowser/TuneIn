using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static MediaBrowser.Plugins.TuneIn.Tests.TransportTests;

namespace MediaBrowser.Plugins.TuneIn.Tests
{
    public class PlaybackTests
    {
        private const string PreRoll = "https://tunein-ondemand.cdnstream1.com/multi_bump_sonic_pre_pre.mp3";
        private const string Live = "https://radio.test/live";

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RequiredAdThenOneLiveAlternativeProducesOneWaveStream(bool nested)
        {
            var opened = new List<string>();
            var decoded = new List<string>();
            var relay = new TuneInRelay(Transport(uri =>
            {
                if (uri.Host == "opml.radiotime.com")
                    return Reply("text/plain", nested ? "https://radio.test/list.pls" : PreRoll + "\n" + Live + "\nhttps://radio.test/unused");
                if (uri.AbsolutePath == "/list.pls")
                    return Reply("audio/x-scpls", "[playlist]\nFile1=" + PreRoll + "\nFile2=" + Live + "\nFile3=https://radio.test/unused");
                opened.Add(uri.AbsolutePath);
                return Reply(uri.Host == "radio.test" ? "audio/aac" : "audio/mpeg", uri.Host == "radio.test" ? "LIVE" : "AD");
            }));
            // Probing is a separate open and must see WAV, never the ad's MP3 metadata.
            using (var probe = await relay.OpenAsync("s269846", CancellationToken.None, _ => { }, Own))
            {
                Assert.Equal("audio/wav", probe.ContentType);
                Assert.Equal("RIFF", Encoding.ASCII.GetString(await ReadHeader(probe.Body), 0, 4));
            }
            Assert.Single(opened);
            opened.Clear();
            using (var response = await relay.OpenAsync("s269846", CancellationToken.None, _ => { }, (source, _) =>
            {
                decoded.Add(source.ContentType);
                return new OwnedStream(source);
            }))
            {
                Assert.Equal("audio/wav", response.ContentType);
                var bytes = await Bytes(response.Body);
                Assert.Equal("ADLIVE", Encoding.ASCII.GetString(bytes, 44, bytes.Length - 44));
                Assert.Equal(48000, BitConverter.ToInt32(bytes, 24));
                Assert.Equal(2, BitConverter.ToInt16(bytes, 22));
                Assert.Equal(16, BitConverter.ToInt16(bytes, 34));
            }
            Assert.Equal(new[] { "audio/mpeg", "audio/aac" }, decoded);
            Assert.Equal(new[] { "/multi_bump_sonic_pre_pre.mp3", "/live" }, opened);
        }

        [Fact]
        public async Task KexpStyleNoAdRemainsCompressedAndFiniteLiveIsNotAnAd()
        {
            var opened = 0;
            var relay = Relay(Live + "\nhttps://radio.test/unused", _ =>
            {
                opened++;
                return Reply("audio/aac", "AAC");
            });
            using (var response = await relay.OpenAsync("s32537", CancellationToken.None, _ => { },
                (_, __) => throw new Exception("No decoder for an ordinary live source.")))
            {
                Assert.Equal("audio/aac", response.ContentType);
                Assert.Equal("AAC", Encoding.ASCII.GetString(await Bytes(response.Body)));
            }
            Assert.Equal(1, opened);
        }

        [Fact]
        public async Task LiveOpenFailureAfterAdUsesFallbackWithoutRepeatingAd()
        {
            var opened = new List<string>();
            var warnings = new List<string>();
            var relay = Relay(PreRoll + "\nhttps://radio.test/dead\nhttps://radio.test/list.m3u", uri =>
            {
                opened.Add(uri.AbsolutePath);
                if (uri.AbsolutePath == "/dead") return new DuplexStream("HTTP/1.1 503 Unavailable\r\n\r\n");
                if (uri.AbsolutePath == "/list.m3u") return Reply("audio/x-mpegurl", Live);
                return Reply("audio/mpeg", uri.Host == "radio.test" ? "LIVE" : "AD");
            });
            using (var response = await relay.OpenAsync("s135097", CancellationToken.None, warnings.Add, Own))
                Assert.Equal("ADLIVE", Encoding.ASCII.GetString((await Bytes(response.Body)).Skip(44).ToArray()));
            Assert.Equal(new[] { "/multi_bump_sonic_pre_pre.mp3", "/dead", "/list.m3u", "/live" }, opened);
            Assert.Single(warnings);
            Assert.DoesNotContain("https://", warnings[0]);
        }

        [Fact]
        public async Task AllLeadingRequiredAdsFinishBeforeSelectingOneLiveStream()
        {
            var opened = 0;
            var relay = Relay(PreRoll + "\n" + PreRoll + "\n" + Live + "\nhttps://radio.test/unused", uri =>
            {
                opened++;
                return Reply("audio/mpeg", uri.Host == "radio.test" ? "LIVE" : "AD" + opened);
            });
            using (var response = await relay.OpenAsync("s1", CancellationToken.None, _ => { }, Own))
                Assert.Equal("AD1AD2LIVE", Encoding.ASCII.GetString((await Bytes(response.Body)).Skip(44).ToArray()));
            Assert.Equal(3, opened);
        }

        [Fact]
        public async Task InvalidNestedRequiredSequenceCannotFallBackAndSkipItsAd()
        {
            var opened = 0;
            var relay = Relay("https://radio.test/list.pls\n" + Live, uri =>
            {
                opened++;
                return Reply("audio/x-scpls", "[playlist]\nFile1=" + PreRoll
                    + "\nFile2=https://cdn-cms.tunein.com/service/Audio/nostream.enUS.mp3");
            });
            await Assert.ThrowsAsync<InvalidDataException>(() => relay.OpenAsync("s1", CancellationToken.None,
                _ => throw new Exception("Required sequences cannot be skipped."), Own));
            Assert.Equal(1, opened);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AdOrLiveReadFailureIsTerminalNotAFallback(bool liveFails)
        {
            var opened = new List<DuplexStream>();
            var relay = Relay(PreRoll + "\n" + Live + "\nhttps://radio.test/unused", uri =>
            {
                var fail = liveFails == (uri.Host == "radio.test");
                var stream = fail
                    ? new DuplexStream("HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nContent-Length: 100\r\n\r\nshort")
                    : Reply("audio/mpeg", "AD");
                opened.Add(stream);
                return stream;
            });
            using (var response = await relay.OpenAsync("s1", CancellationToken.None, _ => { }, Own))
            {
                await Assert.ThrowsAsync<EndOfStreamException>(() => Bytes(response.Body));
                await Assert.ThrowsAsync<EndOfStreamException>(() => response.Body.ReadAsync(new byte[1], 0, 1));
            }
            Assert.Equal(liveFails ? 2 : 1, opened.Count);
            Assert.All(opened, stream => Assert.True(stream.Disposed));
        }

        [Fact]
        public async Task FailedRequiredAdOpeningOrDecoderStartupNeverOpensLive()
        {
            foreach (var startupFails in new[] { false, true })
            {
                var opened = new List<DuplexStream>();
                var relay = Relay(PreRoll + "\n" + Live, _ =>
                {
                    var stream = startupFails ? Reply("audio/mpeg", "AD")
                        : new DuplexStream("HTTP/1.1 503 Unavailable\r\n\r\n");
                    opened.Add(stream);
                    return stream;
                });
                await Assert.ThrowsAnyAsync<Exception>(() => relay.OpenAsync("s1", CancellationToken.None,
                    _ => throw new Exception("Required ad failure must not warn and skip."),
                    (_, __) => throw new InvalidOperationException("Decoder unavailable.")));
                Assert.Single(opened);
                Assert.True(opened[0].Disposed);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CancellationAndProbeDisposalReleaseAdWithoutOpeningLive(bool cancelRead)
        {
            var opened = new List<DuplexStream>();
            var relay = Relay(PreRoll + "\n" + Live, _ =>
            {
                var stream = Reply("audio/mpeg", "AD");
                opened.Add(stream);
                return stream;
            });
            using (var response = await relay.OpenAsync("s1", CancellationToken.None, _ => { }, Own))
            using (var caller = new CancellationTokenSource())
            {
                if (cancelRead)
                {
                    caller.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                        response.Body.ReadAsync(new byte[1], 0, 1, caller.Token));
                }
                else await ReadHeader(response.Body);
            }
            Assert.Single(opened);
            Assert.True(opened[0].Disposed);
        }

        [Fact]
        public async Task RequestCancellationAfterLiveOpenedStillReachesTheDeferredStage()
        {
            var opened = new List<DuplexStream>();
            var relay = Relay(PreRoll + "\n" + Live, _ =>
            {
                var stream = Reply("audio/mpeg", "A");
                opened.Add(stream);
                return stream;
            });
            using (var caller = new CancellationTokenSource())
            using (var response = await relay.OpenAsync("s1", caller.Token, _ => { }, Own))
            {
                await ReadHeader(response.Body);
                Assert.Equal(1, await response.Body.ReadAsync(new byte[1], 0, 1));
                Assert.Equal(1, await response.Body.ReadAsync(new byte[1], 0, 1));
                Assert.Equal(2, opened.Count);
                caller.Cancel();
                Assert.All(opened, stream => Assert.True(stream.Disposed));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bytes(response.Body));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MisplacedOrMissingLiveSequenceFailsBeforeAudio(bool misplaced)
        {
            var relay = Relay(misplaced ? Live + "\n" + PreRoll : PreRoll,
                _ => throw new Exception("Invalid sequences must not open audio."));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                relay.OpenAsync("s1", CancellationToken.None, _ => { }, Own));
        }

        [Theory]
        [InlineData("https://tunein-ondemand.cdnstream1.com.evil.test/multi_bump_sonic_pre_pre.mp3")]
        [InlineData("https://tunein-ondemand.cdnstream1.com/other.mp3")]
        [InlineData("http://tunein-ondemand.cdnstream1.com/multi_bump_sonic_pre_pre.mp3")]
        [InlineData("https://tunein-ondemand.cdnstream1.com:444/multi_bump_sonic_pre_pre.mp3")]
        public void OnlyTheRecognizedProviderAssetIsAnAd(string url) =>
            Assert.False(TuneInRelay.IsProviderPreRoll(url));

        [Fact]
        public async Task PrivateLiveAfterAdStillFailsClosedWithoutFallback()
        {
            var opened = 0;
            var transport = new SafeHttpTransport(host => Task.FromResult(new[] {
                host == "radio.test" ? IPAddress.Loopback : IPAddress.Parse("8.8.8.8") }),
                (_, uri, __) =>
                {
                    opened++;
                    return Task.FromResult<Stream>(uri.Host == "opml.radiotime.com"
                        ? Reply("text/plain", PreRoll + "\n" + Live + "\nhttps://other.test/live")
                        : Reply("audio/mpeg", "AD"));
                });
            using (var response = await new TuneInRelay(transport).OpenAsync("s1", CancellationToken.None,
                _ => throw new Exception("No security fallback."), Own))
                await Assert.ThrowsAsync<InvalidOperationException>(() => Bytes(response.Body));
            Assert.Equal(2, opened);
        }

        [Fact]
        public async Task ChannelCallbackExposesOnlyProtectedLoopbackSource()
        {
            var channel = new TuneInChannel(null, DispatchProxy.Create<ILogManager, HostProxy>(),
                DispatchProxy.Create<IServerApplicationHost, HostProxy>());
            var source = Assert.Single(await channel.GetChannelItemMediaInfo(
                "stream_http://opml.radiotime.com/Tune.ashx?id=s32537", CancellationToken.None));
            Assert.Equal("http://127.0.0.1:8096/emby/TuneIn/Relay/s32537", source.Path);
            Assert.Empty(source.MediaStreams);
            Assert.Null(source.Container);
            Assert.False(source.SupportsDirectPlay);
            Assert.False(source.SupportsDirectStream);
            Assert.True(source.SupportsTranscoding);
        }

        [FfmpegFact]
        public async Task RealMixedCodecAdAndLiveProduceOneProbeablePcmStream()
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            var mp3 = await Encode(encoder, "-f lavfi -i sine=frequency=440:duration=0.2 -ar 22050 -ac 1 -f mp3 pipe:1");
            var aac = await Encode(encoder, "-f lavfi -i sine=frequency=880:duration=0.2 -ar 48000 -ac 2 -f adts pipe:1");
            var relay = Relay(PreRoll + "\n" + Live, uri =>
                BinaryReply(uri.Host == "radio.test" ? "audio/aac" : "audio/mpeg", uri.Host == "radio.test" ? aac : mp3));
            Stream Decode(SafeResponse response, CancellationToken token) => new PcmDecoderStream(encoder, response, token);
            using (var probe = await relay.OpenAsync("s1", CancellationToken.None, _ => { }, Decode))
            {
                Assert.Equal("audio/wav", probe.ContentType);
                Assert.Equal("WAVE", Encoding.ASCII.GetString(await ReadHeader(probe.Body), 8, 4));
            }
            byte[] wave;
            using (var playback = await relay.OpenAsync("s1", CancellationToken.None, _ => { }, Decode))
                wave = await Bytes(playback.Body);
            byte[] adPcm;
            byte[] livePcm;
            using (var ad = Decode(new SafeResponse(new MemoryStream(mp3), "audio/mpeg"), CancellationToken.None))
                adPcm = await Bytes(ad);
            using (var live = Decode(new SafeResponse(new MemoryStream(aac), "audio/aac"), CancellationToken.None))
                livePcm = await Bytes(live);
            Assert.True(adPcm.Length > 0);
            Assert.True(livePcm.Length > 0);
            Assert.Equal(adPcm.Concat(livePcm), wave.Skip(44));
            Assert.Equal(0, (wave.Length - 44) % 4);
            using (var reprobe = Decode(new SafeResponse(new MemoryStream(wave), "audio/wav"), CancellationToken.None))
                Assert.Equal(wave.Skip(44), await Bytes(reprobe));
        }

        [FfmpegFact]
        public async Task NestedLiveSequenceRetainsBothRequiredAdsAndOneLiveInput()
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            var mp3 = await Encode(encoder, "-f lavfi -i sine=frequency=440:duration=0.1 -f mp3 pipe:1");
            var aac = await Encode(encoder, "-f lavfi -i sine=frequency=880:duration=0.1 -f adts pipe:1");
            var opened = new List<string>();
            var relay = Relay(PreRoll + "\nhttps://radio.test/list.m3u", uri =>
            {
                opened.Add(uri.AbsolutePath);
                if (uri.AbsolutePath == "/list.m3u") return Reply("audio/x-mpegurl", PreRoll + "\n" + Live);
                return BinaryReply(uri.Host == "radio.test" ? "audio/aac" : "audio/mpeg", uri.Host == "radio.test" ? aac : mp3);
            });
            Stream Decode(SafeResponse response, CancellationToken token) => new PcmDecoderStream(encoder, response, token);
            byte[] wave;
            using (var response = await relay.OpenAsync("s1", CancellationToken.None, _ => { }, Decode))
                wave = await Bytes(response.Body);
            byte[] ad;
            byte[] live;
            using (var decoder = Decode(new SafeResponse(new MemoryStream(mp3), "audio/mpeg"), CancellationToken.None))
                ad = await Bytes(decoder);
            using (var decoder = Decode(new SafeResponse(new MemoryStream(aac), "audio/aac"), CancellationToken.None))
                live = await Bytes(decoder);
            Assert.Equal(ad.Concat(ad).Concat(live), wave.Skip(44));
            Assert.Equal(new[] { "/multi_bump_sonic_pre_pre.mp3", "/list.m3u", "/multi_bump_sonic_pre_pre.mp3", "/live" }, opened);
        }

        [FfmpegFact]
        public async Task RealDecoderErrorsAndBlockedInputCancellationReleaseResources()
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            using (var response = new PcmDecoderStream(encoder,
                new SafeResponse(new MemoryStream(new byte[128]), "audio/mpeg"), CancellationToken.None))
                await Assert.ThrowsAsync<InvalidDataException>(() => Bytes(response));
            var blocked = new WaitingStream();
            using (var caller = new CancellationTokenSource())
            using (var decoder = new PcmDecoderStream(encoder, new SafeResponse(blocked, "audio/aac"), caller.Token))
            {
                var reading = decoder.ReadAsync(new byte[1024], 0, 1024);
                await blocked.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            Assert.True(blocked.Disposed);
        }

        [FfmpegFact]
        public async Task ServiceUsesHostDecoderAndTransfersOwnershipOrCleansUpFactoryFailure()
        {
            var encoder = Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
            var mp3 = await Encode(encoder, "-f lavfi -i sine=duration=0.1 -f mp3 pipe:1");
            foreach (var fail in new[] { false, true })
            {
                var opened = new List<DuplexStream>();
                var relay = Relay(PreRoll + "\n" + Live, _ =>
                {
                    var stream = BinaryReply("audio/mpeg", mp3);
                    opened.Add(stream);
                    return stream;
                });
                var factory = DispatchProxy.Create<IHttpResultFactory, WaveFactoryProxy>();
                ((WaveFactoryProxy)(object)factory).Fail = fail;
                var request = DispatchProxy.Create<IRequest, HostProxy>();
                ((HostProxy)(object)request).Address = IPAddress.Loopback;
                ((HostProxy)(object)request).Local = true;
                var service = new TuneInService(factory, DispatchProxy.Create<ILogManager, HostProxy>(), relay,
                    DispatchProxy.Create<IFfmpegManager, EncoderProxy>()) { Request = request };
                if (fail)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => service.Get(new GetTuneInRelay { Id = "s1" }));
                else
                    using (var body = Assert.IsAssignableFrom<Stream>(await service.Get(new GetTuneInRelay { Id = "s1" })))
                        Assert.True((await Bytes(body)).Length > 44);
                Assert.All(opened, stream => Assert.True(stream.Disposed));
                Assert.Equal(fail ? 1 : 2, opened.Count);
            }
        }

        public class EncoderProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                switch (method.Name)
                {
                    case "get_FfmpegConfiguration": return DispatchProxy.Create<IFfmpegConfiguration, EncoderProxy>();
                    case "get_EncoderPath": return Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG");
                    case "get_EnvironmentVariables": return null;
                    default: throw new InvalidOperationException("Unexpected encoder dependency: " + method.Name);
                }
            }
        }

        public class WaveFactoryProxy : DispatchProxy
        {
            public bool Fail { get; set; }
            protected override object Invoke(MethodInfo method, object[] args)
            {
                Assert.Equal("GetResult", method.Name);
                Assert.Equal("audio/wav", args[2]);
                if (Fail) throw new InvalidOperationException("Synthetic result failure.");
                return args[1];
            }
        }

        private static TuneInRelay Relay(string metadata, Func<Uri, Stream> audio) =>
            new TuneInRelay(Transport(uri => uri.Host == "opml.radiotime.com" ? Reply("text/plain", metadata) : audio(uri)));

        private static Stream Own(SafeResponse response, CancellationToken _) => new OwnedStream(response);

        private static Task<byte[]> Bytes(Stream stream) =>
            TuneInRelay.ReadBoundedAsync(stream, 1024 * 1024, CancellationToken.None);

        private static async Task<byte[]> ReadHeader(Stream stream)
        {
            var buffer = new byte[44];
            await stream.ReadExactlyAsync(buffer);
            return buffer;
        }

        private static DuplexStream BinaryReply(string contentType, byte[] bytes) => new DuplexStream(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + contentType
                + "\r\nContent-Length: " + bytes.Length + "\r\n\r\n").Concat(bytes).ToArray());

        private static async Task<byte[]> Encode(string encoder, string arguments)
        {
            using (var process = Process.Start(new ProcessStartInfo(encoder, "-hide_banner -loglevel error " + arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            }))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            using (timeout.Token.Register(() => { if (!process.HasExited) process.Kill(); }))
            {
                var error = process.StandardError.ReadToEndAsync();
                var output = await TuneInRelay.ReadBoundedAsync(process.StandardOutput.BaseStream, 1024 * 1024, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await error);
                return output;
            }
        }

        private sealed class OwnedStream : RadioReadStream
        {
            private readonly SafeResponse response;
            internal OwnedStream(SafeResponse response) { this.response = response; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                response.Body.ReadAsync(buffer, offset, count, token);
            protected override void Dispose(bool disposing)
            {
                if (disposing) response.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class WaitingStream : RadioReadStream
        {
            private readonly TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Reading { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Disposed { get; private set; }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                Reading.TrySetResult(true);
                await closed.Task;
                throw new ObjectDisposedException(nameof(WaitingStream));
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Disposed = true; closed.TrySetResult(true); }
                base.Dispose(disposing);
            }
        }
    }

    public sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TUNEIN_TEST_FFMPEG")))
                Skip = "Set TUNEIN_TEST_FFMPEG to run real mixed-codec/process integration checks.";
        }
    }
}

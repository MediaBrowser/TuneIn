using MediaBrowser.Plugins.TuneIn.Security;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace MediaBrowser.Plugins.TuneIn
{
    internal sealed class TuneInRelay
    {
        private readonly SafeHttpTransport transport;

        internal TuneInRelay(SafeHttpTransport transport = null)
        {
            this.transport = transport ?? new SafeHttpTransport();
        }

        internal static MediaSourceInfo CreateMediaSource(IServerApplicationHost host, string itemId)
        {
            var station = StationId(itemId);
            var local = host.GetLocalApiUrl(System.Net.IPAddress.Loopback);
            return new MediaSourceInfo
            {
                Id = "tunein-relay-" + station,
                Name = "TuneIn",
                Path = local.TrimEnd('/') + "/TuneIn/Relay/" + station,
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                MediaStreams = new List<MediaStream>(),
                SupportsDirectPlay = false,
                SupportsDirectStream = false,
                SupportsTranscoding = true
            };
        }

        internal static string StationId(string itemId)
        {
            if (itemId == null || !itemId.StartsWith("stream_", StringComparison.Ordinal))
                throw new ArgumentException("Invalid TuneIn playback item.");
            var uri = CatalogueUri(HttpUtility.HtmlDecode(itemId.Substring("stream_".Length)));
            if (uri.AbsolutePath != "/Tune.ashx")
                throw new ArgumentException("Invalid TuneIn playback endpoint.");
            var id = HttpUtility.ParseQueryString(uri.Query)["id"];
            ValidateStation(id);
            return id;
        }

        internal static void ValidateStation(string id)
        {
            if (id == null || !Regex.IsMatch(id, @"^[spt][1-9][0-9]{0,9}\z"))
                throw new ArgumentException("Invalid TuneIn station.");
        }

        internal static Uri CatalogueUri(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException("Untrusted TuneIn catalogue destination.");
            return PublicAddressPolicy.ValidateUri(new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri, true);
        }

        internal async Task<SafeResponse> OpenAsync(string stationId, CancellationToken token, Action<string> warning,
            Func<SafeResponse, CancellationToken, Stream> decode = null)
        {
            ValidateStation(stationId);
            // This source owns the connection until the relay body is disposed, not just until headers arrive.
            var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            operation.CancelAfter(TimeSpan.FromSeconds(30));
            SafeResponse audio = null;
            try
            {
                using (var response = await transport.OpenAsync(
                    "https://opml.radiotime.com/Tune.ashx?id=" + stationId + "&formats=mp3,aac&partnerid=uD1X52pA",
                    true, operation.Token).ConfigureAwait(false))
                {
                    var text = Encoding.UTF8.GetString(await ReadBoundedAsync(response.Body, 256 * 1024, operation.Token).ConfigureAwait(false));
                    var urls = PlaylistUrls(text).Take(9).ToList();
                    if (urls.Count == 0 || urls.Count > 8)
                        throw new InvalidDataException("TuneIn returned an empty or oversized stream list.");
                    audio = await OpenListAsync(urls, operation.Token, warning, decode, 0, new ResolutionBudget()).ConfigureAwait(false);
                }
                operation.Token.ThrowIfCancellationRequested();
                operation.CancelAfter(Timeout.InfiniteTimeSpan);
                var result = new SafeResponse(new RelayBodyStream(audio, operation), audio.ContentType);
                audio = null;
                return result;
            }
            catch
            {
                audio?.Dispose();
                operation.Cancel();
                operation.Dispose();
                throw;
            }
        }

        private async Task<SafeResponse> OpenListAsync(List<string> urls, CancellationToken token, Action<string> warning,
            Func<SafeResponse, CancellationToken, Stream> decode, int depth, ResolutionBudget budget)
        {
            var preRolls = urls.TakeWhile(IsProviderPreRoll).ToList();
            if (urls.Skip(preRolls.Count).Any(IsProviderPreRoll) || preRolls.Count == urls.Count)
                throw new InvalidOperationException("TuneIn returned an invalid required pre-roll/live order.");
            if (preRolls.Count == 0)
                return await OpenAlternativesAsync(urls, token, warning, decode, depth, budget).ConfigureAwait(false);
            if (decode == null)
                throw new InvalidOperationException("TuneIn pre-roll playback requires the host audio decoder.");

            var stages = new Queue<Func<CancellationToken, Task<SafeResponse>>>();
            foreach (var url in preRolls)
                stages.Enqueue(ct => OpenStageAsync(async opening =>
                {
                    budget.Consume();
                    var ad = await transport.OpenAsync(url, false, opening).ConfigureAwait(false);
                    if (!IsAudio(ad.ContentType))
                    {
                        ad.Dispose();
                        throw new InvalidDataException("Unsupported required TuneIn pre-roll content.");
                    }
                    return ad;
                }, ct));
            // Only recognized leading ads are sequential; select exactly ONE live alternative.
            stages.Enqueue(ct => OpenStageAsync(opening =>
                OpenAlternativesAsync(urls.Skip(preRolls.Count), opening, warning, decode, depth, budget), ct));
            var first = await stages.Dequeue()(token).ConfigureAwait(false);
            return new SafeResponse(new PcmSequenceStream(first, stages, decode, token), "audio/wav");
        }

        private static async Task<SafeResponse> OpenStageAsync(Func<CancellationToken, Task<SafeResponse>> open, CancellationToken token)
        {
            var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            operation.CancelAfter(TimeSpan.FromSeconds(30));
            SafeResponse response = null;
            try
            {
                response = await open(operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                operation.CancelAfter(Timeout.InfiniteTimeSpan);
                return new SafeResponse(new RelayBodyStream(response, operation), response.ContentType);
            }
            catch { response?.Dispose(); operation.Cancel(); operation.Dispose(); throw; }
        }

        private sealed class ResolutionBudget
        {
            private int attempts;
            internal bool Available => attempts < 16;
            internal void Consume()
            {
                if (++attempts > 16) throw new InvalidOperationException("TuneIn stream-resolution attempt limit exceeded.");
            }
        }

        private static bool IsAudio(string contentType) =>
            contentType == "audio/mpeg" || contentType == "audio/mp3" || contentType == "audio/aac"
            || contentType == "audio/aacp" || contentType == "application/octet-stream";

        private async Task<SafeResponse> OpenAlternativesAsync(IEnumerable<string> urls, CancellationToken token, Action<string> warning,
            Func<SafeResponse, CancellationToken, Stream> decode, int depth, ResolutionBudget budget)
        {
            var pending = new Queue<KeyValuePair<string, int>>(urls.Select(url => new KeyValuePair<string, int>(url, depth)));
            Exception lastFailure = null;
            while (budget.Available && pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var candidate = pending.Dequeue();
                SafeResponse response = null;
                List<string> sequence = null;
                var required = false;
                try
                {
                    budget.Consume();
                    response = await transport.OpenAsync(candidate.Key, false, token,
                        validateDestination: uri => RejectPreRoll(new[] { uri.AbsoluteUri })).ConfigureAwait(false);
                    var extension = Path.GetExtension(new Uri(candidate.Key).AbsolutePath);
                    if (extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".m3u", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".pls", StringComparison.OrdinalIgnoreCase)
                        || response.ContentType.IndexOf("mpegurl", StringComparison.OrdinalIgnoreCase) >= 0
                        || response.ContentType.IndexOf("scpls", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (candidate.Value >= 4) throw new InvalidDataException("Radio playlist nesting limit exceeded.");
                        var text = Encoding.UTF8.GetString(await ReadBoundedAsync(response.Body, 256 * 1024, token).ConfigureAwait(false));
                        if (text.Contains("#EXT-X-")) throw new InvalidDataException("HLS is not supported by the TuneIn relay.");
                        var children = new List<string>();
                        foreach (var url in PlaylistUrls(text).Take(9))
                        {
                            required |= IsProviderPreRoll(url);
                            children.Add(url);
                        }
                        if (children.Count == 0 || children.Count > 8)
                            throw new InvalidDataException("Empty or oversized radio playlist.");
                        if (required)
                            sequence = children;
                        else
                            pending = new Queue<KeyValuePair<string, int>>(children.Select(url =>
                                new KeyValuePair<string, int>(url, candidate.Value + 1)).Concat(pending).Take(16));
                    }
                    else
                    {
                        if (!IsAudio(response.ContentType))
                            throw new InvalidDataException("Unsupported radio content type.");
                        var playable = response;
                        response = null;
                        return playable;
                    }
                }
                catch (Exception error) when (!required && (error is IOException || error is InvalidDataException || error is SocketException
                    || error is AuthenticationException || error is TimeoutException))
                {
                    lastFailure = error;
                    warning("Advertised radio alternative failed; trying the next available alternative.");
                }
                finally { response?.Dispose(); }
                // Required ads must never become a recoverable failed live alternative.
                if (sequence != null)
                    return await OpenListAsync(sequence, token, warning, decode, candidate.Value + 1, budget).ConfigureAwait(false);
            }
            throw new InvalidDataException("No radio stream could be opened within the relay limits.", lastFailure);
        }

        internal static bool IsProviderPreRoll(string value)
        {
            var uri = new Uri(value);
            return uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
                && uri.Host.Equals("tunein-ondemand.cdnstream1.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath == "/multi_bump_sonic_pre_pre.mp3";
        }

        private static void RejectPreRoll(IEnumerable<string> urls)
        {
            if (urls.Any(IsProviderPreRoll))
                throw new NotSupportedException("A redirect to a required TuneIn pre-roll lacks an explicit pre-roll/live sequence.");
        }

        internal static IEnumerable<string> PlaylistUrls(string text)
        {
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var item = line.Trim();
                if (item.StartsWith("File", StringComparison.OrdinalIgnoreCase))
                {
                    var equals = item.IndexOf('=');
                    if (equals < 0 || !Regex.IsMatch(item.Substring(0, equals), @"^File[1-9][0-9]*$", RegexOptions.IgnoreCase)) continue;
                    item = item.Substring(equals + 1).Trim();
                }
                if (!item.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    && !item.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                if (item.IndexOf("/notcompatible.", StringComparison.OrdinalIgnoreCase) >= 0
                    || item.IndexOf("/nostream.", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidDataException("TuneIn reports that this station is unavailable or incompatible.");
                yield return PublicAddressPolicy.ValidateUri(item, false).AbsoluteUri;
            }
        }

        internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
        {
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                {
                    if (memory.Length + count > limit) throw new InvalidDataException("TuneIn response exceeds size limit.");
                    memory.Write(buffer, 0, count);
                }
                return memory.ToArray();
            }
        }

        private sealed class RelayBodyStream : Stream
        {
            private readonly SafeResponse response;
            private readonly CancellationTokenSource operation;
            private bool disposed;

            internal RelayBodyStream(SafeResponse response, CancellationTokenSource operation)
            { this.response = response; this.operation = operation; }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Token))
                using (read.Token.Register(response.Dispose))
                {
                    read.Token.ThrowIfCancellationRequested();
                    try { return await response.Body.ReadAsync(buffer, offset, count, read.Token).ConfigureAwait(false); }
                    catch { read.Token.ThrowIfCancellationRequested(); throw; }
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !disposed)
                {
                    disposed = true;
                    try { operation.Cancel(); }
                    finally
                    {
                        try { response.Dispose(); }
                        finally { operation.Dispose(); }
                    }
                }
                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

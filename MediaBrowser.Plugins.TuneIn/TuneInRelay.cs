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

        internal async Task<SafeResponse> OpenAsync(string stationId, CancellationToken token, Action<string> warning)
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
                    RejectPreRoll(urls);
                    audio = await OpenAlternativesAsync(urls, operation.Token, warning).ConfigureAwait(false);
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

        private async Task<SafeResponse> OpenAlternativesAsync(IEnumerable<string> urls, CancellationToken token, Action<string> warning)
        {
            var pending = new Queue<KeyValuePair<string, int>>(urls.Select(url => new KeyValuePair<string, int>(url, 0)));
            Exception lastFailure = null;
            for (var attempts = 0; attempts < 16 && pending.Count > 0; attempts++)
            {
                token.ThrowIfCancellationRequested();
                var candidate = pending.Dequeue();
                SafeResponse response = null;
                try
                {
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
                        var children = PlaylistUrls(text).Take(9).ToList();
                        if (children.Count == 0 || children.Count > 8)
                            throw new InvalidDataException("Empty or oversized radio playlist.");
                        RejectPreRoll(children);
                        pending = new Queue<KeyValuePair<string, int>>(children.Select(url =>
                            new KeyValuePair<string, int>(url, candidate.Value + 1)).Concat(pending).Take(16));
                    }
                    else
                    {
                        if (response.ContentType != "audio/mpeg" && response.ContentType != "audio/mp3"
                            && response.ContentType != "audio/aac" && response.ContentType != "audio/aacp"
                            && response.ContentType != "application/octet-stream")
                            throw new InvalidDataException("Unsupported radio content type.");
                        var playable = response;
                        response = null;
                        return playable;
                    }
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is SocketException
                    || error is AuthenticationException || error is TimeoutException)
                {
                    lastFailure = error;
                    warning("Advertised radio alternative failed; trying the next available alternative.");
                }
                finally { response?.Dispose(); }
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
                throw new NotSupportedException("TuneIn pre-roll-to-live sequencing is not supported by this relay contribution.");
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

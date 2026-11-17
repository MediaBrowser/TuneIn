using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn.Security
{
    internal sealed class SafeHttpTransport
    {
        private readonly Func<string, Task<IPAddress[]>> resolve;
        private readonly Func<IPAddress, Uri, CancellationToken, Task<Stream>> connect;

        internal SafeHttpTransport(Func<string, Task<IPAddress[]>> resolve = null,
            Func<IPAddress, Uri, CancellationToken, Task<Stream>> connect = null)
        {
            this.resolve = resolve ?? Dns.GetHostAddressesAsync;
            this.connect = connect;
        }

        public async Task<SafeResponse> OpenAsync(string value, bool catalogue, CancellationToken token, Action<int> statusReceived = null,
            Action<Uri> validateDestination = null)
        {
            var uri = PublicAddressPolicy.ValidateUri(value, catalogue);
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                token.ThrowIfCancellationRequested();
                validateDestination?.Invoke(uri);
                var lookup = resolve(uri.DnsSafeHost);
                if (await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(20), token)).ConfigureAwait(false) != lookup)
                {
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("Radio DNS lookup timed out.");
                }
                var addresses = await lookup.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (addresses.Length == 0 || addresses.Any(a => !PublicAddressPolicy.IsPublic(a)))
                    throw new InvalidOperationException("Non-public radio destination rejected.");

                var client = new TcpClient(Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                if (Socket.OSSupportsIPv6) client.Client.DualMode = true;
                Stream stream = null;
                var registration = token.Register(() => { stream?.Dispose(); client.Dispose(); });
                var timedOut = 0;
                using (var handshakeTimer = new Timer(_ => { Interlocked.Exchange(ref timedOut, 1); client.Dispose(); }, null,
                    TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan))
                    try
                    {
                        // Connect only to validated addresses, never perform a second DNS lookup.
                        if (connect != null)
                            stream = await connect(addresses[0], uri, token).ConfigureAwait(false);
                        else
                        {
                            await client.ConnectAsync(addresses, uri.Port).ConfigureAwait(false);
                            stream = client.GetStream();
                            if (uri.Scheme == "https")
                            {
                                var tls = new SslStream(stream, false);
                                stream = tls;
                                // Standard .NET certificate validation; do not require reachable online CRL/OCSP services.
                                await tls.AuthenticateAsClientAsync(uri.DnsSafeHost, null,
                                    SslProtocols.Tls12, false).ConfigureAwait(false);
                            }
                        }
                        var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost + "]" : uri.IdnHost;
                        if (!uri.IsDefaultPort) host += ":" + uri.Port;
                        var request = Encoding.ASCII.GetBytes("GET " + uri.PathAndQuery
                            + " HTTP/1.1\r\nHost: " + host
                            + "\r\nUser-Agent: EmbyTuneIn/" + typeof(Plugin).Assembly.GetName().Version + "\r\nAccept-Encoding: identity"
                            + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(request, 0, request.Length, token).ConfigureAwait(false);
                        var statusLine = await ReadLineAsync(stream, 4096, token, !catalogue).ConfigureAwait(false);
                        var parts = statusLine.Split(' ');
                        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
                            || !int.TryParse(parts[1], out var status))
                            throw new InvalidDataException("Invalid radio HTTP response.");
                        statusReceived?.Invoke(status);
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var size = statusLine.Length;
                        while (true)
                        {
                            var line = await ReadLineAsync(stream, 8192, token, !catalogue).ConfigureAwait(false);
                            size += line.Length + 2;
                            if (size > 32768) throw new InvalidDataException("Radio headers exceed limit.");
                            if (line.Length == 0) break;
                            var colon = line.IndexOf(':');
                            if (colon <= 0 || !Regex.IsMatch(line.Substring(0, colon), @"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")
                                || line.Any(c => (c < 32 && c != '\t') || c == 127))
                                throw new InvalidDataException("Invalid radio header.");
                            var name = line.Substring(0, colon);
                            var valueText = line.Substring(colon + 1).Trim();
                            if (headers.TryGetValue(name, out var previous))
                            {
                                // Radio servers repeat advisory ICY/CORS/cache fields. Never merge fields
                                // that determine framing, decoding or redirect destinations.
                                if (catalogue || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                                    || name.Equals("Location", StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException("Duplicate radio framing or destination header.");
                                headers[name] = previous + ", " + valueText;
                            }
                            else headers.Add(name, valueText);
                        }
                        if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                        {
                            if (redirects == 5 || !headers.TryGetValue("Location", out var location))
                                throw new InvalidDataException("Invalid radio redirect.");
                            var next = new Uri(uri, location);
                            if (uri.Scheme == "https" && next.Scheme != "https")
                                throw new InvalidOperationException("Radio TLS downgrade rejected.");
                            uri = PublicAddressPolicy.ValidateUri(next.AbsoluteUri, catalogue);
                            stream.Dispose();
                            registration.Dispose();
                            client.Dispose();
                            continue;
                        }
                        if (status != 200)
                            throw new InvalidDataException("Radio provider returned HTTP " + status + ".");
                        if (headers.TryGetValue("Content-Encoding", out var encoding)
                            && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Unexpected radio content encoding.");
                        var hasTransfer = headers.TryGetValue("Transfer-Encoding", out var transfer);
                        var chunked = hasTransfer && transfer.Equals("chunked", StringComparison.OrdinalIgnoreCase);
                        if (hasTransfer && !chunked
                            && (catalogue || !transfer.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidDataException("Unsupported radio transfer encoding.");
                        long? length = null;
                        if (headers.TryGetValue("Content-Length", out var contentLength))
                        {
                            if (hasTransfer || !long.TryParse(contentLength, NumberStyles.None,
                                CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                                throw new InvalidDataException("Invalid radio content length.");
                            length = parsed;
                        }
                        return new SafeResponse(new BodyStream(stream, client, registration, chunked, length),
                            headers.TryGetValue("Content-Type", out var type) ? type.Split(';')[0].Trim().ToLowerInvariant() : "application/octet-stream");
                    }
                    catch
                    {
                        stream?.Dispose();
                        registration.Dispose();
                        client.Dispose();
                        token.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref timedOut) != 0) throw new TimeoutException("Radio handshake timed out.");
                        throw;
                    }
            }
            throw new InvalidDataException("Radio redirect limit exceeded.");
        }

        internal static async Task<string> ReadLineAsync(Stream stream, int limit, CancellationToken token,
            bool allowLf = false)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1];
            while (bytes.Count <= limit)
            {
                if (await stream.ReadAsync(buffer, 0, 1, token).ConfigureAwait(false) == 0)
                    throw new EndOfStreamException("Truncated radio response.");
                if (buffer[0] == 10)
                {
                    var crlf = bytes.Count > 0 && bytes[bytes.Count - 1] == 13;
                    if (!crlf && !allowLf)
                        throw new InvalidDataException("Invalid radio line ending.");
                    var line = bytes.Take(bytes.Count - (crlf ? 1 : 0)).ToArray();
                    if (line.Contains((byte)13)) throw new InvalidDataException("Invalid radio line ending.");
                    return Encoding.ASCII.GetString(line);
                }
                bytes.Add(buffer[0]);
            }
            throw new InvalidDataException("Radio response line exceeds limit.");
        }

        private sealed class BodyStream : Stream
        {
            private readonly Stream inner;
            private readonly TcpClient client;
            private readonly CancellationTokenRegistration registration;
            private readonly bool chunked;
            private long? remaining;
            private long chunkRemaining;
            private bool chunkEnded;
            private bool ended;

            public BodyStream(Stream inner, TcpClient client, CancellationTokenRegistration registration,
                bool chunked, long? remaining)
            {
                this.inner = inner;
                this.client = client;
                this.registration = registration;
                this.chunked = chunked;
                this.remaining = remaining;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                using (token.Register(() => { inner.Dispose(); client.Dispose(); }))
                {
                    token.ThrowIfCancellationRequested();
                    try { return await ReadBodyAsync(buffer, offset, count, token).ConfigureAwait(false); }
                    catch { token.ThrowIfCancellationRequested(); throw; }
                }
            }

            private async Task<int> ReadBodyAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (count == 0 || ended) return 0;
                if (chunked && chunkRemaining == 0)
                {
                    if (chunkEnded && await ReadLineAsync(inner, 2, token).ConfigureAwait(false) != "")
                        throw new InvalidDataException("Invalid radio chunk ending.");
                    var line = await ReadLineAsync(inner, 512, token).ConfigureAwait(false);
                    if (!long.TryParse(line.Split(';')[0], NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out chunkRemaining) || chunkRemaining < 0)
                        throw new InvalidDataException("Invalid radio chunk size.");
                    if (chunkRemaining == 0)
                    {
                        ended = true;
                        return 0;
                    }
                    chunkEnded = true;
                }
                var available = chunked ? chunkRemaining : remaining ?? count;
                if (available == 0) return 0;
                var read = await inner.ReadAsync(buffer, offset, (int)Math.Min(count, available), token).ConfigureAwait(false);
                if (read == 0 && (chunked || remaining.HasValue))
                    throw new EndOfStreamException("Truncated radio body.");
                if (chunked) chunkRemaining -= read;
                else if (remaining.HasValue) remaining -= read;
                return read;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { inner.Dispose(); registration.Dispose(); client.Dispose(); }
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

    internal sealed class SafeResponse : IDisposable
    {
        public Stream Body { get; }
        public string ContentType { get; }
        public SafeResponse(Stream body, string contentType) { Body = body; ContentType = contentType; }
        public void Dispose() => Body.Dispose();
    }
}

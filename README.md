# TuneIn

This Emby channel plugin browses TuneIn radio. The current playback callback,
browsing behavior, plugin GUID, assembly and configuration filenames, display
name, and assembly version remain unchanged from upstream.

## Protected transport foundation

The plugin contains a bounded HTTP transport, a radio resolver for MP3/AAC and
M3U/PLS alternatives, and a server-local relay endpoint. The relay accepts only
station identifiers, requires both a local socket and loopback peer, and injects
opened streams through Emby's HTTP result factory. Its media-source helper is
prepared to expose a loopback-only source that requires transcoding. The helper
is not connected to the channel playback callback: the relay is inert during
normal browsing and playback, and the existing upstream network paths have not
been migrated to the protected transport.

The transport validates URI destinations, rejects DNS responses containing any
non-public address, connects to a validated address without a second DNS lookup,
and validates HTTPS certificates against the original host. Redirects are
limited to five and HTTPS cannot downgrade to HTTP. DNS lookup and connection
handshake time out after 20 seconds; stream resolution is limited to 30 seconds.
HTTP headers are limited to 32 KiB; Tune responses and individual playlists to
256 KiB; each playlist to eight entries and four nested levels; and resolution
to 16 attempts. Radio compatibility includes LF-delimited response headers,
repeated advisory headers, and close-delimited `Transfer-Encoding: identity`.
Ambiguous framing, unsupported encodings, provider fault URLs, and oversized
responses fail explicitly. Cancellation and disposal close opened connections.

Required pre-roll-to-live sequencing is not implemented. The prepared relay
rejects recognized pre-roll inputs rather than skipping advertisements or
concatenating incompatible formats. Because the relay is not activated, this
foundation does not change current station playback. Complete pre-roll handling
and channel callback activation must be integrated together. HLS, outbound
proxies, and private-network radio destinations are not supported by the relay.
Artwork still uses the upstream implementation.

## Host requirements and validation

The plugin requires patched Emby Server stable 4.9.1.81 or later, or compatible
beta 4.9.2.7 or later; the version gate rejects older/affected versions. This
requirement addresses a server vulnerability, not a vulnerability in the SDK
reference package. Installing this plugin DLL does not patch Emby Server; keep
the server itself updated.

The plugin targets `netstandard2.0` and builds against the existing
`mediabrowser.server.core` 4.6.0.50 and `System.Memory` 4.5.4 references. No
production SDK/reference upgrade is required for the transport foundation.
Build compatibility with those references is not evidence of runtime support
on the minimum host. No Emby runtime installation, minimum-version loading,
host authorization, station playback, or transcoding has been tested. Validate
those behaviors in Emby before integrating the relay into normal playback.

Build the solution with:

```powershell
dotnet build MediaBrowser.Plugins.TuneIn.sln -c Release
```

Run the transport regression tests with:

```powershell
dotnet test MediaBrowser.Plugins.TuneIn.sln -c Release
```

Tests cover DNS pinning and private-address rejection, redirects, framing and
legacy radio HTTP compatibility, bounded playlist resolution, local-only relay
access, stream ownership, cancellation, media-source preparation, and the host
version gate. Automated tests do not replace validation on an actual patched
Emby Server, including request injection, authorization, FFmpeg/ffprobe access
to loopback playback, disconnect cleanup, and remote-client isolation.

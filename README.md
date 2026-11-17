# TuneIn

This Emby channel plugin browses TuneIn radio. Browsing behavior, plugin GUID,
assembly and configuration filenames, display name, and assembly version remain
unchanged from upstream. The playback callback uses the protected relay described
below; other upstream network paths have not been migrated.

## Protected playback

The relay resolves MP3/AAC streams and M3U/PLS alternatives within the plugin.
Playback exposes only a server-loopback `/TuneIn/Relay/{Id}` source, disables
direct play and direct stream, and requires host probing/transcoding. No remote
provider URL is passed to Emby for playback. Relay requests require a local socket
and loopback peer, in addition to Emby's local-only authentication attribute.

Only leading instances of TuneIn's recognized HTTPS
`tunein-ondemand.cdnstream1.com/multi_bump_sonic_pre_pre.mp3` asset are required
sequential pre-rolls. After they finish, remaining entries are alternatives for
one live stream, not a queue. The same rule applies to nested M3U/PLS lists.
Arbitrary finite streams are not classified as advertisements. Missing live
entries, misplaced recognized ads, and redirects to ads without an explicit
sequence fail closed. Required ad opening or decoding failures cannot skip to
live. A live-source opening failure may try the next alternative; EOF, read, and
decoder failures after selection are terminal.

Pre-roll playback uses separate FFmpeg processes to decode each input into 48 kHz
stereo signed 16-bit PCM behind one streaming WAV header. Probing and playback
therefore receive WAV even when the ad is MP3 and live is AAC. Compressed inputs
are not concatenated and ad codec metadata is not exposed as live codec metadata.
Stations without recognized pre-roll, such as KEXP-style AAC sources, remain
compressed passthrough inside the relay. The media source does not guess the
codec/container from a URL. Probe and playback requests separately re-fetch
provider metadata, so provider or geolocation changes between requests remain
possible.

The host must provide a runnable
`IFfmpegManager.FfmpegConfiguration.EncoderPath`, optional FFmpeg environment
settings, and permission to start FFmpeg for pre-roll stations. FFmpeg receives
protected bytes on stdin with only the `pipe` protocol allowed, never a remote URL.
Deferred openings have a 30-second budget; cancellation and disposal close inputs
and terminate the decoder, with a five-second pipe-completion/cleanup bound.

The playback callback accepts `stream_` plus an allowlisted HTTP(S) catalogue URL
on the default port, without credentials or fragment, at the exact `/Tune.ashx`
path and with exactly one valid `id` matching `[spt][1-9][0-9]{0,9}`. Only that
station identifier reaches the local route; provider URLs and other query values
are discarded. The relay validates the station again and constructs a fresh
provider request.

## Protected transport

The transport validates URI destinations, rejects DNS responses containing any
non-public address, connects to a validated address without a second DNS lookup,
and validates HTTPS certificates against the original host. Redirects are limited
to five and HTTPS cannot downgrade to HTTP. DNS lookup and connection handshake
time out after 20 seconds; stream resolution is limited to 30 seconds. HTTP
headers are limited to 32 KiB; Tune responses and individual playlists to 256 KiB;
each playlist to eight entries and four nested levels; and resolution to 16
attempts. Radio compatibility includes LF-delimited response headers, repeated
advisory headers, and close-delimited `Transfer-Encoding: identity`. Ambiguous
framing, unsupported encodings, provider fault URLs, and oversized responses fail
explicitly. Cancellation and disposal close opened connections.

HLS, outbound proxies, and private-network radio destinations are not supported.
Browsing and artwork retain their upstream network implementations and are not
migrated to this relay.

## Host requirements and validation

Use patched Emby Server stable **4.9.1.81+** or compatible beta **4.9.2.7+**.
The plugin rejects older or affected hosts at load. See
[CVE-2025-64113 / withdrawn GHSA-95fv-5gfj-2r84](https://github.com/advisories/GHSA-95fv-5gfj-2r84):
the advisory was withdrawn because the flaw affects the actual server, not the
NuGet reference package. Installing this DLL does not patch Emby Server.

The plugin targets `netstandard2.0` and uses the existing
`mediabrowser.server.core` 4.6.0.50 and `System.Memory` 4.5.4 references. No
production SDK/reference upgrade is required. Build compatibility with these
references is not evidence of runtime support on the minimum host. No Emby
runtime installation, minimum-version loading, host authorization, live station
availability, or end-to-end playback/transcoding has been tested. Validate
patched-host loading, native request injection/authentication, ffprobe and FFmpeg
access to the loopback route, playback, stop/disconnect cleanup, and remote-client
denial in Emby before deployment. Normal certificate trust, hostname, and expiry
validation apply; online revocation checks are not required.

Build the solution with:

```powershell
dotnet build MediaBrowser.Plugins.TuneIn.sln -c Release
```

Run the regression tests with:

```powershell
dotnet test MediaBrowser.Plugins.TuneIn.sln -c Release
```

Tests cover DNS pinning and private-address rejection, redirects, framing and
legacy radio HTTP compatibility, bounded playlist resolution, local-only relay
access, stream ownership, cancellation, media-source preparation, host version
gating, required-ad ordering, live-only alternatives, and terminal stream errors.
To run generated mixed MP3/AAC fixtures and host decoder/factory integration tests,
set `TUNEIN_TEST_FFMPEG` to a runnable FFmpeg executable before running the test
command. Without it, those tests are skipped. The fixtures are generated in
memory, and the resulting WAV is decoded again to check the pipeline and PCM
sequence. Automated tests do not replace validation on an actual patched Emby
Server.

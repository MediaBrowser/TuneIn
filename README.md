# TuneIn

## Development

Build the plugin with `dotnet build MediaBrowser.Plugins.TuneIn.sln`.
Run the browsing regression tests with the .NET 8 SDK:

```text
dotnet test MediaBrowser.Plugins.TuneIn.Tests\MediaBrowser.Plugins.TuneIn.Tests.csproj
```

The tests follow provider OPML links through the channel's browsing API with a
mock HTTP client; they do not contact TuneIn. They cover pagination links whose
final `filter=s:~#` or `filter=p:~#` must send the hash as `%23`, while leaving
already encoded filters and other links unchanged.
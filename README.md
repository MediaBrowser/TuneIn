# TuneIn

An Emby plugin that provides TuneIn streams in the collection. The settings page
stores a TuneIn username and latitude/longitude values.

## Build and test

Prerequisites: .NET SDK 8 and Node.js 18 or later.

```powershell
dotnet build MediaBrowser.Plugins.TuneIn.sln --configuration Release
node --test MediaBrowser.Plugins.TuneIn.Tests/configPage.test.cjs
```

The controller tests use a simulated Emby view adapter; they do not validate the
plugin in a live Emby installation or browser. This change is limited to settings
view loading and navigation. Playback behavior and existing provider requests
are unchanged.

The project is based on [MediaBrowser/TuneIn](https://github.com/MediaBrowser/TuneIn)
and is distributed under the terms in [LICENSE.md](LICENSE.md).
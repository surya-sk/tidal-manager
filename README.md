# Tidal Manager

A Windows 11 app for putting TIDAL playlists and albums on a Rockbox player. It follows the Windows light/dark setting, uses the system accent color and Mica, and has its own app icon. It shows your saved TIDAL playlists and lets you search for albums. A single **sync** button downloads what's missing with a separately installed `tiddl`, copies the FLACs to the player with album art, and writes one M3U8 per playlist. The app does not stream, decrypt or encode audio itself, and it has no cloud sync or sharing.

## Using it

The app opens on one hub page that scrolls sideways:

- **playlists** shows your saved TIDAL playlists and mixes as cover tiles. Tap a tile to put it on the player. Selected tiles get an accent border and a check in the corner. Tap again to remove it.
- **albums** shows the albums you've chosen. Use the **find albums** tile or the search box to search TIDAL, then tap results to add them.
- **settings** (the gear at the top right) slides in from the right. It holds the TIDAL account, the `tiddl` command, the download folder, the player folder, and the cleanup option.
- The bottom bar shows a progress bar, the current status, and **sync**. While a sync runs, the same button cancels it.

Your choices are saved in `%LOCALAPPDATA%\TidalManager\sync-set.json`. A successful sync clears the choices that were synced; cancelled or failed syncs leave them selected so you can retry.

## What sync does

1. Fetches the current track list of every chosen playlist and album from TIDAL. Tracks that TIDAL lists as unavailable are counted and skipped.
2. Scans the download folder and the whole player for those tracks, showing progress. Only the text tags are read (embedded pictures are skipped), and tags are kept in `%LOCALAPPDATA%\TidalManager\tag-cache.json`, so unchanged files are not read again. A track already on the player is reused and never copied twice; if it sits outside `<player>\Music`, it is moved into `Music\<Album artist>\<Album>\` along with the album's cover, and other playlists in `Playlists` that pointed at it are updated.
3. Downloads only the missing tracks, ten per `tiddl download --path <folder> --scan-path <folder> url <track URLs…>` run.
4. Copies the new FLACs to `<player>\Music\<Album artist>\<Album>\NN - Title.flac`. Only tracks that belong to the chosen playlists and albums are copied, never the rest of the download folder.
5. Writes `<player>\Playlists\<playlist name>.m3u8` in TIDAL order. On a drive root the entries are absolute (`/Music/...`); in a subfolder they are relative to `Playlists`. After writing, every entry is re-resolved on the player. A playlist with tracks that could not be downloaded is still written; the summary says how many were missing. The app never overwrites an M3U8 it didn't write.
6. Adds album art. Every album folder used on the player gets a `cover.jpg`, a TIDAL cover of up to 640px; an existing `cover.*`/`folder.*` image is kept. Newly copied FLACs also get the cover embedded. tiddl does not embed covers by default, so its downloads have none. TIDAL serves progressive JPEGs, which Rockbox cannot decode, so covers are re-encoded as baseline JPEGs with Windows' imaging codec before they are written. In the synced album folders, progressive `cover.jpg`/`folder.jpg` files and progressive embedded art are re-encoded the same way. Only the picture is rewritten; the audio is untouched.
7. If **Delete downloads once they're verified on the player** is ticked, it deletes source FLACs from the download folder only after they're verified. A newly copied file must be hash-checked through the copy. A reused file is only deleted if it is byte-identical to the player copy. The option is off by default.

Tracks are matched by tags: the album and title must match (the title with or without TIDAL's version suffix, e.g. `Wonderwall (Remastered)`), and at least one artist must be shared. This accounts for tiddl writing `ARTIST` as a sorted list of all track artists. Different editions or altered tags can still cause a miss. Hidden folders on the player (`.rockbox`, `.Trash-*`, `.git`) are not scanned.

Confirm the first sync on the device itself: Rockbox should list the playlist under `/Playlists`, and each album should show its cover.

## Setup

### Install on another Windows 11 x64 PC

Build the release ZIP with `./tools/Publish-Installer.ps1` from PowerShell on the development PC. Copy `dist/TidalManager-win-x64.zip` to the other PC, extract it, and run `Install-TidalManager.ps1` from PowerShell. The script installs the app for the current user in `%LOCALAPPDATA%\Programs\TidalManager` and creates a Start menu shortcut. No Visual Studio or .NET SDK is needed on that PC. This is a self-contained unpackaged app, not an MSIX, so it does not require a signing certificate.

`tiddl`, Python 3.13+, and ffmpeg are separate prerequisites on the destination PC. Install `tiddl` with `py -m pip install tiddl`, run `tiddl auth login`, and ensure `tiddl` and ffmpeg are on PATH (or enter the full `tiddl.exe` path in settings). Settings now warns if the downloader cannot be found. See `installer/INSTALL.txt` inside the ZIP.

### Develop from source

1. Open `TidalManager.slnx` in Visual Studio 2026 with the WinUI workload, set **TidalManager.App** as the startup project, choose **Tidal Manager (Unpackaged)** and press F5. Or run `dotnet run --project src/TidalManager.App/TidalManager.App.csproj -p:Platform=x64 --launch-profile "Tidal Manager (Unpackaged)"`.
2. Register an app at [TIDAL Developer](https://developer.tidal.com/) with the redirect URI `http://127.0.0.1:8888/callback/` (or any URI you also enter in settings). The app requests `user.read collection.read`. Album search worked with these scopes on a third-party developer account; if TIDAL refuses search for your app, it needs `search.read`.
3. In **settings**, enter the client ID and choose **sign in**. The browser returns automatically through the loopback address. If it can't, paste the final address into the box that appears. The client ID and redirect URI are stored in `%LOCALAPPDATA%\TidalManager\settings.json`; the session is protected with Windows DPAPI in `session.dat`.
4. Install `tiddl` (`pip install tiddl`; it needs Python 3.13+ and ffmpeg), run `tiddl auth login`, and enter `tiddl` or its full path in settings. The download folder defaults to `Music\tiddl`. The app's TIDAL session is not shared with tiddl.
5. Set the player folder to the mounted player or SD-card root, e.g. `D:\`. These settings are stored in `%LOCALAPPDATA%\TidalManager\download-settings.json`.

No client credentials are embedded in this repository. Use external tools only where you have the rights to do so; see TIDAL's [developer terms](https://developer.tidal.com/documentation/guidelines/guidelines-developer-terms).

## Code

- [Hub](src/TidalManager.App/Views/HubPage.xaml), [album search](src/TidalManager.App/Views/SearchPage.xaml), [settings pane](src/TidalManager.App/Views/SettingsPane.xaml), [window, app bar and sync button](src/TidalManager.App/MainWindow.xaml), [Metro styles](src/TidalManager.App/App.xaml)
- [Sync pipeline](src/TidalManager.App/SyncEngine.cs) and [saved choices](src/TidalManager.App/SyncSetStore.cs)
- [TIDAL API client](src/TidalManager.Tidal/LibraryClient.cs) and [PKCE sign-in](src/TidalManager.Tidal/BrowserAuthService.cs)
- [Player copy, playlists and album art](src/TidalManager.Local/PlayerSyncService.cs), [tag matching](src/TidalManager.Local/LocalCatalogMatcher.cs)
- [tiddl process adapter](src/TidalManager.Queue/ExternalCliDownloadService.cs)

`LocalMusicOrganizer` (copy any FLAC folder into `Artist/Album` folders) is still in the Local library and tested, but it no longer has UI.

## Tests

- `dotnet run --project tests/TidalManager.Tidal.Tests/TidalManager.Tidal.Tests.csproj` covers PKCE, refresh, playlists, search, tracks and artwork against simulated HTTP responses.
- `dotnet run --project tests/TidalManager.Local.Tests/TidalManager.Local.Tests.csproj` covers player sync, playlists, album art, cleanup and tag matching. It generates a short FLAC with `ffmpeg`, which must be on PATH.
- `dotnet run --project tests/TidalManager.Queue.Tests/TidalManager.Queue.Tests.csproj` covers the tiddl adapter, using the test executable as a stand-in for tiddl.

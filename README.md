# LiquidCloud

A customizable fork of [GlassPlayer](https://github.com/Jonzu00/GlassPlayer) by Jonzu00.
Windows 10/11 music player for SoundCloud, YouTube Music, Spotify and local files.

## Install

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows x64).
2. Double-click **build.bat**. It builds `LiquidCloud\LiquidCloud.exe`, puts a **LiquidCloud** shortcut on your desktop and starts the app.
   After that, just use the shortcut.

No .NET on the PC? Push the project to GitHub: the included Actions workflow builds a ready-to-run `LiquidCloud-win-x64` zip.

## What's in LiquidCloud

**Layout.** Sidebar (Home, Discover, Library, SoundCloud web, Themes, your playlists, account), a page area in the middle,
and the player on the right with *Playing / Lyrics / Up next* tabs. Full-window player (F11) and an always-on-top mini player (Ctrl+M).

**Home.** Continue listening, Recently played, Your rotation, Made for you (mixes around your top artists),
Recommended for you, Favorite tracks, New discoveries, Recently added, your playlists, popular playlists.

**Discover.** Real SoundCloud search with tabs for Top, Tracks, Artists, Playlists and Albums, suggestions while you type,
search history, genres, trending, new this week, popular artists. Results load more as you scroll.
YouTube Music and Spotify are still available as sources. Paste any link into the search box to play it.

**Player.** Play/pause, previous/next, shuffle (every song once per cycle), repeat off / all / one, seek, volume and mute,
favorites, add to playlist, a global queue you can reorder by dragging, lyrics from the file, level bars driven by
your real audio output, and LiquidCloud remembers the queue and position between launches.

**Playlists.** LiquidCloud playlists (create, rename, describe, custom cover, add/remove, drag to reorder, play, shuffle, repeat,
delete) and your SoundCloud playlists (add/remove songs, create new private ones). Every change tells you whether it was
saved in LiquidCloud or on SoundCloud; if SoundCloud refuses a change, you see the error instead of a fake success.

**Themes.** Theme studio with built-in cold-blue "liquid glass" themes and your own: colors, glass tint and opacity,
blur, glow, corner radius, shadows, background gradient. Live preview, save, duplicate, delete, set as default,
import/export as `.lctheme` files. Custom wallpaper (PNG/JPG/GIF) and particles are in Settings.

**Intro.** "LiquidCloud. Hi, {name}. Nice {time of day}, isn't it?" when the app opens (can be turned off).

**Motion.** One motion system (shared durations and easing curves), only opacity/transform animations,
and it follows Windows' "Show animations" setting plus an in-app *Reduce motion* switch.

**Keyboard.** Space, Ctrl+←/→, Shift+←/→ (seek), Ctrl+↑/↓ (volume), M, S, R, L, Q, Ctrl+K, Ctrl+M, F11, Alt+←.
Media keys work in the background.

**About SoundCloud.** LiquidCloud uses the same web API soundcloud.com uses (not the official developer API, which needs a paid
Artist Pro plan). It's meant for personal use and may break if SoundCloud changes its website.

## Discord status (optional)

1. Open https://discord.com/developers/applications → **New Application** → name it **LiquidCloud**
   (this name is what Discord shows: "Listening to LiquidCloud").
2. Copy the **Application ID** and paste it into `config.json` next to `LiquidCloud.exe`:
   `"DISCORD_CLIENT_ID": "1234567890"`.
3. Optional: under **Rich Presence → Art Assets** upload `GlassPlayer/icon.png` with the name `logo`
   (shown as the small icon and for local songs without a cover).
4. Restart LiquidCloud with the Discord desktop app running.

Your profile shows the song, "by artist", the cover, a live `0:07 / 3:27` progress bar,
and (on hover over the pictures) how long you've been listening: "enjoying for 01:23:45".

## Spotify (optional, needs Premium)

Create an app at https://developer.spotify.com/dashboard with redirect URI `http://127.0.0.1:5543/callback`
and put its Client ID in `config.json` next to `LiquidCloud.exe`.

## License

GNU General Public License v3.0, same as the original. See [LICENSE](LICENSE).
This is a modified version; changes are listed above.

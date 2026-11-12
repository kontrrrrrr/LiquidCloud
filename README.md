# LiquidCloud

A customizable fork of [GlassPlayer](https://github.com/Jonzu00/GlassPlayer) by Jonzu00.
Windows 10/11 music player for SoundCloud, YouTube Music, Spotify and local files.

## Install

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows x64).
2. Double-click **build.bat**. It builds `LiquidCloud\LiquidCloud.exe`, puts a **LiquidCloud** shortcut on your desktop and starts the app.
   After that, just use the shortcut.

No .NET on the PC? Push the project to GitHub: the included Actions workflow builds a ready-to-run `LiquidCloud-win-x64` zip.

## What's new compared to the original

- **Themes**: Glass, Aurora, Sunset, Ocean, Sakura, Ember, Forest, Mono.
- **Your own background**: PNG, JPG, JPEG or animated GIF, with darken and blur sliders.
  Buttons, sliders and panels take their colors from the picture, and bright pictures are darkened automatically so text stays readable.
- **SoundCloud inside the player**: "Browse SoundCloud" opens the real soundcloud.com in the left panel.
  Sign in on SoundCloud's own page (Glass never sees your password), scroll your feed, likes and playlists,
  and press play on anything: it plays in Glass. "Play this page in Glass" loads a whole playlist or profile.
- **Your SoundCloud in the player**: under the SoundCloud tab press "Sign in to SoundCloud", sign in once on
  SoundCloud's page, and your **Likes**, **Feed** and **Playlists** appear as normal lists in Glass.
  The login token is stored encrypted for your Windows user only (`%AppData%\GlassPlayer\soundcloud-token.bin`).
  This uses SoundCloud's website API, not an official one, so it's meant for personal use and may break if SoundCloud changes things.
- **Lyrics from the song file**: read from the file's tags or a `.lrc` file next to it. Shown under the cover;
  "Lyrics ⤢" opens them across the whole panel while the cover glides into the corner. Timed lyrics follow the music;
  click a line to jump to it.
- **Particles**: dots, snow, stars or bubbles, with amount, size, speed and color.
- **More animation**: pulsing play button, cover morph, theme swatches, adjustable animation speed.
- **Bigger settings menu**: panel opacity, corner roundness, glow, lyric size and more.
- **Lighter on memory**: virtualized song list, small thumbnails, covers and wallpapers decoded at screen size,
  effects pause while you use other windows, the SoundCloud browser is closed when you leave it,
  and memory is handed back to Windows when Glass is minimized. Also: import whole folders, drag & drop files.

- **Endless autoplay**: when a SoundCloud or YouTube list ends, it keeps going with suggested songs (like SoundCloud).
- **Faster starts**: songs stream right away instead of downloading first, the next song is prepared ~25 s early,
  and SoundCloud's key is remembered so the first search is quick.

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

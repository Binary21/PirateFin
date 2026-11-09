<p align="center">
<img alt="PirateFin logo banner" src=".github/assets/piratefin-banner.png" width="600"/>
</p>
<h3 align="center">A Jellyfin fork that finds, downloads and organises your shows, anime and movies for you</h3>

---


PirateFin is a fork of the [Jellyfin](https://github.com/jellyfin/jellyfin) media server. Jellyfin only plays media you already have. PirateFin builds the library itself: it follows release schedules, creates the shows, seasons and episodes before they air, and downloads them through your own torrent client when they're released or when someone presses Play.

Everything else in Jellyfin works as before. For Jellyfin's own documentation, build instructions and community links, see the **[original Jellyfin README](https://github.com/jellyfin/jellyfin/blob/master/README.md)**.

> **Note:** PirateFin automates torrent downloads. You're responsible for making sure that what you download is legal where you live.

---

## How PirateFin differs from Jellyfin

### Library that fills itself
- **Release schedules:** weekly schedules come from [Trakt](https://trakt.tv) for TV, [AniList](https://anilist.co) for anime and YTS for movies. Placeholder items are created for every announced episode, with the correct air times.
- **Search beyond your library:** search returns Trakt (TV) and AniList (Anime) and YTS (Movie) titles, not only what's on disk. Opening a result creates the series with its folders and metadata.
- **Anime episode details:** titles, descriptions and stills come from TMDB, matched to AniList episodes by air date.
- **Popular rows:** each library gets a "Popular" row built from TMDB popularity (AniList for anime). The titles are added as placeholders, so they can be played straight away.

### Downloading
- **Torrent search through [Jackett](https://github.com/Jackett/Jackett):** results are filtered by preferred release groups that you set per library type. If none of them has a release, PirateFin shows the best-seeded alternatives for you to pick from.
- **Downloading through [qBittorrent](https://www.qbittorrent.org):** downloads are sequential, and an item can be played once about 10% is on disk with no gaps. The Play button fills as the download progresses and shows its state: Downloading, Queued, Stalled, Paused or Failed. Updates are pushed to the page over the WebSocket.
- **Playing while downloading:** the player pauses just before the part that hasn't downloaded yet, then carries on by itself once enough has arrived.
- **Subscriptions:** each user subscribes to seasons. Watching half of an episode subscribes you to its season and fetches the next two episodes. New episodes download automatically.
- **Requests:** if no release is found, users can send a download request. Admins handle it on the dashboard and can attach a magnet link. Each user has a configurable limit on open requests.
- **Multiple drives:** new titles go to the library drive with the most free space, and downloads pause when every drive is below a minimum free space. A series split across drives is moved back together overnight.

### Streaming-service style interface
*Requires the PirateFin web client, a fork of jellyfin-web.*
- A Netflix-style look across the app, for both desktop and TV layouts (Currently only available on Samsung and LG TV's).
- **Shows, Anime and Movies tabs** with:
  - a rotating billboard (continue watching, new episodes of followed seasons, newest additions), which plays saved trailers;
  - genre rows personalised to each user's viewing.
- **Profiles:**
  - "Who's watching?" picker with avatar packs that admins can manage;
  - optional 4-digit PINs;
  - kids profiles that require a PIN to leave and use a simplified theme;
  - "Remove from Continue Watching".
- **Player:**
  - Skip Intro and Skip Recap prompts, with "skip all future intros" per series;
  - a next-episode countdown that starts when the credits begin;
  - "Are you still watching?" after 3 episodes or 90 minutes.
- **YouTube tab** (optional, off by default): a user's subscriptions and recommendations from the official YouTube Data API, with playback through yt-dlp. **WIP**

### Notifications and accounts
- **Email through Gmail** (an app password on a dedicated account): HTML emails for new episodes, closed requests and slow downloads. The dashboard has a setup wizard.
- **In-app inbox** (the bell icon): open requests, plus notifications when episodes are ready.
- **Forgot password:** sends an emailed temporary password, valid for 30 minutes. The old password keeps working until the temporary one is used.
- **Remote admin lock:** by default, admins signing in from outside the home network get no dashboard access. This can be changed in a dashboard setting.

### Dashboard additions
- **Insights per user:** watch time, genre ranking, an hour-by-weekday heatmap, top titles and drop-offs. Admins can let users see their own stats.
- **PirateFin pages:** settings for providers, Gmail, requests, storage, trailers, YouTube and avatar packs.

---

## Requirements

On top of [Jellyfin's own requirements](https://github.com/jellyfin/jellyfin/blob/master/README.md) PirateFin is dependent on several external applications and services:

| Service | Needed for | Where to configure |
|---|---|---|
| **qBittorrent** with the Web UI enabled | Downloading | `Admin Dashboard → PirateFin → qBittorrent` |
| **Jackett** | Torrent search | `Admin Dashboard → PirateFin → Jackett` (URL, API key, indexers, preferred release groups) |
| **Trakt API application** | TV schedules and search | `Admin Dashboard → PirateFin → Providers` |
| Gmail account with an app password *(optional)* | Email | `Dashboard → PirateFin → Email` |
| Google Cloud project *(optional)* | YouTube tab | `Dashboard → PirateFin → YouTube` |

yt-dlp is downloaded automatically when it's needed, for trailers and YouTube playback.

**Platform:**
- PirateFin is currently built and tested on **Windows**.
- Storage balancing uses directory junctions, and stored secrets are encrypted with Windows DPAPI.

**Before you share or commit your setup:** the configuration files above hold your API keys and passwords. Keep your filled-in copies out of version control.

## Building

The server builds the same way as Jellyfin. See *Server Development* in the [original README](https://github.com/jellyfin/jellyfin/blob/master/README.md).

PirateFin's server code lives mostly in:
- `Emby.Server.Implementations/PirateFin/`
- `Jellyfin.Server/PirateFin/`
- `MediaBrowser.Controller/PirateFin/`
- `MediaBrowser.Model/PirateFin/`

PirateFin's tests are in `tests/Jellyfin.Server.Implementations.Tests/PirateFin/`.

The PirateFin UI needs the PirateFin web client. Build it and copy its `dist` output into the server's `jellyfin-web` folder, as you would with jellyfin-web.

## License

PirateFin is a fork of Jellyfin and, like Jellyfin, is licensed under the [GNU General Public License v2.0](LICENSE). It is **NOT** affiliated with nor endorsed by the Jellyfin project.

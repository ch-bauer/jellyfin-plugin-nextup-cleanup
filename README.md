<div align="center">
  <img src="images/icon.png" alt="Next Up Cleanup for Jellyfin" width="128" />
  <h1>Next Up Cleanup for Jellyfin (Proof of Concept)</h1>
</div>

Clears the first episodes of series you never started out of the **Next Up** row, and
collapses a show that fills **Continue Watching** with half-finished episodes down to
the one you are actually on — so both rows hold what you are watching again, on **every
client**, because the server's response is filtered before it leaves the server. **No
watch data is touched**: nothing is marked, reset or deleted, and switching the plugin
off puts the rows straight back to how Jellyfin serves them.

## Why the row fills up in the first place

This is not corrupted watch data — it is the server working as built. Jellyfin
[#13687](https://github.com/jellyfin/jellyfin/pull/13687) rewrote the Next Up query for
speed (10 s → 1.5 s) and, as a side effect, stopped excluding the first episode of series
with no play history. Since 10.11 every unwatched show in the library can therefore put its
S01E01 in the row, in among the shows you are half-way through
([#13743](https://github.com/jellyfin/jellyfin/issues/13743), closed as not planned).

There is no server or user setting to turn it off, and there is nothing in the database to
repair — resetting watch data changes nothing, because the entries are not coming from
watch data. The only thing that helps is filtering what Next Up returns, which is what this
plugin does.

## How it works

An **MVC action filter** edits the `QueryResult<BaseItemDto>` the controller returned,
before any of it is serialised. That is what makes it client-proof: it does not matter
which JSON casing profile the client negotiated, whether the response is gzip, brotli or
deflate, or what other plugins do to the body afterwards — none of that has happened yet.
It also reaches rows built by *plugin* controllers, which response middleware sitting on
the URL cannot reliably do.

Rows are recognised by the controller and action Jellyfin dispatched to, not by the URL
text, so a reverse-proxy base path (`/jellyfin/Shows/NextUp`), the `/emby` prefix older
clients use, and the legacy spelling of a route all match on their own:

| Row | Controller / action |
| --- | --- |
| Next Up, every stock client | `TvShows` / `GetNextUp` |
| Continue Watching (`/UserItems/Resume`, `/Users/{id}/Items/Resume`) | `Items` / `GetResumeItems`, `GetResumeItemsLegacy` |
| Home Screen Sections rows | `HomeScreen` / `GetSectionContent` |

The Home Screen Sections plugin builds its rows in process and serves every one of them
from that single action, so `/Shows/NextUp` never sees the request; which row it is comes
from the section id. Sections that mention Next Up, Resume or Continue are filtered —
including `ContinueWatchingNextUp`, the merged row that plugin's *combine Continue
Watching and Next Up* option serves — and Latest Media, My Media and Live TV are left
alone, since a newly added `S01E01` belongs in those.

What the filter then does to a row:

- Episodes with season 1, episode 1 are dropped. `S02E01` stays: that one means you
  finished season one.
- On a row that also carries **Continue Watching**, an episode you are genuinely part-way
  through is never removed, whichever mode is configured. "Part-way through" means a resume
  position past the *started watching* mark, and only that — a few seconds in is a mis-tap,
  not something to carry on with.
- A series with several part-way-through episodes in the row is collapsed to the one you
  played most recently, decided by `LastPlayedDate` rather than by how far into an episode
  you are. The row's own order is kept; entries are only dropped, never reordered.
- The controller applies the client's row length *before* anything is removed, so a
  20-item row would come back short. The `limit` argument is inflated and the result
  trimmed back to the length the client asked for, and `TotalRecordCount` is corrected so
  paging clients behave. Requests for a later page are filtered but not over-fetched,
  since the offset would no longer line up.
- Any response the plugin fails on is left exactly as the controller produced it — a
  broken filter degrades to stock Jellyfin, never to a broken row.

## Configuration

Dashboard → Plugins → Next Up Cleanup.

- **Enable filtering** — off makes the plugin a pass-through.
- **What to hide**
  - *Every first episode* (default) — all `S01E01` entries, without exception. This is the
    one that empties the row.
  - *Only untouched first episodes* — narrower: keeps an `S01E01` you have actually
    watched, and removes the rest.
- **Started watching after (minutes)** — default 5. Jellyfin writes a resume position, a
  play count *and* a play date the instant playback begins, so a mis-tap or a look at the
  opening titles is enough to pin an episode to the row for good. Below this mark an
  episode counts as never started. 0 makes any resume position count.

An episode counts as **watched** only if it is marked played, or its resume position is
past that mark. A play count and a play date on their own deliberately do not count —
they are written on the first frame and say nothing about whether you watched anything.

On a row that also carries Continue Watching, both modes keep any episode past the mark,
so the thing you are genuinely part-way through never disappears.
- **Over-fetch multiplier** — how much longer a row to ask the server for so it is still
  full after filtering (default 3; 1 disables it).
- **Over-fetch ceiling** — upper bound on that inflated length (default 150).
- **One entry per series** (default on) — collapses a series to the episode you played
  most recently.
- **Episodes per series** — how many episodes of one series may stay (default 1).
- **One entry per movie** (default off) — only matters when the same film is in the
  library more than once.
- **Reset episodes under (minutes)** — the mark used by the scheduled task below
  (default 2).

## Resetting abandoned episodes

Filtering hides barely-started episodes without touching anything. If you also want the
stale play state *gone*, there is a scheduled task — **Dashboard → Scheduled Tasks →
Reset abandoned episodes**.

It clears the resume position, play count and play date of any episode stopped before the
reset mark. Episodes **marked played are never touched**, so a show you finished and
restarted keeps its history.

This is the only thing in the plugin that writes to the database, and it **cannot be
undone**. It therefore has no default trigger and never runs on its own — you start it by
hand, or give it a trigger yourself if you want it periodic.

## Installation

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/ch-bauer/jellyfin-plugin-nextup-cleanup/main/manifest.json`
2. Install **Next Up Cleanup** from the catalog and restart Jellyfin.

Requires Jellyfin **10.11**.

## A row is still full of first episodes

Turn on debug logging (Dashboard → Logs, or `"Jellyfin.Plugin.NextUpCleanup": "Debug"` in
`logging.json`) and reload the home screen. Every row the plugin touched logs one line
with the controller and action it matched and what it removed.

- **A line with `hid 0`** — the plugin saw the row and there was nothing matching `S01E01`
  in it, or the mode is *Only untouched first episodes* and the entries have play state.
- **A `returned N first-episode entr(ies) and is not an endpoint this plugin filters`
  line** — that controller and action is the row, and it needs adding to `Classify` in
  `NextUpFilter.cs`. The plugin looks for this on every action it does not handle, so an
  unknown row endpoint names itself.
- **No line at all** — the plugin is not loaded, or filtering is switched off.

## Building

```sh
dotnet test
dotnet publish src/Jellyfin.Plugin.NextUpCleanup -c Release
```

## License

MIT

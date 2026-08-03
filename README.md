# Next Up Cleanup for Jellyfin (Proof of Concept)

Clears the first episodes of series you never started out of the **Next Up** row, so it
holds shows you are actually watching again — on **every client**, because the server's
response is filtered before it leaves the server. **No watch data is touched**: nothing
is marked, reset or deleted, and switching the plugin off puts the row straight back to
how Jellyfin serves it.

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

- Middleware in front of every endpoint that can serve a Next Up row:
  - `/Shows/NextUp` — every stock client.
  - `/HomeScreen/Section/{id}` — the Home Screen Sections plugin and Jellyfin Enhanced
    build their rows in process, so `/Shows/NextUp` never sees the request. Sections whose
    id mentions Next Up, Resume or Continue are filtered; Latest Media, My Media and Live
    TV are left alone, since a newly added `S01E01` belongs in those.
  - `/UserItems/Resume` and `/Users/{id}/Items/Resume` — the Continue Watching row, which
    the 10.11 web client merges Next Up into.

  A base path left in front of the route by a reverse proxy (`/jellyfin/Shows/NextUp`)
  still matches.
- On a combined **Continue Watching / Next Up** row, *Every first episode* is narrowed to
  *Only untouched first episodes* — an episode you are genuinely part-way through is never
  taken out of Continue Watching, whichever mode is configured.
- Episodes with season 1, episode 1 are dropped from the response. `S02E01` stays: that
  one means you finished season one.
- The server applies the client's row length *before* the plugin removes anything, so a
  20-item row would come back short. The request is over-fetched and trimmed back to the
  length the client asked for, and `TotalRecordCount` is corrected so paging clients
  behave. Requests for a later page are filtered but not over-fetched, since the offset
  would no longer line up.
- Anything that is not a `200` with a parseable body is passed through untouched, as is
  any response the plugin fails on — a broken filter degrades to stock Jellyfin, never to
  a broken row.

## Configuration

Dashboard → Plugins → Next Up Cleanup.

- **Enable filtering** — off makes the plugin a pass-through.
- **What to hide**
  - *Every first episode* (default) — all `S01E01` entries, without exception, including
    one you happen to be part-way through.
  - *Only untouched first episodes* — keeps a first episode that has a resume position, a
    play count, a played flag or a played date; hides the rest.
- **Over-fetch multiplier** — how much longer a row to ask the server for so it is still
  full after filtering (default 3; 1 disables it).
- **Over-fetch ceiling** — upper bound on that inflated length (default 150).

## Installation

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/ch-bauer/jellyfin-plugin-nextup-cleanup/main/manifest.json`
2. Install **Next Up Cleanup** from the catalog and restart Jellyfin.

Requires Jellyfin **10.11**.

## A row is still full of first episodes

Turn on debug logging (Dashboard → Logs, or `"Jellyfin.Plugin.NextUpCleanup": "Debug"` in
`logging.json`) and reload the home screen. Every row the plugin touched logs one line
with the path it matched and how many entries it hid.

- **A line with `hid 0`** — the plugin saw the row and there was nothing matching `S01E01`
  in it, or the mode is *Only untouched first episodes* and the entries have play state.
- **No line at all** — the row is served by an endpoint the plugin does not know about.
  Open the browser dev tools, reload the home screen, and check the Network tab for the
  request behind that row; the path in it is what needs adding.

## Building

```sh
dotnet test
dotnet publish src/Jellyfin.Plugin.NextUpCleanup -c Release
```

## License

MIT

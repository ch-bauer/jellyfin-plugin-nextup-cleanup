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

- Middleware in front of `/Shows/NextUp` — and `/HomeScreen/Section/NextUp*`, for the
  Home Screen Sections plugin and Jellyfin Enhanced, which serve their own home rows.
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

## Building

```sh
dotnet test
dotnet publish src/Jellyfin.Plugin.NextUpCleanup -c Release
```

## License

MIT

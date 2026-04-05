# Changelog

## 2026-04-05 — First Feature Update

**New features**

- Added config-file support for slideshow background color via optional `BackgroundColor`. The current config accepts `black` and `white`; invalid values fall back to the default black background and are dropped on save. Launcher UI for background selection remains future work.
- Added a "Screensaver-mode" `--slideshow` launch flag to start playback directly from `MantelviewConfig.json`, while falling back to the launcher if the saved folder is missing, invalid, or empty. Screensaver-mode launches exit the app when the slideshow closes instead of returning to the launcher.
- Added config-driven transition selection for `Crossfade`, `Slide`, `Cover`, and `Uncover`. Optional `TransitionEffects` config field loads case-insensitively, resolves synonyms to canonical names, drops invalid entries, deduplicates without weighting, and falls back to default `Crossfade`. `TransitionEffects` also accepts `Cut` outside e-ink mode. When users mix `Cut` with timed effects such as `Crossfade`, `ImageDurationSec` remains independent of transition time: `Cut` stays instantaneous, while timed effects still add `TransitionDurationSec` on top of the display interval.
- Added provisional `SmartSlide` as a valid `TransitionEffects` config name. Unlike the legacy `Slide`, it derives `Stretch="Uniform"` fitted image bounds and keeps visible content edges in contact during the push, avoiding transient black gaps with letterboxed or pillarboxed images.
- Added optional `MaxCatalogImages` config field. When set, Mantelview applies the limit before full-set sorting/materialization, preserves the setting through launcher saves, and warns in both the launcher and fullscreen slideshow when playback is truncated.

**Fixes**

- Moved memory reclaim from a blocking aggressive GC on the UI thread to a background `malloc_trim` timer (Linux only, 30s interval during display). Eliminates a suspected source of transition stutter on all platforms and removes a pointless GC penalty on Windows.

- Fixed a config-loader regression where a malformed config field could cause Mantelview to fall back to a default root and later rewrite the whole config file. The loader now repairs missing commas in known array/property cases and, if needed, drops only the malformed field so the rest of the config is preserved.
- Malformed `IgnoredKeys` are no longer silently dropped, and destructive recoveries surface a launcher warning. Saving after a degrading recovery now writes `MantelviewConfig.json.bak` once before the recovered config is persisted.
- Oversized `MantelviewConfig.json` files >64KB are blocked from loading. The launcher now warns that the oversized config cannot be loaded, and the first save (app close or Play) after that fallback writes the original oversized file to `MantelviewConfig.json.bak` before replacing it.

## 2026-04-02 — First Public Release

- First public release of Mantelview.

- Delivered the core fullscreen slideshow experience for local image folders on Windows and Linux, with persisted launcher settings for folder selection, display timing, transition timing, shuffle, topmost, and e-ink mode.

- Added recursive image catalog loading for JPEG, PNG, and WebP inputs, magic-byte validation, and the two-bitmap preload/runtime model that keeps memory use bounded during playback.

- Shipped cut and cross-fade transitions, reshuffled random playback, pause/stop behavior, Escape hold-to-close, pointer interrupt handling, and configurable ignored-key filtering from `MantelviewConfig.json`.

- Added Linux IPC control support with optional `IpcSecret`, plus the runtime watchdog/stop handling needed for unattended slideshow operation.

- Included silently skipping behavior when playback hits unreadable images.

- Folder scans skip unreadable descendant directories.

# Changelog

## 2026-04-02 — First Public Release

- First public release of Mantelview.

- Delivered the core fullscreen slideshow experience for local image folders on Windows and Linux, with persisted launcher settings for folder selection, display timing, transition timing, shuffle, topmost, and e-ink mode.

- Added recursive image catalog loading for JPEG, PNG, and WebP inputs, magic-byte validation, and the two-bitmap preload/runtime model that keeps memory use bounded during playback.

- Shipped cut and cross-fade transitions, reshuffled random playback, pause/stop behavior, Escape hold-to-close, pointer interrupt handling, and configurable ignored-key filtering from `MantelviewConfig.json`.

- Added Linux IPC control support with optional `IpcSecret`, plus the runtime watchdog/stop handling needed for unattended slideshow operation.

- Included silently skipping behavior when playback hits unreadable images.

- Folder scans skip unreadable descendant directories.

- 

# Resource Footprint

Measured resource usage for Mantelview during continuous slideshow playback. These numbers can help you decide whether your hardware is a good fit, especially for low-power or always-on setups.

## Test Conditions

- **Medium images:** 120 mixed JPEG/PNG files, ~1080p resolution
- **Large images:** 120 mixed JPEG/PNG files, ~4K resolution
- **Holdout (idle soak):** 2 medium images with a 30-second display time, 10 cycles (~10 minutes) — tests long-running idle behavior
- **Active cycling:** 1-second display time, 0.5-second transitions, 2 full catalog cycles (~6 minutes)
- **Transition effects:** Crossfade, Slide, Cover, Uncover (all enabled simultaneously)
- **Platform:** Windows 10 x64 (native, hardware-accelerated) and Linux x64 (WSL2 software rendering)

RSS (Resident Set Size) is the physical memory the process holds. "Steady-state" means the slideshow is past startup and cycling normally.

## Windows x64 (Native)

Measured on Windows 10 x64 with D3D hardware acceleration.

| Scenario                          | RAM (typical) | RAM (peak) | CPU (typical) | CPU (peak)                      |
| --------------------------------- | ------------- | ---------- | ------------- | ------------------------------- |
| 120 medium images, active cycling | ~230 MB       | ~295 MB    | ~7%           | ~20%                            |
| 120 large images, active cycling  | ~240 MB       | ~285 MB    | ~11%          | ~25%                            |
| 2 images, 30s hold (idle soak)    | ~200 MB       | ~220 MB    | ~0%           | brief spikes during transitions |

Between transitions, CPU drops to near zero. The process is effectively idle while an image is on screen.

## Linux x64 (WSL2, Software Rendering)

Measured on WSL2 with software rendering (no GPU passthrough). Native Linux with GPU acceleration should be closer to the Windows numbers.

| Scenario                          | RAM (typical) | RAM (peak) | CPU (typical) | CPU (peak) |
| --------------------------------- | ------------- | ---------- | ------------- | ---------- |
| 120 medium images, active cycling | ~215 MB       | ~260 MB    | ~42%          | ~50%       |
| 120 large images, active cycling  | ~240 MB       | ~270 MB    | ~46%          | ~52%       |
| 2 images, 30s hold (idle soak)    | ~235 MB       | ~280 MB    | ~3%           | ~50%       |

The elevated CPU during active cycling is a WSL2 software-rendering artifact. When the display is holding a static image, CPU drops around 3%, confirming the app itself is not doing unnecessary work.

## What This Means in Practice

- **RAM:** Expect roughly 200-300 MB regardless of image resolution or platform. 4K images use only modestly more than 1080p because Mantelview scales images to screen resolution before display.
- **CPU:** On hardware with GPU acceleration (Windows, native Linux with a GPU), CPU usage is low during playback and near-zero between transitions. Software rendering (WSL2, some VMs, headless-to-display setups) will show higher CPU.
- **Transition effects:** Enabling all five transition types adds negligible overhead compared to crossfade alone (~1-5 MB RAM, ~0-2% CPU).
- **Long-running stability:** No memory growth was detected across any test run. RSS trends are flat or slightly declining over time.
- **Startup:** The app is typically ready within 2-6 seconds.

## Raspberry Pi

No automated measurements for Raspberry Pi ARM64 yet. Expect higher RAM and CPU usage than the numbers above due to lower single-core performance and shared GPU memory. A Raspberry Pi 4 (4 GB+) with a desktop install should run the slideshow comfortably at 1080p.

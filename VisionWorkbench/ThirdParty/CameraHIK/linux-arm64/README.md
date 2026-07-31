## HikRobot Linux arm64 Runtime

This directory contains the Linux `aarch64` native runtime extracted from:

- `MvCamCtrlSDK_STD_V4.7.0_251113.zip`
- inner package: `MvCamCtrlSDK_Runtime-4.7.0_aarch64_20251113`

Included here are the native libraries and transport-layer files needed for Jetson-side validation.

Notes:

- The existing `../MvCameraControl.Net.dll` remains Windows-only and is intended for Desktop use.
- Jetson should load the native `.so` files from this directory via `LD_LIBRARY_PATH`.
- The vendor installer also modifies system environment variables, `udev`, and some network settings.
  We are intentionally not applying those changes here yet.
- The vendor package also contains `ThirdParty/libavutil.so.60.8.100` and `ThirdParty/libswscale.so.9.1.100`.
  Those are intentionally not vendored here to avoid conflicting with the project's FFmpeg runtime.

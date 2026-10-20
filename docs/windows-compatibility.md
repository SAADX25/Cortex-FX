# Windows compatibility and radial transparency

The app remains a .NET 10 WPF application, published self-contained for `win-x64`. Publishing includes its .NET runtime; Word, OCR, LibreOffice, Calibre, and 7-Zip remain separate optional conversion dependencies.

## Rendering behavior

- The radial wheel uses a layered, per-pixel-transparent window. Native tool-dialog backdrops are never applied to it.
- The default WPF rectangular focus adorner is disabled only on the wheel and its drawing surface. Arrow/Tab/Enter navigation and the highlighted segment still identify the selected action. Working, success, and error states retain transparent outer pixels.
- Crop and compression dialogs share the same backdrop policy. Windows builds 22621 and newer may use native desktop acrylic when high-contrast mode is off. Older Windows builds and unavailable compositor effects use an opaque client surface and turn off extended glass.
- Windows 10 uses the opaque dialog fallback. The conversion engines and wheel do not depend on acrylic or Windows 11 rounded-corner APIs.

## Validation scope

The current machine runs Windows 11 build 26300. The full automated suite and published application probes run on this machine. Compatibility tests cover Windows 10 builds 17763/19045, Windows 11 builds 22000/22620/22621/26100/26300, high contrast, actual fallback brushes/chrome, and transparency during keyboard-triggered conversion/result states.

Those build-policy and fallback tests are not an end-to-end run on Windows 10. A Windows 10 machine/VM was not available in this session, so complete runtime compatibility on Windows 10 has not been certified.

Microsoft's current .NET 10 support matrix lists specific Windows 10 Enterprise/IoT/LTSC releases, and supported Windows 11 releases; this is distinct from an application's technical ability to launch on another Windows edition. Do not describe every Windows 10 edition/build as vendor-supported or tested.

## Windows 10 acceptance run

On the intended Windows 10 x64 installation, launch the complete published folder and check:

1. Main window, settings, minimize/restore, tray exit, and launch from the installed shortcut.
2. Explorer drag with Shift/Alt/Ctrl; reject folders, executable conversion inputs, invalid data, and mixed unsupported selections, then accept a supported file immediately.
3. Keyboard selection, start/cancel/complete/error, transparent wheel edges, modal crop/compression, and multiple display scales.
4. Image conversion and compression/crop, PDF image export, audio/video conversion with bundled FFmpeg, and available Office conversions with the corresponding Office applications installed.
5. Crop/compression fields, percentage/maximum size, KB/MB, PDF rasterization opt-in, and an opaque background without black borders when native acrylic is unavailable.
6. Originals remain unchanged, output names do not overwrite existing files, and output byte limits are enforced.

Primary references:

- [Microsoft: focus visual styles and the default dashed rectangle](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/styling-for-focus-in-controls-and-focusvisualstyle)
- [Microsoft: desktop backdrop API requirements](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)
- [Microsoft: .NET 10 supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
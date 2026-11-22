# Windows unified diagnostic prototype

This standalone WPF project targets `net8.0-windows`. One EXE supports `--mode host` (default), `--mode viewer`, and UI role switching. It has no Core dependency and contains no copied reference-project code.

## Implemented

- Host/Viewer role UI with an explicit **no streaming implemented** status.
- Hardware DXGI adapter/output enumeration with desktop coordinates, dimensions and rotation.
- Host-only single-frame Desktop Duplication diagnostic: creates a D3D11 device on the selected adapter, acquires one desktop texture, reports its dimensions/format and actual dirty/move rectangle counts, releases texture/resource/frame/duplication/device/context/output/adapter/factory.
- A 1500ms acquisition timeout; missing hardware output and DXGI failures remain errors with HRESULT, never a fabricated successful frame.
- CLI JSON diagnostic mode for repeatable smoke checks; exit 0 = diagnostic completed, 1 = diagnostic failed, 2 = invalid arguments, 3 = report write failed.
- Input adapter contract only: no SendInput implementation and no input injection calls.

## Commands

```powershell
dotnet build src/ScreenCast.Windows/ScreenCast.Windows.csproj -c Release
dotnet format src/ScreenCast.Windows/ScreenCast.Windows.csproj --verify-no-changes
dotnet publish src/ScreenCast.Windows/ScreenCast.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o src/ScreenCast.Windows/publish
./src/ScreenCast.Windows/publish/ScreenCast.Windows.exe --mode host
./src/ScreenCast.Windows/publish/ScreenCast.Windows.exe --mode viewer
./src/ScreenCast.Windows/publish/ScreenCast.Windows.exe --mode host --diagnostics ./capture-result.json --capture-once
```

The diagnostic file path is supplied by the operator. Omitting `--capture-once` only enumerates local displays. Viewer mode never connects to a remote endpoint in this prototype. No capture texture or pixel data is saved or transmitted.

## Dependency/API verification

NuGet `Vortice.Direct3D11` and `Vortice.DXGI` are pinned to **3.6.2**, which supplies .NET 8 assemblies. Restore and compilation verified the actual package API: `EnumAdapters1`, `EnumOutputs`, `D3D11CreateDevice`, `IDXGIOutput1.DuplicateOutput`, `AcquireNextFrame`, `GetFrameDirtyRects`, `GetFrameMoveRects`, and `ReleaseFrame`. Rectangle buffer sizes are bytes, calculated using the package struct sizes; the diagnostic bounds metadata allocation at 16 MiB.

## Local verification (2026-11-21)

- Windows, .NET SDK **10.0.401**, targeting **net8.0-windows**: Release build succeeded with **0 warnings / 0 errors**.
- `dotnet format ... --verify-no-changes --no-restore`: exit **0**.
- Self-contained `win-x64` singlefile publish: exit **0**, output contains only `ScreenCast.Windows.exe` (162,777,359 bytes at verification time). Native components may be extracted by the .NET singlefile runtime.
- Published EXE `--diagnostics ... --capture-once`: exit **0** on NVIDIA GeForce RTX 5060, `\\.\DISPLAY1`, **2560×1440**, **B8G8R8A8_UNorm**, accumulated frames **1**, dirty rects **1**, move rects **0**. JSON explicitly records `streamingImplemented: false`.
- Host and Viewer GUI launches both produced responsive windows titled `ScreenCast — Windows 诊断原型`, and both closed normally with `CloseMainWindow`; button interaction was not automated.
- These observations validate local hardware enumeration and one acquired frame; zero move rectangles is an observed result, not a test of actual move-region behavior.

## Remaining work

Continuous capture, GPU desktop cache, frame preview, hardware encode/decode, remote rendering, signaling/identity/key handshake, STUN/TURN/P2P, authenticated input leases/injection, WGC/GDI fallback, DPI coordinate transforms, automatic access-lost recovery, and end-to-end streaming are unimplemented. Multi-monitor/hotplug, headless/no-GPU and protected desktop failure paths need additional hardware validation. The GitHub workflow is configured for Windows Server 2022 with SDK 8; its hosted run is separate from the local SDK 10 result.

# ScreenCast 项目指南

> 首次审查：2026-11-21。本文在新架构设计及实现之前创建。项目当前是参考资料工作区，尚无自己的可运行串流软件；以下明确区分现有资产与目标技术栈。

## 项目定位

开发自托管、优先点对点连接的远程桌面串流软件。每个平台提供统一 Host / Viewer 客户端，通过界面或启动参数切换角色。Host 采集和编码画面，Viewer 解码、显示并回传输入。独立服务器负责信令、STUN 地址发现及 TURN 中继，部署硬约束是 **Windows Server 2022**，服务器不处理视频编解码。

首个产品验收目标：两台 Windows PC 完成 DDA 采集、H.264 编码、P2P 建链、视频显示及鼠标键盘控制。TURN、移动端和硬件矩阵按后续里程碑验收，不以界面占位或构建成功代表功能完成。

## 审查范围与方法

递归枚举全部 13,953 个现有文件，包括隐藏文件、源码、构建配置、文档、资源、二进制和压缩包；检查 12 个备份 ZIP 的目录表；按工程阅读入口文档、依赖声明和关键机制源码。大体量 FFmpeg 使用目录/类型统计与采集、编码、硬件上下文、UDP 代码抽样；未逐行阅读全部文件，未反编译已有二进制，未执行参考程序。新产物不包含参考源码的直接复制。

原始工作区没有根级 Git 元数据、自己的解决方案、构建配置或自动测试。工具发现：本机 .NET SDK 10.0.401，已装运行时 6/10；PATH 中没有 Rust/Cargo；有 Java 和 Git。运行 .NET 8 项目需要另备 SDK/运行时或发布自包含程序。

## 现有目录与资产

| 目录 | 文件数 | 内容和结论 |
| --- | ---: | --- |
| `Client for Windows/` | 0 | 预留 Windows 输出目录 |
| `Client for Android/` | 0 | 预留 Android 输出目录 |
| `Client for Mac-arm/`、`Client for Mac-x64/` | 0 | 预留 macOS 目录；不等同于 iOS 工程 |
| `Server Software/`、`temp/` | 0 | 预留服务器输出与临时目录 |
| `p2p Server Info/` | 1 | `info.txt` 包含部署地址、Windows Server 2022、4 核/4GB 描述；不复制地址到公共配置 |
| `preference/backup/` | 12 | 12 份 ZIP 参考源码备份，约 208MB |
| `preference/FFmpeg-master/` | 11,303 | C/汇编媒体库，含测试样本、DDA、硬编、硬件帧上下文 |
| `preference/Sunshine-master/` | 619 | C++ 远程串流 Host，DDA/WGC、NVENC/AMF/QSV、输入与传输 |
| `preference/moonlight-qt-master/` | 310 | Qt/C++ Viewer，FFmpeg、D3D11VA 渲染与能力检测 |
| `preference/moonlight-android-master/` | 500 | Java/JNI Android Viewer，MediaCodec 与输入处理 |
| `preference/moonlight-ios-master/` | 556 | Objective-C 为主的 iOS/tvOS Viewer，VideoToolbox 与平台输入 |
| `preference/p2p-main/` | 90 | .NET Framework 4.8 WinForms 示例，UDT、旧 STUN、JPEG 区域更新 |
| `preference/P2pChat-master/` | 16 | Java UDP 节点发现与同一 socket 对等通信示例，含 class 文件 |
| `preference/p2p_Net-master/` | 12 | Boost.Asio TCP seed/peer 示例；不是 UDP/STUN/TURN 实现 |
| `preference/Wireboy.Socket.P2PSocket-dev_4.0/` | 26 | README、图片和引用缺失 csproj 的 sln；没有 README 所述新版实现 |
| `preference/DeskPad-main/` | 45 | Swift macOS 虚拟显示与本地显示，不是远程传输核心 |
| `preference/virtual-display-rs-master/` | 166 | Rust Windows IddCx 虚拟显示驱动及控制工具 |
| `preference/VQEngine-master/` | 297 | C++/D3D12 渲染引擎和素材，非远程桌面工程 |

参考代码快照缺少多个 Git 子模块：Sunshine 的 third-party、Moonlight 的 moonlight-common-c 等，VQEngine 也声明外部依赖。ZIP 快照没有足够信息保证恢复原始精确子模块提交；不能把这些目录视作已验证可复现构建。

## 目标技术栈

| 部件 | 选型 | 说明 |
| --- | --- | --- |
| Windows 服务器 | C#、.NET 8、异步 Socket、Windows Service | 三个独立逻辑子系统，原生 Windows 部署，不依赖 Linux coturn/WSL |
| Windows 统一客户端 | C#、.NET 8、WPF、Vortice DXGI/D3D11 | DDA 为主、WGC 单窗口回退、GDI 兼容；SendInput |
| Windows 媒体后端 | NVENC / AMF / QSV，D3D11 硬件解码 | 通过经验证的原生绑定/媒体适配器接入；AV1 双端能力协商，H.264 保底 |
| Android | Rust 核心 `.so` + Java UI | MediaProjection、MediaCodec、Surface；GitHub Actions 打 APK |
| iOS | Swift + Rust `.xcframework` | ReplayKit、VideoToolbox；macOS runner 归档、配置签名后导出 IPA |
| 协议 | STUN RFC5389、TURN RFC5766、自定义 UDP 应用协议 | NAT 行为诊断补充 RFC5780；应用负载 AES-GCM |
| 验证 | .NET 单元/集成测试、Rust 测试、跨语言协议向量 | 网络仿真与实际 GPU/公网 NAT 测试分别记录 |

Windows 和 Rust 核心共享线协议与测试向量，而不是强制 Windows 通过 Rust 完成全部网络处理。iOS/Android 的硬件编解码由平台 API 执行，Rust 负责编排、传输和帧封装。

## 从参考工程学习的机制

- `p2p-main/p2pconn/Form1.cs` 在 STUN 后将同一 UDP socket 交给 UDT，值得保留端口生命周期思想。其 `StunClientEndPoint/StunClient.cs` 是 RFC3489 风格，16 字节事务号、旧属性及 NAT 分类需要重新设计。`StunServer/StunServer.cs` 只是地址列表工具。
- `p2p-main/p2pconn/StreamingLibrary/UnsafeStreamCodec.cs` 对比像素、合并变化矩形、JPEG 小块更新。可学习变化区域管理，不直接替代 H.264/AV1 参考帧编码。
- `P2pChat-master/src/io/itit/p2p/` 用同一 DatagramSocket 注册、获取对端并周期通信；缺少身份验证、可靠控制、会话清理及 TURN。
- Sunshine 的 `src/platform/windows/display_base.cpp`、`display_wgc.cpp` 展示采集资源生命周期和失效恢复；`src/video.cpp` 展示编码能力选择；`src/stream.cpp` 分视频、音频与控制，并使用 ENet 控制协议。不是本产品双 UDP 协议的现成实现。
- FFmpeg 的 `libavfilter/vsrc_ddagrab.c`、`libavcodec/nvenc.c`、`amfenc.c`、`libavutil/hwcontext_qsv.c` 可学习 GPU 纹理传递和硬编后端。Sunshine/FFmpeg 审查未发现直接使用 GetFrameDirtyRects/GetFrameMoveRects 的实现。
- Moonlight 平台端可学习解码能力探测、帧排队、硬件 Surface 生命周期及输入坐标；本地缺失的 common-c 不能作为已读源码证据。
- DeskPad/virtual-display-rs 为后续虚拟屏扩展参考，MVP 无需驱动。

## 构建说明

### 当前快照

原始根目录没有可构建的 ScreenCast 产品。请勿尝试把 preference 中的 sln 当作本项目解决方案。后续自己的工程统一放 `src/`、`tests/`、`apps/`、`native/`，产物放 `artifacts/`。初始审查完成后已建立下述基础工程；当前实际功能与验收边界见[验证记录](docs/validation.md)。

### 目标开发环境

1. Windows 10/11 客户端开发机：.NET 8 SDK、Windows SDK，具备 D3D11 和对应硬编驱动；VS2022 可选。
2. Windows Server 2022：运行自包含 win-x64 服务端，开放配置的 UDP 监听与 TURN 中继端口范围；完整 NAT 分类需要两公网 IP、两端口。
3. Android：JDK17、Android SDK/NDK、Gradle、Rust Android targets；使用项目锁定版本，不套用参考仓库最新版本号。
4. iOS：macOS、Xcode、Rust iOS targets；归档与 TestFlight 导出需要 Apple Team、Bundle ID、签名证书和 provisioning profile。

### 参考工程自身构建（仅供理解）

- Sunshine：C++23/CMake≥3.24，Windows MSYS2 UCRT64；该子树的 AGENTS.md 要求命令以 `C:\msys64\msys2_shell.cmd -defterm -here -no-start -ucrt64 -c` 前缀运行，构建目录以 `cmake-build-` 开头。依赖快照尚需恢复。
- Moonlight Qt：当前 README 要求 Qt6.11+、VS2026/MSVC，需子模块；不是目标 Windows C# 构建链。
- FFmpeg：configure/make，Windows 通常使用适配的 MSYS2/MSVC 工具链；按所需编解码器配置。
- 老 P2P：p2p-main 使用 .NET Framework4.8；P2pChat 是旧 Gradle/Java 示例；p2p_Net 使用 CMake/Boost.Asio。
- virtual-display-rs：VS2022、SDK/WDK、Rust/cargo-make；驱动安装与签名单独处理。

## 开发注意事项

- 先读参考机制、记录证据，再以本项目接口重新实现；不把静态口令、旧加密、无限重试和同步阻塞接收循环迁入。
- STUN/TURN 标准报文保持原有 RFC 封装；AES-GCM 用于自定义应用负载和 TURN 中继的内层数据，不能给标准 Binding/Allocate 外加私有头后仍声称互通。
- 每个方向、通道、会话和密钥代次要独立 nonce 空间；认证成功才更新防重放窗口，重传规则需由协议明确。
- 每个媒体/控制 socket 分别发现和保持映射；同一 socket 生命周期内完成 STUN、打洞和应用流量，避免重建端口导致探测结果失效。
- DDA 脏区域是采集元信息；静止帧跳过与 ROI 属于第一阶段，任意局部图块视频更新需要额外协议和恢复机制。
- iOS ReplayKit 屏幕分享与系统级远程输入能力分开建模：公开 API 不提供任意跨应用输入注入；Android 也需区分普通应用、Accessibility 与特权输入。
- 本地输入独占使用可恢复的控制租约与本机紧急释放；断线释放所有按键/鼠标状态。
- 日志记录状态/时延/丢包/后端错误，排除密钥、令牌和输入内容；支持分级与导出。
- MIT/GPL/LGPL 参考工程混合存在：保留机制来源台账；依赖二进制或代码引入时按具体组件记录许可与版本。FFmpeg 默认 LGPL2.1+，构建选项可能改变组合许可；Moonlight/Sunshine 含 GPL 组件。

## 新工程构建（审查完成后建立）

```powershell
# 安装.NET8 SDK的Windows开发机
 dotnet restore ScreenCast.sln
 dotnet build ScreenCast.sln -c Release
 dotnet run --project tests/ScreenCast.Core.Tests -c Release
 dotnet run --project tests/ScreenCast.Transport.Tests -c Release
 dotnet run --project src/ScreenCast.Server -c Release -- --self-test
 dotnet format ScreenCast.sln --verify-no-changes --no-restore --include src tests
 dotnet publish src/ScreenCast.Windows -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o artifacts/windows-client
 dotnet publish src/ScreenCast.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o artifacts/windows-server
```

本机仅.NET10运行时时测试命令可加 `--roll-forward Major`；这验证net8目标在10运行时的行为，CI仍使用.NET8。

Windows 客户端命令：

- `--mode host|viewer`：统一双角色界面。
- `--diagnostics <file> --capture-once`：单帧 DDA 诊断。
- `--media-smoke <dir>`：持续采集 + H.264 编码 + NAL 校验。
- `--session-smoke <dir>`：完整数据面——采集→编码→分片→AES-GCM→真实 UDP→重组→写 Annex-B 码流。
- `--live-smoke <dir> [--live-seconds N]`：**同进程实时会话**，Host 与 Viewer 都跑在生产会话类上，验证实时画面与输入租约。
- `--host-listen <videoPort> <controlPort> <peerVideoPort> <seconds> <dir>` / `--viewer-connect <host> <videoPort> <controlPort> <localVideoPort> <localControlPort> <seconds> <dir>`：**两个独立进程**的 Host/Viewer，用于跨机验证。
- `--decode-smoke <dir> [--decode-input <file.h264>]`：解码已录制码流并输出 BMP。
- `--decode-matrix <file.h264>` / `--decode-probe <file.h264>`：解码器输出类型与样本格式的诊断工具。
- `--media-seconds N` / `--media-frames N`：限制运行时长与帧数。

服务端命令：

- `--bind 0.0.0.0 --stun-port 3478 --signal-port 3479`：同时开 STUN 与信令。
- `--pairing-file pairings.json`：固定连接码表；同一被控设备始终使用同一个 9 位码，重启服务器后仍然有效。
- `--self-test`：用真实 UDP 客户端自检 11 项流程，部署机上可直接验证。
- `--help`：完整参数说明（租约、上限、限流、扫描间隔）。

当前能力边界：STUN Binding 与 UDP 信令会话完整可用；**Windows 到 Windows 的实时远程桌面已完整可用**（实时画面 + 键鼠控制，同进程与两进程均实测通过）；**Rust 跨平台网络核心已完成**（102 项测试 + 40 项 ABI 检查通过）；**Android 控制端（Viewer）已完成并重写为 UU 风格界面**（GitHub Actions 产出带原生库的 APK）；**MacBook 客户端（arm64 + Intel x64）已在 GitHub Actions 编译通过并产出 App，但尚未在真机运行界面**。

客户端信令地址固定为 `jsspace.xyz:3479`；配对一次后连接码长期有效，三端都会记住上次成功配对的码。

未实现：TURN 中继、真实公网打洞、硬件 NVENC MFT 事件驱动、D3D11 零拷贝、音频、多 Viewer、自适应码率。

从 FFmpeg 提取的解码/呈现机制记录在 [docs/ffmpeg-extraction.md](docs/ffmpeg-extraction.md)：解码纹理创建与绑定标志、GPU 回读三步（含 `RowPitch` 与 opaque 格式限制）、`E_PENDING` 有界重试、coded size 而非显示尺寸、NV12→RGB 定点系数。FFmpeg 代码本身未进入本仓库。

移动构建入口为 `bash scripts/build-android.sh all` 与 `bash scripts/build-ios.sh all`，工具链与签名配置见各平台README。GitHub Actions提供windows/mobile流水线，`v*` tag触发构建并生成草稿Release；iOS未配置完整签名时仅输出无签名archive/XCFramework，有完整配置才导出IPA，不自动上传App Store Connect。

新目录：`src/ScreenCast.Core`、`src/ScreenCast.Server`、`src/ScreenCast.Windows`、`tests/ScreenCast.Core.Tests`、`native`、`apps/android`、`apps/ios`、`scripts`、`.github/workflows`、`docs`。完整原始参考索引见[reference-inventory.csv](docs/reference-inventory.csv)。

## 文档演进

本文件首先完成资产审查与开发入口说明；之后新增 `docs/` 的架构、接口、协议、状态机、路线图和验证记录。每个里程碑分别标识“已实现”“已本地测试”“需硬件/公网验收”，不将规划写成已完成功能。

# ScreenCast macOS 客户端（MacBook，双角色）

自研 P2P 串流产品的 macOS 端。一个窗口、两个角色：

* **Viewer（控制）**：解码对端画面并显示，把 Mac 的鼠标键盘转发成 ScreenCast 控制事件。
* **Host（被控）**：用 ScreenCaptureKit 采集本机屏幕，VideoToolbox 编 H.264 发出去，把收到的
  控制事件用 CGEvent 注入（需要**辅助功能权限**）。

网络层不是重新实现的：直接用 `native/` 的 Rust 静态库，通过 `native/include/screencast_core.h`
的 14 个 C 函数驱动。协议逐字节由 Rust 核心保证与 Windows 端一致。

> **状态：界面已按 UU 远程风格重写；GitHub Actions 已编译通过 x64 与 universal 两个 App。**
> 还没有在真实 macOS 桌面上运行过界面；见文末「未验证声明」。

---

## 1. 目录结构

```
apps/macos/
├── project.yml                     XcodeGen 工程定义（macOS 13.0+）
├── Info.plist                      含 NSScreenCaptureUsageDescription
├── Sources/
│   ├── Support/ScreenCastBridge.h  bridging header → screencast_core.h
│   ├── App/
│   │   ├── ScreenCastMacApp.swift  @main、侧栏工作台、全窗口会话层
│   │   ├── Theme.swift             暗色 + 绿色设计令牌、卡片/徽章/按钮/输入框
│   │   ├── DeviceBook.swift        本机设备簿 + 9 位连接码（UserDefaults）
│   │   ├── SettingsPanel.swift     设置页：版本、安全说明、键位表、本地数据
│   │   └── SharedViews.swift       NSViewRepresentable、连接码输入、统计表格、权限行
│   ├── Core/
│   │   ├── CoreDefaults.swift      默认密钥 / sessionId / 端口 / 标志位
│   │   ├── CoreSession.swift       14 个 C 函数的 Swift 封装
│   │   ├── ControlWire.swift       控制事件体线格式（与 InputCodec.cs 逐字节一致）
│   │   ├── KeyMap.swift            PC 扫描码 ↔ macOS keyCode 双向表
│   │   └── Support.swift           锁、计数器、地址解析、格式化
│   ├── Viewer/
│   │   ├── AnnexB.swift            Annex-B 分 NAL / AVCC 互转
│   │   ├── H264Decoder.swift       VTDecompressionSession 解码
│   │   ├── RemoteSurfaceView.swift AVSampleBufferDisplayLayer + 输入采集
│   │   ├── ViewerSession.swift     接收循环、统计、关键帧恢复
│   │   └── ViewerPanel.swift       设备页 / 远程协助页 / 会话悬浮控制条
│   └── Host/
│       ├── ScreenCaptureSource.swift  SCShareableContent / SCStream
│       ├── H264Encoder.swift          VTCompressionSession + AVCC→Annex-B
│       ├── MacInputInjector.swift     CGEvent 注入 + 辅助功能检查
│       ├── HostSession.swift          采集→编码→发送 / 收控制→注入
│       └── HostPanel.swift            被控页：共享开关、端口、画面编码、权限、统计
└── .gitignore
```

构建脚本：`scripts/build-macos.sh`。

---

## 2. Rust 静态库接入

`scripts/build-macos.sh framework`：

1. `cargo build --release --target aarch64-apple-darwin`
2. `cargo build --release --target x86_64-apple-darwin`
3. `lipo -create` 两个 `libscreencast_core.a` → `build/universal/libscreencast_core.a`
4. `xcodebuild -create-xcframework -library <universal> -headers native/include`
   → `apps/macos/Frameworks/ScreenCastCore.xcframework`
5. `xcodegen generate --spec apps/macos/project.yml --project apps/macos`

Swift 侧通过 **bridging header** 引入头文件：

```objc
// Sources/Support/ScreenCastBridge.h
#include "screencast_core.h"
```

头文件搜索路径是 `$(SRCROOT)/../../native/include`，所以 Swift 看到的头与
`native/tools/header_smoke.c` 编译的是同一份字节，而不是 xcframework 里可能过期的副本。

App 直接链接 `$(SRCROOT)/build/universal/libscreencast_core.a`（`LIBRARY_SEARCH_PATHS` +
`-lscreencast_core`），**不把 xcframework 作为 target dependency**。原因是 xcframework 与
`native/include/module.modulemap` 会各自声明一个 `ScreenCastCore` Clang module，同时存在会让
bridging header 的 PCH 报 `redefinition of module 'ScreenCastCore'`。xcframework 仍然由
`framework` 生成，供 `verify` 检查布局与给其他客户端复用。

链接参数：`-lscreencast_core -liconv -lresolv -framework CoreFoundation -framework Security`
（Rust std 在 Apple 平台引用到的系统库）。`native/Cargo.toml` 的 `staticlib` 产物名是
`libscreencast_core.a`。

### C ABI 调用点（14 个函数全部用到）

| 函数 | 调用点 | 用途 |
|---|---|---|
| `sc_core_version` | `CoreSession.swift: CoreVersion.text` | 标题栏显示核心版本 |
| `sc_get_role` | 未调用（角色由 UI 决定，见下） | — |
| `sc_set_role` | `ScreenCastMacApp.swift: RootView` | 角色切换时同步进程内角色 |
| `sc_session_create` | `CoreSession.init` | 32 字节 secret + 16 字节 sessionId + epoch 1 + is_host |
| `sc_session_bind` | `CoreSession.bind` | 绑定 0.0.0.0 的视频/控制 socket |
| `sc_session_set_peer` | `CoreSession.setPeer` | 视频/控制各一个对端（只接受 IP 字面量） |
| `sc_session_start` | `CoreSession.start` | 起 Rust 的收发线程 |
| `sc_session_send_frame` | `HostSession.send` | 每帧 Annex-B + frameId + 采集时间戳 + flags + codec |
| `sc_session_send_control` | `ViewerSession` / `HostSession` | 10 种控制事件 |
| `sc_session_poll` | `ViewerSession.pollLoop` / `HostSession.pollLoop` | 非阻塞取事件 |
| `sc_session_stats` | 两侧 `refreshStats` | 丢帧、拒绝包、RTT、路径状态 |
| `sc_session_local_ports` | `CoreSession.bind` | 读实际绑定的端口 |
| `sc_frame_release` | `CoreSession.poll` | 拷完 payload 立刻释放原生缓冲 |
| `sc_session_destroy` | `CoreSession.close` / `deinit` | 停线程释放会话 |

`sc_get_role` 没有调用：`native` 的角色只是进程内 UI 状态，真正决定行为的是
`sc_session_create(..., is_host:)`，所以这里不做一次无意义的往返。`sc_set_role` 仍然调用，保持与
Windows 端一致的角色记录。

### payload 生命周期

头文件写明 `sc_event_t.data` 指向会话内存，**下一次返回 body 的 poll 或 `sc_frame_release`
之后就失效**。`CoreSession.poll` 在返回前立刻 `Array(UnsafeBufferPointer(...))` 拷贝，然后
`sc_frame_release`，所以 Swift 层拿到的是独立数组，不会有悬垂指针。

### 对端地址必须手填的原因

`sc_session_set_peer` 只接受 IPv4/IPv6 **字面量**（Rust 侧 `IpAddr::from_str`，不做 DNS）。
`HostResolver.literal(for:)` 先用 `inet_pton` 判断是否已是字面量，不是才用 `getaddrinfo` 解析。

C ABI 没有「窥视首个数据报源地址」的接口（Windows 的 `WaitForPeerSources` 是裸 socket 才能做
的事），所以 **Host 端必须手填控制端（Viewer）的地址与端口**；Viewer 界面上会显示它实际绑定的
视频/控制端口，填过去即可。这是本 build 的已知取舍，不是遗漏。

---

## 3. Viewer（控制端）

### 3.1 解码链路

```
Rust poll → SC_EVENT_VIDEO_FRAME (Annex-B)
  → AnnexB.split 分出 NAL
  → SPS(7)/PPS(8) → CMVideoFormatDescriptionCreateFromH264ParameterSets(nalUnitHeaderLength: 4)
  → 其余 NAL 改写成 AVCC（4 字节大端长度前缀）
  → CMSampleBuffer → VTDecompressionSessionDecodeFrame
  → CVPixelBuffer → CMSampleBufferCreateReadyWithImageBuffer + DisplayImmediately
  → AVSampleBufferDisplayLayer.enqueue
```

要点：

* **Annex-B → AVCC**：VideoToolbox 只吃 AVCC。`AnnexB.split` 同时认 3 字节与 4 字节起始码，
  并把 NAL 尾部的 0x00 剪掉（4 字节起始码的前导 0 会落在上一个 NAL 末尾）。
* **参数集变化就重建会话**：`VTDecompressionSession` 绑定一个 format description，SPS/PPS 变了
  必须 `VTDecompressionSessionWaitForAsynchronousFrames` + `Invalidate` 后重建。
* **像素格式**：向解码器要 `kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange`（NV12，硬件解码
  的原生输出，`AVSampleBufferDisplayLayer` 直接能吃）。要换 BGRA 只改 `H264Decoder` 里那一个常量。
* **渲染**：`AVSampleBufferDisplayLayer` 比 `MTKView`+Metal 简单可靠，缩放/色彩/vsync 都由它做。
  `kCMSampleAttachmentKey_DisplayImmediately` 让它入队即显示，不需要给 layer 设 timebase。

### 3.2 输入转发

`RemoteSurfaceView`（一个 NSView 同时管画面与输入）：

| AppKit 回调 | 发出的控制事件 |
|---|---|
| `mouseMoved` / `mouseDragged` | `MouseMove` |
| `mouseDown` / `mouseUp` | `MouseButton(Left)` |
| `rightMouseDown` / `rightMouseUp` | `MouseButton(Right)` |
| `otherMouseDown` / `otherMouseUp` | `MouseButton(Middle)` |
| `scrollWheel` | `Wheel` |
| `keyDown` / `keyUp` | `Key` |
| `flagsChanged` | `Key`（修饰键） |

**坐标归一化**：先按 `pictureRect`（aspect-fit 之后的画面矩形）反算到画面像素，再
`ratio = pos / (extent - 1)` → `round(ratio * 65535)`，与 `InputCodec.Normalize` 同一套取整。
视图 `isFlipped = true`，所以 y 轴天然与 Windows 桌面同向，不用再翻。点在黑边（letterbox）里
不会算成画面上的某个位置。

**滚轮**：线格式是 Windows 的 120/格。触控板（`hasPreciseScrollingDeltas`）按 ×8 折算，
普通滚轮按 ×120 折算，再 clamp 到 Int16。

**键盘**：见第 5 节。`event.isARepeat` 直接丢弃（Windows 会自己产生自动重复，转发会翻倍）；
带 `Command` 的组合键交给本机（`Cmd+Q`/`Cmd+W` 不应该在远端按下一个键）。

### 3.3 统计面板

分辨率、帧率、解码/渲染帧数、丢帧（原生丢帧 + 解码器丢帧）、拒绝包、重组帧、接收字节、
解码耗时、视频/控制 RTT、路径状态、本机端口。每 0.5 s 刷新。

丢帧（原生 `dropped_frames` 增加）会触发 `RequestKeyFrame`，限 500 ms 一次 —— 与
`ViewerSession.cs` 的限流一致。解码失败同样触发。

---

## 4. Host（被控端）

### 4.1 采集

`ScreenCaptureSource`：

```
SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
  → SCDisplay 列表（UI 里选）
  → SCContentFilter(display:excludingWindows: [])
  → SCStreamConfiguration(width, height, minimumFrameInterval = 1/fps,
                          pixelFormat = 32BGRA, queueDepth = 6, showsCursor = true,
                          scalesToFit = true)
  → SCStream + addStreamOutput(.screen) → CMSampleBuffer
```

`SCFrameStatus != .complete` 的帧（画面没变化）直接丢弃，等价于 Windows 端的「静态帧跳过」，
省码率。

编码尺寸 = 采集尺寸（`ScreenCaptureSource.targetSize`：保持宽高比、按最大宽高缩放、两个维度
都取偶数，因为 H.264 4:2:0 要求偶数）。

### 4.2 编码

`VTCompressionSession`：

```
kVTCompressionPropertyKey_RealTime                        = true
kVTCompressionPropertyKey_AllowFrameReordering            = false   （不要 B 帧）
kVTCompressionPropertyKey_ProfileLevel                    = H264_High_AutoLevel
kVTCompressionPropertyKey_AverageBitRate                  = 码率
kVTCompressionPropertyKey_ExpectedFrameRate               = fps
kVTCompressionPropertyKey_MaxKeyFrameInterval             = fps * 2
kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration     = 2
kVTCompressionPropertyKey_AllowOpenGOP                    = false
```

强制关键帧：`kVTEncodeFrameOptionKey_ForceKeyFrame`。首帧、收到 `RequestKeyFrame`、
`sc_session_send_frame` 之前都保证是 IDR。

**AVCC → Annex-B**：`H264Encoder.annexB(from:)` 从 `CMBlockBuffer` 按 format description 里的
NAL 长度前缀（通常 4）拆开，逐个加 `00 00 00 01`；**每个 IDR 前面补上 format description 里的
SPS/PPS**。这一点是互操作的关键：Windows 端 `H264Decoder` 靠访问单元里的 SPS/PPS 建
format description，关键帧不带参数集就解不出来（Windows 的
`H264Bitstream.NormalizeToAnnexB` 也是同样的做法）。

### 4.3 输入注入

`MacInputInjector`：

* **权限**：`AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": ...])`。
  `isTrusted == false` 时所有注入方法直接返回，不假装成功。UI 明确显示「辅助功能：未授权」并给
  「打开系统设置」按钮（`x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility`）。
* **鼠标移动**：`CGEvent(mouseEventSource:mouseType:.mouseMoved:mouseCursorPosition:mouseButton:)`，
  归一化坐标 → `CGDisplayBounds` 的全局点坐标（`CGDisplayBounds` 是点不是像素，正好是 CGEvent 的
  坐标空间）。
* **按键**：`CGEvent(keyboardEventSource:virtualKey:keyDown:)`，`virtualKey` 是 **macOS 虚拟键码**，
  由 `KeyMap.binding(forScanCode:extended:)` 从 PC 扫描码反查（与 Viewer 的表互为反向，同一份表）。
* **滚轮**：`CGEvent(scrollWheelEvent2Source:units:.line:wheelCount:2:wheel1:wheel2:wheel3:)`，
  `delta / 120` 折算成格。
* **防回环**：每个注入事件都 `setIntegerValueField(.eventSourceUserData, value: 0x53434D43)`
  （ASCII "SCMC"）。将来加 event tap 或采集过滤时按这个标记丢弃自己发的事件。
* **租约**：只有 `ControlLease(1)` 之后才注入，`ControlLease(0)` / 会话停止 / `ReleaseAll` /
  `SC_EVENT_CONTROL_EXPIRED` 都会把按住的所有键和按钮弹起来。断线绝不能留下一个卡住的修饰键。

### 4.4 端口与握手顺序

```
sc_session_create(is_host: true)
sc_session_bind(video: 42000, control: 42001)      // 返回实际端口
sc_session_set_peer(viewer_ip, 42100, viewer_ip, 42101)
sc_session_start()
```

与 Windows 端 `HostSession.StartCore` 的顺序一致（Create → SetPeers → Start）。

---

## 5. 键盘映射表（PC Set 1 扫描码 ↔ macOS keyCode）

`extended` 是 E0 前缀，参与身份判定：PC Set 1 用同一个字节表示两个键，只有前缀能区分
（Left/Right Control、Numpad7/Home、Numpad/、KP Enter 等）。所以双向查表都用
`(scanCode, extended)` 复合键。

| 键 | macOS keyCode | PC 扫描码 | E0 |
|---|---|---|---|
| A | 0x00 | 0x1E | |
| S | 0x01 | 0x1F | |
| D | 0x02 | 0x20 | |
| F | 0x03 | 0x21 | |
| H | 0x04 | 0x23 | |
| G | 0x05 | 0x22 | |
| Z | 0x06 | 0x2C | |
| X | 0x07 | 0x2D | |
| C | 0x08 | 0x2E | |
| V | 0x09 | 0x2F | |
| B | 0x0B | 0x30 | |
| Q | 0x0C | 0x10 | |
| W | 0x0D | 0x11 | |
| E | 0x0E | 0x12 | |
| R | 0x0F | 0x13 | |
| Y | 0x10 | 0x15 | |
| T | 0x11 | 0x14 | |
| 1 | 0x12 | 0x02 | |
| 2 | 0x13 | 0x03 | |
| 3 | 0x14 | 0x04 | |
| 4 | 0x15 | 0x05 | |
| 5 | 0x17 | 0x06 | |
| 6 | 0x16 | 0x07 | |
| = | 0x18 | 0x0D | |
| 9 | 0x19 | 0x0A | |
| 7 | 0x1A | 0x08 | |
| - | 0x1B | 0x0C | |
| 8 | 0x1C | 0x09 | |
| 0 | 0x1D | 0x0B | |
| ] | 0x1E | 0x1B | |
| O | 0x1F | 0x18 | |
| U | 0x20 | 0x16 | |
| [ | 0x21 | 0x1A | |
| I | 0x22 | 0x17 | |
| P | 0x23 | 0x19 | |
| Return | 0x24 | 0x1C | |
| L | 0x25 | 0x26 | |
| J | 0x26 | 0x24 | |
| ' | 0x27 | 0x28 | |
| K | 0x28 | 0x25 | |
| ; | 0x29 | 0x27 | |
| \ | 0x2A | 0x2B | |
| , | 0x2B | 0x33 | |
| / | 0x2C | 0x35 | |
| N | 0x2D | 0x31 | |
| M | 0x2E | 0x32 | |
| . | 0x2F | 0x34 | |
| Tab | 0x30 | 0x0F | |
| Space | 0x31 | 0x39 | |
| ` | 0x32 | 0x29 | |
| Backspace | 0x33 | 0x0E | |
| Esc | 0x35 | 0x01 | |
| Left Shift | 0x38 | 0x2A | |
| Caps Lock | 0x39 | 0x3A | |
| Left Option → Left Alt | 0x3A | 0x38 | |
| Left Control | 0x3B | 0x1D | |
| Right Shift | 0x3C | 0x36 | |
| Right Option → Right Alt | 0x3D | 0x38 | ✔ |
| Right Control | 0x3E | 0x1D | ✔ |
| F1 | 0x7A | 0x3B | |
| F2 | 0x78 | 0x3C | |
| F3 | 0x63 | 0x3D | |
| F4 | 0x76 | 0x3E | |
| F5 | 0x60 | 0x3F | |
| F6 | 0x61 | 0x40 | |
| F7 | 0x62 | 0x41 | |
| F8 | 0x64 | 0x42 | |
| F9 | 0x65 | 0x43 | |
| F10 | 0x6D | 0x44 | |
| F11 | 0x67 | 0x57 | |
| F12 | 0x6F | 0x58 | |
| Insert（Help 键） | 0x72 | 0x52 | ✔ |
| Home | 0x73 | 0x47 | ✔ |
| Page Up | 0x74 | 0x49 | ✔ |
| Forward Delete | 0x75 | 0x53 | ✔ |
| End | 0x77 | 0x4F | ✔ |
| Page Down | 0x79 | 0x51 | ✔ |
| Left | 0x7B | 0x4B | ✔ |
| Right | 0x7C | 0x4D | ✔ |
| Down | 0x7D | 0x50 | ✔ |
| Up | 0x7E | 0x48 | ✔ |
| KP 0..9 | 0x52–0x59, 0x5B, 0x5C | 0x52, 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49 | |
| KP . | 0x41 | 0x53 | |
| KP * | 0x43 | 0x37 | |
| KP + | 0x45 | 0x4E | |
| KP - | 0x4E | 0x4A | |
| KP / | 0x4B | 0x35 | ✔ |
| KP Enter | 0x4C | 0x1C | ✔ |

共 **97** 条（`KeyMap.bindings.count`，应用里「键位映射」可展开查看）。`macKeyCode` 与
`(scanCode, extended)` 两个方向都无重复（构建脚本外的一次性检查：
`KeyBinding(macKeyCode` 出现 97 次，`mac` 分组与 `scan/ext` 分组均无 `Count > 1`）。

**故意不在表里的键**（`binding` 返回 nil，直接不发）：

* `Command`(0x37/0x36)、`Fn`(0x3F)：PC 没有对应键。发 Windows 键会打开开始菜单，比不发更糟。
* `F13`–`F20`：PC Set 1 没有单字节扫描码。
* 音量/亮度/媒体键：`CGEvent` 能用但 PC 端没有扫描码语义。
* `ISO Section`(0x0A)、`KP =`(0x51)、`KP Clear`(0x47)：PC 无对应。
* 任何 `flagsChanged` 之外的非修饰键、以及 `isARepeat` 事件。

**左/右修饰键的区分**：`flagsChanged` 只带 `modifierFlags`，左右 Shift 同时按下时它只说「shift
是按下」。所以视图自己维护 `pressedModifiers: Set<UInt16>` 做状态翻转，左右互不干扰。

---

## 6. 权限

| 权限 | 检查方式 | 声明 | 未授权时 |
|---|---|---|---|
| 屏幕录制 | `CGPreflightScreenCaptureAccess()`；请求用 `CGRequestScreenCaptureAccess()` | `Info.plist` → `NSScreenCaptureUsageDescription` | 采集被系统拒绝；Host 直接拦下并提示去「系统设置 → 隐私与安全性 → 屏幕录制」 |
| 辅助功能 | `AXIsProcessTrustedWithOptions`（`kAXTrustedCheckOptionPrompt`） | **没有 Info.plist 键**，只能在运行时检查并引导 | 画面照常共享，但 CGEvent 注入全部丢弃；UI 显示橙色警告 + 「打开系统设置」按钮 |

* `com.apple.security.app-sandbox` **没有开启**（`ENABLE_APP_SANDBOX: NO`，且没有 entitlements
  文件）。开沙盒后 ScreenCaptureKit + CGEvent 注入要额外配一堆 entitlement，对自用工具不值得。
* 用 ad-hoc 签名（`CODE_SIGN_IDENTITY = "-"`）。TCC 是按代码签名记账的，ad-hoc 每次重编签名
  会变，系统可能重新询问一次权限。
* 授权辅助功能后**通常要重启本应用**才生效，UI 上写了这句。

---

## 7. 界面

窗口 1180×780，左侧 220 pt 固定侧栏，右侧页面切换；配色与 Android 端一致（背景
`#0E1014`、卡片 `#161A20`、主色 `#00D26A`）。

| 页面 | 内容 |
|---|---|
| 设备 | 「远程协助」大卡片：9 位连接码输入框、连接按钮、可展开的 IP 直连（地址 + 视频/控制端口）；下方「我的设备」网格，卡片显示名称、地址、连接码、上次连接时间，可连接或删除；空态提示先用 IP 直连 |
| 远程协助 | 同一张协助卡片 + 连接步骤 / 当前限制侧栏 |
| 被控 · 这台 Mac | 共享开关与状态、本机端口 / 对端地址、控制端 IP 与端口输入、显示器选择、最大宽高/帧率/码率、屏幕录制与辅助功能权限行、实时统计、使用提示 |
| 设置 | 版本与 Rust 核心、连接与安全说明、键位映射表、清空本地设备 |

**会话界面**：连接成功后整个窗口覆盖为黑色视频层（`AVSampleBufferDisplayLayer`）。顶部悬浮条
含返回、设备名、延迟胶囊、统计开关、收起控制条；底部悬浮条含统计、释放按键、全屏、断开；
右上角统计卡显示分辨率、帧率、延迟、丢帧、接收字节。收起控制条后顶部留一个「显示控制条」按钮。

### 9 位连接码的含义

连接码由固定信令服务器 `jsspace.xyz:3479` 签发：

1. 被控端点「开始共享」后，服务器生成 9 位数字码（首位非 0），默认 300 秒有效；过期自动换码。
2. 控制端输入该码，双方注册并加入同一会话，由服务器转发端点与媒体端口。
3. 媒体密钥由连接码 + 会话 ID 经 HKDF-SHA256 两端各自派生，服务器不接触密钥。
4. 之后媒体走 UDP 点对点；同一 NAT 内优先内网地址。

连接码不写入 `DeviceBook`，`DeviceBook` 只保存 IP 直连设备。信令实现在 `Sources/Core/SignalingWire.swift`、`SignalingClient.swift`、`MediaKeys.swift`、`PeerMediaInfo.swift`。

---

## 8. 构建

```sh
# 只检查 Rust 核心（fmt/clippy/test）
scripts/build-macos.sh check

# 交叉编译两个架构的静态库
scripts/build-macos.sh targets

# lipo + xcframework + xcodegen（会顺带 targets）
scripts/build-macos.sh framework

# 编 App（Debug，ad-hoc 签名；默认 universal）
scripts/build-macos.sh app

# 只编 Intel MacBook（x86_64）
scripts/build-macos.sh app-x64

# 只编 Apple silicon（arm64）
scripts/build-macos.sh app-arm64

# 打开
scripts/build-macos.sh run

# 打印 universal 库架构、14 个导出符号、xcframework 布局
scripts/build-macos.sh verify
```

脚本在非 Darwin 上直接 `exit 2`，不会假装成功。

Bundle id 默认 `org.screencast.macos`，可用 `MACOS_BUNDLE_ID=...` 覆盖。

### 在 GitHub 上编译

`.github/workflows/macos.yml` 跑矩阵，推送或 PR 改动 `native/**`、`apps/macos/**`、
`scripts/build-macos.sh` 时触发：

| 矩阵项 | Runner | 步骤 | 产物 |
|---|---|---|---|
| x64 | `macos-15-intel`（Intel） | `framework` → `app-x64` → `verify` | `screencast-macos-x64`（`ScreenCast-x64.zip`） |
| universal | `macos-15`（arm64） | `framework` → `app` → `verify` | `screencast-macos-universal`（`ScreenCast-universal.zip`） |

`release.yml` 已把 macOS 工作流加入 `needs`，打 `v*` 标签时产物一起进 Release。App 是 ad-hoc
签名（`CODE_SIGN_IDENTITY="-"`），下载后首次打开需右键「打开」，并手动授予屏幕录制 / 辅助功能
权限。

---

## 9. 使用

两端用同一份默认密钥与 sessionId（32 字节 `00..1F`，16 字节 `A1..B0`），没有信令服务器，
所以两边都要手填对端端口。

**Windows 当 Host、Mac 当 Viewer**：Mac 端填 Windows 机器的 IP + Windows 的
视频/控制端口；Windows 端填 Mac 的 IP + Mac 界面显示的本机端口（默认 42100/42101）。

**Mac 当 Host、Windows 当 Viewer**：Mac 端 Host 页填 Windows 的 IP + Windows 的端口；
本机端口默认 42000/42001。

---

## 10. 未验证声明

**已在 GitHub Actions 上编译通过，但没有在真实 macOS 桌面上运行过。**

本轮验证（提交 `532f622`）：

* `macOS client` 工作流的两个矩阵任务都成功：`macos-15-intel` 产出 `screencast-macos-x64`
  （1.33 MB），`macos-15` 产出 `screencast-macos-universal`（2.65 MB）。
* `verify` 打印了 universal 静态库、14 个 `sc_` 导出符号、xcframework 布局与 App 架构。
* 编译过程中发现并修掉三个真实问题：
  1. `macos-14`  runner 标签已退役 → 改用 `macos-15-intel`（x64）与 `macos-15`（universal）。
  2. xcframework 与 `native/include/module.modulemap` 重复声明 `ScreenCastCore` module →
     App 改为直接链接 `build/universal/libscreencast_core.a`。
  3. `VTDecompressionSessionCreate` 的 Swift 标签实际是 `decoderSpecification:` /
     `imageBufferAttributes:`，不是头文件里的内部参数名。
* tree-sitter-swift 语法解析全部通过。

以下仍然**未经执行验证**：

* 界面没有在真实窗口里渲染过；布局、悬浮条、9 位码输入都没有肉眼确认。
* 屏幕录制 / 辅助功能授权流程、CGEvent 注入、ScreenCaptureKit 采集与 VideoToolbox 编解码
  都没有实机跑过，只保证能编译、能链接。
* 协议互操作性（与 Windows 端真机对跑）**没有验证**。
* 键位映射表逐条手写，**没有在真机上按键验证**，尤其 `Help → Insert`、`KP /`、`KP Enter`、
  Right Option/Control 的 E0 组合。
* 滚轮方向：Windows `deltaY > 0` 是「向上」，`CGEvent.wheel1 > 0` 也是向上，按此实现；
  **水平方向的正负没有实测**，如方向相反改 `MacInputInjector.wheel` 里 `horizontal` 的符号。
* 触摸板精确滚动 ×8 的折算系数是经验值，没有调过。
* ScreenCaptureKit 的 `.idle` 帧过滤、`SCContentFilter` 的构造、`SCStream.startCapture` 的
  完成回调签名，都按文档写，未实测。
* 辅助功能与屏幕录制的 TCC 行为（ad-hoc 签名下是否重新弹窗）未实测。

**首次真机运行时，最可能出问题的地方按概率排序**：

1. `H264Encoder` / `ScreenCaptureSource` 里 VideoToolbox 与 ScreenCaptureKit 的运行时行为；
2. `MacInputInjector` 的坐标换算与 TCC 授权；
3. `CoreSession` 里 Rust 会话的绑定 / 对端设置顺序；
4. `HostResolver` 里 `getaddrinfo` / `getnameinfo` 的指针参数。

---

## 11. 未实现的部分

* **信令服务器 / SCSH 握手 / TURN / 打洞**：`native/` 本身没有（见 `native/README.md`
  「Not implemented here」），所以本客户端也没有。密钥与 sessionId 是手填的固定值。
* **自动发现对端地址**：C ABI 没有窥视源地址的接口，Host 必须手填控制端地址（见 2 节末）。
* **多显示器同时共享**：一次只采一个 `SCDisplay`。控制事件的 `monitorId` 字段固定发 0。
* **文本输入 / 输入法**：只发扫描码，不发 Unicode 文本。中文等需要 IME 的输入在远端无法直接
  输入（协议里 `Touch`/文本是另一个话题）。
* **音频**：协议有 channel 4 但 `native` 未实现，本客户端也没有。
* **AV1**：只有 H.264（`codec = 1`）。
* **抖动缓冲 / 自适应延迟 / FEC**：`native` 未实现；解码是收到就解。
* **码率自适应 / SetQuality**：`SetQuality`(8) 事件在 Host 侧被忽略（只 break），码率靠 UI 手填。
* **光标作为独立状态更新**：靠 `showsCursor = true` 把光标烤进画面。
* **剪贴板同步**：协议里没有。
* **多 Viewer 并发**：一个 Host 会话只有一个对端。
* **重连**：断开后需要手动重连，没有自动重试。
* **代码签名 / 公证 / 分发**：只有 ad-hoc，没有 Developer ID、没有 notarization、没有 dmg/pkg。

---

## 12. 与 Windows 端行为对齐的点

| 行为 | Windows | macOS |
|---|---|---|
| 控制事件体线格式 | `InputCodec.cs` | `ControlWire.swift`（同尺寸、同大端、同 flags 位） |
| 坐标归一化取整 | `Normalize`：`round(ratio * 65535)` | `ControlWire.normalize` 同式 |
| 关键帧请求限流 | 500 ms | 500 ms（`CoreDefaults.keyFrameRequestInterval`） |
| 丢帧 → 请求关键帧 | `ViewerSession.ReceiveLoopAsync` | `ViewerSession.pollLoop` |
| 输入租约 | `ControlLease` 1/0 | 同 |
| `ReleaseAll` / 租约丢失弹键 | `WindowsInputInjector.ReleaseAll` | `MacInputInjector.releaseAll` / `releaseLease` |
| `SC_EVENT_CONTROL_EXPIRED` 弹键 | 平台层负责 | `HostSession.handle` → `releaseAll()` |
| 首帧强制关键帧 | `_forceKeyFrame = 1` | `forceKeyFrame.raise()` |
| configId / codec / flags | 1 / H264 / key=1 | 同 |
| 会话密钥与 sessionId | 32B `00..1F` / 16B `A1..B0` | 同 |
| epoch | 1 | 1 |

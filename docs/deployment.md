# 部署与使用流程

本文按"服务器 → Windows 主机 → 控制端"的顺序给出可照做的步骤。所有命令都在仓库根目录执行，产物统一输出到 `artifacts/`。

> 状态说明：服务器（STUN + 信令）与 Windows 客户端已完成；Windows、Android、macOS 三端默认通过固定地址 `jsspace.xyz` 完成信令配对，IP 直连保留为高级回退。配对一次后连接码固定保存在服务器，客户端会记住上次的码，之后无需重新配对。Android 与 MacBook 的部署步骤见对应小节，标注了各自的验证状态。

## 0. 环境要求

| 角色 | 系统 | 依赖 |
|---|---|---|
| 服务器 | **Windows Server 2022**（硬约束） | 无，自包含 EXE |
| Windows 主机/控制端 | Windows 10 1809+ / Windows 11 | 无，自包含 EXE；需要支持 DXGI Desktop Duplication 的显卡 |
| Android 控制端 | Android 8.0+ | 无，APK |
| MacBook | macOS 13+ | 无，App；采集需屏幕录制权限，控制需辅助功能权限 |

客户端与服务端使用同一份协议，版本必须一致。

## 1. 服务器部署（Windows Server 2022）

客户端把信令地址写死为 `jsspace.xyz:3479`，服务端必须部署在能让这个域名解析到的机器上。

### 1.1 DNS

在域名服务商处给 `jsspace.xyz` 添加 A 记录，指向服务器的公网 IP：

```
jsspace.xyz.  A  <服务器公网 IPv4>
```

解析生效后用 `nslookup jsspace.xyz` 确认。三端不会回退到别的地址，DNS 不通就只剩 IP 直连。

### 1.2 构建

在开发机（装 .NET 8 SDK）上：

```powershell
dotnet publish src/ScreenCast.Server -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=embedded -o artifacts/windows-server
```

产物：`artifacts/windows-server/ScreenCast.Server.exe`（单文件，约 65 MB）。

### 1.3 上传与首次自检

把 EXE 复制到服务器，例如 `C:\ScreenCast\`。**先跑自检**，确认这台机器上两个子系统都正常：

```powershell
C:\ScreenCast\ScreenCast.Server.exe --self-test
```

期望输出结尾：`self-test passed: 11 checks`。这 11 项用的是真实 UDP socket，不是模拟。

### 1.4 开放端口

服务端有两个 UDP 端口：

| 用途 | 默认端口 | 说明 |
|---|---:|---|
| STUN | 3478 | 标准 RFC5389，客户端用来发现自己的公网映射 |
| 信令 | 3479 | SCSG v1，会话与端点交换 |

Windows 防火墙：

```powershell
New-NetFirewallRule -DisplayName "ScreenCast STUN" -Direction Inbound -Protocol UDP -LocalPort 3478 -Action Allow
New-NetFirewallRule -DisplayName "ScreenCast Signaling" -Direction Inbound -Protocol UDP -LocalPort 3479 -Action Allow
```

云主机还要在**安全组**里放行同样的 UDP 端口——这是最常见的"服务端起来了但连不上"的原因。

### 1.5 启动

前台调试：

```powershell
C:\ScreenCast\ScreenCast.Server.exe --bind 0.0.0.0 --stun-port 3478 --signal-port 3479
```

正常应打印：

```
ScreenCast Binding listening on 0.0.0.0:3478 (RFC5389 Binding only; TURN not implemented)
Signaling listening on 0.0.0.0:3479 (SCSG v1, max 8192 B per message)
limits: devices<=... sessions<=... perDevice<=... perHost<=...
leases: device=...ms session=...ms invite=...ms scan=...ms
pairings: E:\ScreenCast\pairings.json (0 hosts)
```

`pairings.json` 保存每个被控设备的固定连接码，首次创建会话时自动生成，重启服务器后继续沿用。

**默认只绑 127.0.0.1**，不显式给 `--bind 0.0.0.0` 就不会对外服务。

### 1.6 完整参数

```powershell
ScreenCast.Server.exe --help
```

关键项：

| 参数 | 默认 | 作用 |
|---|---:|---|
| `--bind <ip>` | 127.0.0.1 | 两个 socket 的绑定地址 |
| `--stun-port <port>` | 3478 | STUN 端口，`--port` 是它的别名 |
| `--signal-port <port>` | 3479 | 信令端口 |
| `--no-stun` / `--no-signal` | 都启用 | 关掉其中一个子系统 |
| `--device-lease-seconds` | 45 | 设备多久无活动被移除 |
| `--session-idle-seconds` | 45 | 会话多久无活动被关闭 |
| `--invite-seconds` | 300 | 未配对邀请的有效期；已配对设备的固定码不受影响 |
| `--pairing-file` | `pairings.json` | 固定连接码表；删除该文件会让所有已配对设备换码 |
| `--scan-seconds` | 5 | 清理扫描间隔 |
| `--max-sessions` / `--max-devices` | 见 help | 全局上限 |
| `--max-sessions-per-device` / `--max-sessions-per-host` | 见 help | 单设备上限 |
| `--global-rate` / `--per-ip-rate` | 200 / 20 | 每秒请求数限流 |
| `--punch-delay-ms` | 见 help | 打洞起始建议延迟 |

### 1.7 注册为 Windows 服务（可选）

自包含 EXE 可以直接用 `sc.exe` 注册，崩溃后自动重启：

```powershell
sc.exe create ScreenCast binPath= "C:\ScreenCast\ScreenCast.Server.exe --bind 0.0.0.0 --stun-port 3478 --signal-port 3479" start= auto
sc.exe failure ScreenCast reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start ScreenCast
```

查看日志用事件查看器，或先前台跑一遍确认无误再注册。

### 1.8 容量参考

4 核 / 4 GB 的机器，服务端只做信令与地址交换，不碰视频。真正的带宽压力在中继（TURN，尚未实现）和客户端本身。信令是低频小包，4 核足够支撑数百会话的设备表；瓶颈会先出现在带宽与客户端编码能力上，而不是这里。

## 2. Windows 客户端

### 2.1 构建

```powershell
dotnet publish src/ScreenCast.Windows -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=embedded -o artifacts/windows-client
```

产物：`artifacts/windows-client/ScreenCast.Windows.exe`（单文件，约 156 MB）。

### 2.2 被控端（Host）

1. 双击 EXE，角色选 **Host（被控）**。
2. 在显示器下拉框里选要共享的屏幕。
3. 连接方式保持 **服务器配对**，点 **开始共享**。
4. 界面显示服务器签发的 **9 位数字连接码**（例如 `683 204 917`），并提示"等待控制端输入连接码"。

把连接码告诉控制端即可，不需要 IP 和端口。同一个被控设备会一直使用同一个码：重开会话、重启客户端、重启服务器（`pairings.json` 保留）后都不变，因此只需要配对一次。码过期不再影响已配对设备；控制端离线时被控端继续等待，控制端再次加入会自动建立新会话。

5. 控制端加入并接受后，双方通过服务器交换媒体地址，随后建立点对点传输。状态栏会显示"已连接 <地址>:<视频端口>/<控制端口>"，以及采集/编码/关键帧/发送字节数。

Host 的输入注入是**租约制**：只有控制端显式申请租约后才会注入，断开或失焦时自动抬起所有按下的键和鼠标按钮。

### 2.3 控制端（Viewer）

1. 双击 EXE，角色选 **Viewer（控制）**。
2. 连接方式保持 **服务器配对**，输入 Host 界面上的 9 位连接码；输入框会自动填入上次成功配对的码。
3. 点 **连接**，等待双方交换媒体地址。
4. 连上后窗口里就是远端桌面，**点一下画面**让窗口拿到键盘焦点，之后鼠标键盘都会转发到远端。
5. **双击画面**切换全屏/窗口。

### 2.4 两台机器之间的网络

默认路径：

1. 两端分别向 `jsspace.xyz:3479` 注册并加入同一个会话。
2. 服务器只转发双方观测到的公网端点、内网候选地址与媒体端口，**不接触视频，也不持有媒体密钥**。
3. 媒体密钥由 9 位连接码 + 会话 ID 经 HKDF-SHA256 在两端各自派生。
4. 之后视频与控制走 UDP 点对点；同一 NAT 内优先用内网地址，跨 NAT 用公网地址。

限制：

- 当前没有 TURN 中继。双方都在严格 NAT / 对称 NAT 后面时可能无法直连，这是已知限制。
- 跨公网不再需要手工映射端口；只有改用 **直连（高级）** 时才需要自己对齐 IP 与端口。
- 信令是明文 UDP，服务器能看到会话元数据与连接码，但看不到媒体内容。

### 2.5 常见问题

| 现象 | 原因与处理 |
|---|---|
| Host 一直"等待控制端输入连接码" | 控制端还没加入：确认 9 位码没有输错、没有过期，并且两端都能解析 `jsspace.xyz` 与访问 UDP 3479 |
| 控制端黑屏但状态显示有帧 | 解码失败：查看状态栏的"拒绝/丢帧"计数。丢帧会自动请求关键帧，通常 1 秒内恢复 |
| 画面卡顿 | 检查状态栏 fps 与解码渲染耗时。默认 1280x720@30fps，可改 `H264EncoderSettings.Default720p30` 后重新构建 |
| 键鼠没反应 | 先点一下画面拿焦点；Host 侧确认"输入租约"不是"无" |
| 某些窗口黑屏 | 受保护内容或反作弊拦截 DDA，当前无 WGC 回退 |

## 3. Android 控制端

### 3.1 构建

本机没有 Android SDK/NDK，APK 通过 GitHub Actions 构建：

```bash
# 推送后 mobile.yml 自动跑，产物在 Actions 的 Artifacts 里
# 或本地有完整工具链时：
bash scripts/build-android.sh all
```

需要的工具链（CI 已固定）：JDK 17、Gradle 8.10.2、AGP 8.7.3、NDK 27.2.12479018、cargo-ndk 3.5.4。

### 3.2 安装与使用

1. 安装 APK（debug 签名，允许未知来源）。
2. 在首页输入被控端显示的 9 位连接码，点连接；输入框会自动填入上次成功配对的码。页面提示"正在连接服务器 jsspace.xyz…"，随后等待被控端。
3. 画面出现后：
   - 单指拖动 = 移动鼠标
   - 单指点击 = 左键
   - 双指点击 = 右键
   - 双指拖动 = 滚轮

服务器配对失败时，可展开「使用 IP 地址直连」填写对方 IP、视频端口与控制端口。直连需要两端使用同一份共享密钥与会话 ID，仅供调试。

### 3.3 键盘限制

Android 的 `KeyEvent` 键码与 PC 扫描码是两套体系。项目只映射有确定对应关系的键（方向键、回车、退格、Tab、Esc、空格、修饰键），**普通字母数字不发送**——与其发一个错误的扫描码让对方乱按键，不如明确不支持。

### 3.4 验证状态

**本地已真实构建**：`javac` 对真实 API 35 `android.jar` 零 error/warning，`./gradlew lintDebug` 输出 `No issues found.`，`assembleDebug` 产出 36 KB 的 `app-debug.apk`。

**未验证**：Rust `.so` 交叉编译（本机无 NDK，下载速度约 0.02MB/s），真机运行。APK 里目前没有 `.so` 是设计如此——`jniLibs/` 是生成目录，由 CI 的 `cargo-ndk` 步骤填充。

**接入顺序**：`native/src/android_jni.rs` 已就位并在 `lib.rs` 中以 `#[cfg(target_os = "android")]` 引入，13 个 JNI 符号与 Java native 方法全部对应，无重复定义。CI 打 APK 时会自动编进 `.so`。

## 4. MacBook 客户端

### 4.1 构建（需要 macOS + Xcode 15+）

```sh
scripts/build-macos.sh check      # Rust 侧 fmt / clippy / test
scripts/build-macos.sh framework  # 两个 Darwin target → lipo → xcframework → xcodegen
scripts/build-macos.sh app        # xcodebuild Debug，ad-hoc 签名
scripts/build-macos.sh run
```

产物在 `apps/macos/build/`。非 macOS 上脚本直接退出码 2，不会假装成功。

### 4.2 权限（两个都要，缺一不可）

| 权限 | 用途 | 未授权的后果 |
|---|---|---|
| **屏幕录制** | Host 采集本机屏幕 | Host 无法启动，界面直接拦下并提示 |
| **辅助功能** | Host 注入远程鼠标键盘 | **画面照常共享，但不能被控制** |

授权路径：系统设置 → 隐私与安全性 → **屏幕录制** / **辅助功能**，勾选 ScreenCast。

辅助功能没给时，界面会显示橙色警告和"打开系统设置"按钮。ad-hoc 签名下重新编译可能需要重新授权。

### 4.3 使用

**Viewer（控制 Windows / Mac）**：
1. 角色选 Viewer，输入对方设备上的 9 位连接码（上次的码会自动填入），连接。
2. 双方通过 `jsspace.xyz` 交换媒体地址，成功后画面出现；点击窗口获得焦点，之后鼠标键盘转发到远端。

**Host（被 Mac 共享）**：
1. 角色选 Host，先确认两个权限都已授予。
2. 选择要共享的显示器，点「开始共享」，界面显示服务器签发的 9 位连接码。
3. 把连接码发给控制端；同一个设备始终使用同一个码，过期也不会变。

**直连回退**：两侧面板都保留了「直连（高级）」，需要手工对齐 IP、端口与共享密钥，不经过服务器。

### 4.4 键盘映射

Mac 的 keyCode 和 PC 扫描码是两套完全不同的编码。项目内置 97 条双向映射表（字母、数字、标点、方向键、功能键、修饰键、数字键盘），左右修饰键靠 extended 位区分。

**故意不发送**：Command、Fn、F13–F20、音量键、ISO Section、数字键盘 `=` 和 Clear——PC 侧没有对应扫描码，瞎发等于在对方机器上按错键。

### 4.5 验证状态

**未在 macOS 上编译或运行验证过。** 本机不是 macOS，没有 Swift 工具链和 xcodebuild。代码按 Apple 文档手写，做过括号配平静态检查，但未经编译器检查。首次构建最可能出问题的地方：VideoToolbox 回调结构、ScreenCaptureKit 附件字典桥接、`sc_session_t*` 的 OpaquePointer 可选性、getaddrinfo 指针参数。

## 5. 验证清单

部署完成后按这个顺序自查：

```powershell
# 1. 服务器自检（在服务器上）
ScreenCast.Server.exe --self-test

# 2. 服务器端口是否真的在监听
Get-NetUDPEndpoint -LocalPort 3478,3479

# 3. 固定域名是否解析到服务器
Resolve-DnsName jsspace.xyz

# 4. 客户端单帧采集是否正常
ScreenCast.Windows.exe --mode host --diagnostics .\cap.json --capture-once

# 5. 实时链路是否正常（同机，不需要第二台机器）
ScreenCast.Windows.exe --live-smoke .\live --live-seconds 10
```

第 5 步期望结尾是 `结果：ok`，且报告里 `ViewerRendered > 0`、`ViewerDroppedFrames = 0`。

跨进程验证（两个窗口）：

```powershell
# 窗口 A
ScreenCast.Windows.exe --host-listen 41100 41101 41210 20 .\xp-host
# 窗口 B
ScreenCast.Windows.exe --viewer-connect 127.0.0.1 41100 41101 41210 41211 15 .\xp-viewer
```

两边都应以退出码 0 结束。

# 验证记录与当前实现边界

日期：2026-11-21。原始目录13,953文件；guide首先创建，然后才建立新设计/工程。参考源码未直接复制。工作区原本没有Git元数据，因此本轮以新文件逐项审阅及工具验证代替git diff；未初始化远端或部署服务器。

## 本地已验证

| 项目 | 结果/环境 |
|---|---|
| 根ScreenCast.sln Release | Windows、SDK10.0.401、目标net8.0/net8.0-windows，0警告/0错误 |
| .NET格式 | dotnet format ScreenCast.sln --verify-no-changes --no-restore --include src tests，通过 |
| Core最终测试 | 15项通过（含新增FINGERPRINT CRC/位置测试与MAPPED-ADDRESS兼容）：共享Rust头向量、固定AES向量、篡改/方向隔离/1024重放窗口、ACK序号、IPv4/6 STUN RFC5769地址向量、非法长度/事务/来源、420响应、3次打洞失败策略、对称NAT转relay策略、IPv4/6 localhost UDP |
| 运行时区别 | 本地net8测试使用--roll-forward Major运行在.NET10；发布EXE包含net8运行时，CI明确setup-dotnet8 |
| Windows singlefile | win-x64自包含发布成功，artifacts/windows-client/ScreenCast.Windows.exe |
| DDA实际GPU诊断 | 最终EXE退出0，NVIDIA GeForce RTX5060，2560×1440，B8G8R8A8_UNorm，dirtyRect1/moveRect0；证据artifacts/capture-result.json |
| Server发布进程 | win-x64自包含EXE启动后，独立UDP客户端收到32字节标准Binding成功响应；验证进程已关闭。首个启动即测脚本遇宿主PowerShell缺FromHexString及启动时序问题，改为分步启动后实测通过 |
| Windows UI smoke | 子代理实测Host/Viewer窗口响应且正常关闭；按钮未自动化覆盖 |
| Rust基础 | 隔离Rust1.83工具链：fmt、宿主Clippy、Android ARM64/x86_64 JNI Clippy均通过，6项测试通过，实际DLL C ABI调用通过；本机MinGW16 MCF测试额外使用-lmcfgthread，仅本地环境设置。详见native/VALIDATION.md |

STUN/GCM测试验证当前代码范围，RFC5769地址向量不等于全套RFC5389合规；连接策略测试不等于真实公网打洞。

## 已完成：UDP 传输层与会话（2026-11-21 第二轮）

`src/ScreenCast.Core/Transport/` 新增实现，并新增独立测试工程 `tests/ScreenCast.Transport.Tests`（已加入 `ScreenCast.sln` 与 Windows CI）：

| 组件 | 实现内容 |
|---|---|
| [UdpTransport.cs](<E:/ScreenCast/src/ScreenCast.Core/Transport/UdpTransport.cs>) | 真实绑定 UDP socket、单 owner 接收、取消接收不关 socket（保住 NAT 映射）、池化数据报租约、地址族校验、重入拒绝 |
| [DatagramClassifier.cs](<E:/ScreenCast/src/ScreenCast.Core/Transport/DatagramClassifier.cs>) | 按 SCT1 → SCSH → STUN cookie → TURN 0x4000-0x7FFF 的固定优先级分派，应用 magic 优先于 0x5343 冲突区间 |
| [ControlCodec.cs](<E:/ScreenCast/src/ScreenCast.Core/Transport/ControlCodec.cs>) | 32 字节控制头编解码、保留位/kind/长度校验、body 预算 |
| [ReliableControlChannel.cs](<E:/ScreenCast/src/ScreenCast.Core/Transport/ReliableControlChannel.cs>) | 累计+选择 ACK、64 条窗口、按序交付、重复只执行一次但必回 ACK、RTO 重传、单消息指数退避、快重传、Karn RTT、2 秒/8 次过期上报 |
| [VideoFraming.cs](<E:/ScreenCast/src/ScreenCast.Core/Transport/VideoFraming.cs>) | 32 字节帧分片前缀、分片器与重组器、4 帧/8MiB/8192 片上限、缺片整帧丢弃、过期清理 |
| [MediaSession.cs](<E:/ScreenCast/src/ScreenCast.Core/Transport/MediaSession.cs>) | 双 socket 会话、四个方向域密钥、视频发送分片加密、控制发送队列、两条接收循环、密码学失败计数丢弃 |

**真实执行结果**：`dotnet build -c Release` 0 警告 0 错误；`dotnet run tests/ScreenCast.Transport.Tests` **88/88 PASS**；`dotnet format --verify-no-changes` 通过。

关键验证项包括：真实 loopback UDP 收发、取消接收后端口不变且可复用、第二个接收 owner 被拒、乱序缓冲不提前交付、丢首个 ACK 后重传仍完成、分片重组字节完全一致、缺片绝不进入解码器、伪造 tag 被计数丢弃且会话继续工作、以及**两个 `MediaSession` 经真实 loopback 完成 4000 字节加密视频帧与控制事件端到端传输**。

这解除了先前"接口层零实现"和"测试只覆盖单进程回环不做跨 socket"两处核心缺口中的传输部分。仍未做：真实公网打洞、TURN 中继、保活与断线重连、硬件编解码、输入注入。

**第二轮补充**：保活探测、打洞调度与链路策略已实现并通过测试；同轮媒体链（持续 DDA + H.264 编码）与信令服务（11 项真实 UDP 自检）也已完成，见下节。

## 已完成：真实媒体链（2026-11-21 第三轮）

`src/ScreenCast.Windows` 新增持续采集与 H.264 编码，并新增 `--media-smoke <dir>` 真实执行入口。

| 组件 | 实现内容 |
|---|---|
| Capture/DesktopDuplicator.cs | 持续 DDA 采集循环、超时继续、AccessLost 重建、尺寸变化检测、脏矩形/移动矩形统计、静止帧跳过与帧率节流 |
| Capture/CapturedFrame.cs | 帧租约模型，保证纹理在 ReleaseFrame 前完成消费 |
| Media/H264Encoder.cs | Media Foundation H.264 MFT 编码，序列头、关键帧标记、时间戳、码率/GOP/分辨率设置 |
| Media/Nv12Converter.cs | BGRA→NV12 转换（GPU 或 CPU 路径） |
| Media/H264Bitstream.cs | Annex-B NAL 解析与校验（SPS/PPS/IDR/SEI） |
| Media/MediaFoundationProbe.cs | 枚举硬件与软件 H.264 MFT，探测 D3D11 表面缓冲可用性 |
| MediaSmoke.cs | 端到端冒烟：采集→转换→编码→写 `.h264`→NAL 校验→JSON 报告 |

**真实执行结果**（本机 RTX 5060，2560×1440 桌面）：

| 指标 | 实测值 |
|---|---|
| 退出码 | 0 |
| 采集帧 | 140（超时 0、重建 0、尺寸变化 0） |
| 编码帧 | 60，关键帧 2 |
| 输出 | 256,259 字节 H.264 |
| 编码设置 | 1280×720@30fps, 4 Mbps, GOP 30 |
| NAL 统计 | 总 126，SPS 2，PPS 2，IDR 2，非 IDR 58，SEI 2 |
| 起始码 | true（真实 Annex-B） |

**诚实限制**：本次实际走的是 Windows 内置**软件** `CMSH264EncoderMFT`，不是 NVIDIA 硬件 MFT；`MFCreateDXGISurfaceBuffer` 探测返回 `E_INVALIDARG`，因此纹理走 CPU NV12 转换而非零拷贝 GPU 路径。硬件 MFT 已被枚举到（`NVIDIA H.264 Encoder MFT [async]`）但未接入成功。这是下一阶段的首要目标。

## 已完成：UDP 信令会话服务（2026-11-21 第三轮）

`src/ScreenCast.Server` 从 51 行 Binding 服务扩展为完整信令子系统（约 2,000 行），并新增 `--self-test` 用**真实 UDP 客户端**跑完整流程。

| 组件 | 实现内容 |
|---|---|
| Signaling/SignalingWire.cs | SCSG v1 手写二进制帧、128 位 requestId/sessionId、端点编解码与规范化、字段上限 |
| Signaling/SignalingService.cs | 设备注册/租约、会话状态机（Created→Accepted→Negotiating→Active→Closed）、邀请码、端点交换、NAT 类型与候选列表、打洞指令、密钥负载透传、超时清理 |
| Signaling/SignalingServer.cs | UDP 接收循环、限流、幂等 requestId 缓存、异常请求静默丢弃 |
| Signaling/SignalingState.cs | 设备/会话/密钥代次状态与并发保护 |
| ServerOptions.cs | 完整命令行：`--bind --stun-port --signal-port --no-stun --no-signal` 及租约/上限/限流参数 |
| ServerHost.cs | 单进程托管两个 socket、`--self-test`、`--help` |

**真实执行结果**：`dotnet run -- src/ScreenCast.Server --self-test` → **11/11 PASS**：

```
PASS selftest.register                    PASS selftest.punch-start
PASS selftest.register-second-device      PASS selftest.peer-key-forward
PASS selftest.create-session              PASS selftest.path-commit
PASS selftest.join-session                PASS selftest.close-session
PASS selftest.accept-session              PASS selftest.unregistered-rejected
                                          PASS selftest.stun-binding
self-test passed: 11 checks
```

其中 `peer-key-forward` 验证密钥负载逐字节透传且服务端不解析；`unregistered-rejected` 验证未注册源地址被拒。

**仍未实现**：TURN 中继、真正的设备身份签名认证（当前 deviceId 为自报字符串）、SDP 交换、消息分片。

## 已完成：链路策略与保活（2026-11-21 第三轮）

| 组件 | 实现内容 |
|---|---|
| PathProbe.cs | 加密探测报文、token 匹配 RTT、发送绝不刷新存活时钟、连续 3 次静默判 Down |
| PunchCoordinator.cs | 5 个发送偏移、单次尝试 1.5 秒、最多 3 次、按 generation 丢弃旧结果、只接受已认证探测完成打洞 |
| LinkController.cs | 对称 NAT 直接要求中继、未知 NAT 先真实打洞、3 次失败转中继、双 component 保活监督、超时判 Down 并记录原因 |

## 已完成：端到端数据面（2026-11-21 第四轮）

`src/ScreenCast.Windows/SessionSmoke.cs` 新增 `--session-smoke <dir>`，把媒体链与传输层真正接起来：

**Host**：持续 DDA 采集 → NV12 转换 → H.264 编码 → 分片 → AES-GCM 加密 → 真实 loopback UDP 发送
**Viewer**：UDP 接收 → 解密 → 去重/重组 → 拼成 Annex-B 码流写盘

同时验证控制通道：viewer 通过可靠控制通道发送 `RequestKeyFrame`，host 收到。

**真实执行结果**（发布版单文件 EXE，RTX 5060，2560×1440 桌面）：

| 指标 | Host 侧 | Viewer 侧 |
|---|---|---|
| 帧数 | 40 编码 | 40 重组 |
| 字节 | 135,520 | 135,520（完全一致） |
| 关键帧 / IDR | 2 | 2 |
| SPS / PPS | — | 2 / 2 |
| NAL 单元 | — | 86（非 IDR 38） |
| 起始码 | — | true |
| 拒绝包 | — | 视频 0 / 控制 0 |
| 控制事件 | 收到 1 | 发送 1 |
| 退出码 | **0** | |

这证明**采集→编码→加密→UDP→重组→解密**整条数据面是通的，且接收侧产出的是合法 Annex-B H.264。**唯一缺的是解码与显示**。

为让 DDA 在有静止桌面时仍能产帧，冒烟会开一个持续重绘的活动窗口制造真实脏矩形——这不是造假，而是复现"桌面有变化"这一真实前提。

## 已完成：解码与呈现（2026-11-21 第五轮）

按用户要求从 `preference/FFmpeg-master` 提取机制后落地。**完整链路现已打通**：DDA 采集 → NV12 → H.264 编码 → 分片 → AES-GCM → UDP → 重组 → 解密 → H.264 解码 → NV12 → BGRA → 位图。

| 文件 | 状态 | 说明 |
|---|---|---|
| `Media/H264Sps.cs` | 已验证 | SPS exp-Golomb 解析，从真实码流解出 **1280x720** |
| `Media/Nv12ToBgra.cs` | 已验证 | FFmpeg `yuv2rgb.c` 定点系数，BT.601/BT.709，行跨度显式传入 |
| `Media/BitmapWriter.cs` | 已验证 | 24 位 BMP 输出与均值/标准差统计 |
| `Media/H264Decoder.cs` | **已验证** | MF H.264 解码 MFT，Annex-B 样本、avcC 序列头、SPS 驱动几何、有界重试 |
| `Media/H264Bitstream.cs` | 增强 | `Payload()` 修正 NAL 偏移、avcC 构造、`SplitAccessUnits()` 共享切分 |
| `DecodeSmoke.cs` / `DecodeProbe.cs` / `DecodeMatrix.cs` | 可用 | 解码冒烟、输出类型探测、六组合矩阵 |

### 四个真实缺陷（逐个实测定位，不是猜的）

| # | 缺陷 | 症状 | 修复 |
|---|---|---|---|
| 1 | 样本用了 4 字节长度前缀 | 解码器永远 `NEED_MORE_INPUT`，0 帧 | 改回 **Annex-B 起始码**（六组合矩阵实测：起始码解码，长度前缀不解码） |
| 2 | 输入类型没带几何信息 | 解码器只提供 1920x1080 占位输出类型 | 从 SPS 读出 coded size 后补 `MF_MT_FRAME_SIZE`，输出类型变为正确的 1280x720 |
| 3 | NV12→BGRA 整数溢出 | 解码成功但画面全黑（均值 0.5） | `(luma-16)*255*65536` 在 Int32 溢出变负数 → 全钳到 0；改 64 位运算后均值 224 |
| 4 | UDP socket 接收缓冲太小 | 关键帧 78KB 突发被丢，整帧丢失，后续 P 帧全废 | 接收/发送缓冲设 4MB；丢失后 viewer 请求关键帧、host 响应 |

第 3 条是最隐蔽的：解码器输出 Y 平面均值 228.6（正确），但转换后是 0.5（全黑）。是"原始 NV12 统计"这条诊断日志把它抓出来的。

### 端到端实测结果（发布版单文件 EXE）

**传输侧**（`--session-smoke`）：

| 指标 | Host | Viewer |
|---|---|---|
| 帧数 | 40 | **40** |
| 字节 | 308,317 | **308,317（完全一致）** |
| SPS / PPS / IDR | — | **2 / 2 / 2** |
| 拒绝包 | — | 0 / 0 |
| 退出码 | | **0** |

**解码侧**（`--decode-smoke`）：

| 指标 | 值 |
|---|---|
| 访问单元 | 40 |
| 原始 NV12 Y 平面均值 | 208.7 |
| 解码画面 | 4 张 BMP |
| 首帧 BGRA 均值 / 标准差 | **224.3 / 70.3** |
| 采集侧原始帧均值 / 标准差 | **224.3 / 70.4** |
| 退出码 | **0** |

采集侧与解码侧的亮度统计几乎完全相同，说明**画面经过编码、加密、UDP、重组、解码后仍然一致**。BMP 的 ASCII 缩略图能看到左上角深色活动窗口、明亮桌面和底部任务栏。

## 已完成：A 阶段 Windows 实时会话与输入注入（2026-11-21 第六轮）

Windows 客户端从"诊断原型"变成可用的远程桌面：实时画面 + 远程鼠标键盘。

| 文件 | 作用 |
|---|---|
| `Input/InputCodec.cs` | 控制事件线编码（Key/MouseMove/MouseButton/Wheel/Touch），全部定长大端，Host 侧可无结构解析地校验 |
| `Input/WindowsInputInjector.cs` | `SendInput` 实现：扫描码注入、绝对坐标移动、滚轮、**按键与按钮全量追踪并在释放时逐一抬起**、租约独占 |
| `Session/HostSession.cs` | 持续采集→跳静止帧→**按目标帧率节流**→编码→分片加密发送；响应 `RequestKeyFrame`；控制事件注入 |
| `Session/ViewerSession.cs` | 接收→重组→解码→NV12→BGRA；丢帧自动请求关键帧（限流 500ms）；键盘鼠标转发 |
| `Render/VideoSurface.cs` | `WriteableBitmap` 渲染，等比 letterbox，坐标反向映射回源像素 |
| `MainWindow.cs` | 真实 UI：Host 显示端口与统计，Viewer 输入地址连接并在窗口内操作远端 |
| `LiveSmoke.cs` | 同进程实时全链路验证 |
| `CrossProcessSmoke.cs` | **两个独立进程**的 Host/Viewer 验证 |

### 关键设计点

- **帧率节流**：DDA 每次桌面变化都给帧，实测能到 145fps。不节流会把码率和 CPU 打满却毫无视觉收益，因此按 `1000/fps` 间隔丢帧。
- **静止帧跳过**：`DirtyRects` 与 `MoveRects` 都为空说明画面没变，直接跳过整个编码步骤。
- **输入释放**：跟踪 `_pressedKeys` / `_pressedButtons`，在断开、失焦、窗口关闭时逐一抬起。丢连接不能让 Host 卡住 Ctrl。
- **扩展键标志**：方向键/Insert/Delete/Home/End/PgUp/PgDn/右Ctrl/右Alt/Win 键与数字键盘共用扫描码，必须单独置 extended 位。
- **坐标映射**：指针坐标按渲染后的画面反算，letterbox 黑边和窗口缩放不会让远端指针偏移。

### 实测结果

**同进程实时会话**（`--live-smoke`，10 秒）：

| 指标 | 值 |
|---|---|
| Host 采集 / 编码 / 关键帧 | 277 / 261 / 9 |
| Host 发送 | 2,905,079 字节 |
| Viewer 解码 / 渲染 | 227 / 227 |
| Viewer 接收 | **2,905,079 字节（与发送完全一致）** |
| 拒绝 / 丢帧 | 0 / 0 |
| 分辨率 / 帧率 | 1280x720 / 26.8 fps |
| 解码渲染耗时 | 4.94 ms |
| 末帧画面 | 均值 224.87，标准差 61.84 |
| 退出码 | **0** |

**两进程跨进程**（`--host-listen` + `--viewer-connect`，两个独立 EXE）：

| 进程 | 结果 |
|---|---|
| Host | 采集 263，编码 247，关键帧 9，发送 1,398,703 字节，退出码 **0** |
| Viewer | 解码 138，渲染 138，接收 891,870 字节，拒绝 0，丢帧 0，1280x720，18.3 fps，末帧均值 60.12 标准差 78.82，退出码 **0** |

解码画面能清楚看到标题栏、中间的绿色活动窗口、左侧桌面图标和底部任务栏。

**顺带修掉一个真实产品缺陷**：静止桌面原本会被无条件跳过，导致**后连上的控制端一直看不到画面**。现在强制关键帧时即使画面静止也编码，保证任何时刻连进来都能拿到一张图。

## 已完成：Rust 跨平台网络核心（2026-11-21 第七轮）

`native/` 从 183 行"角色状态"升级为完整网络核心，供 Android 与 MacBook 复用。**零外部依赖**：AES-256、GCM/GHASH、SHA-256、HMAC、HKDF 全部自实现。

| 模块 | 内容 |
|---|---|
| `src/crypto/` | AES-256、GCM(GHASH)、SHA-256、HMAC、HKDF、OS 熵源 |
| `src/packet.rs` | 64 字节头、域密钥派生、AES-256-GCM、1024 防重放窗口 |
| `src/control.rs` | 32 字节控制头、可靠有序通道（累计+选择 ACK、重复必回 ACK、Karn、RTO、快重传、过期上报） |
| `src/video.rs` | 32 字节分片头、分片器、有界重组器（4 帧/8MiB/8192 片） |
| `src/transport.rs` | 真实 `UdpSocket`、4MiB `SO_RCVBUF`、读超时停线程（保住 NAT 映射） |
| `src/session.rs` | 双 socket、四方向密钥、两条接收循环、控制发送队列、5ms ticker、有界事件队列 |
| `src/probe.rs` | 32 字节探测、token 配对 RTT、发送绝不刷新存活 |
| `src/stun.rs` | RFC5389 Binding、XOR-MAPPED-ADDRESS v4/v6、FINGERPRINT CRC、420、退避客户端 |
| `src/classifier.rs` | SCT1 > SCSH > STUN > TURN 0x4000-0x7FFF 分派 |
| `src/ffi.rs` | 14 个 C ABI 函数 + `sc_event_t`/`sc_stats_t` |

### 与 C# 的协议一致性验证

- **整包已知答案向量与 `docs/packet-vector.md` 完全一致**：tag `0a92411a02b67657d95a187a5a25a623`、密文 `c8226b`。
- 头部 fixture `native/tests/vectors/header-v1.hex` 双向读取。
- HKDF info 布局、nonce 构造、AAD 范围、控制头哨兵语义、分片头字段全部逐字节对齐。

### 实测结果

```
cargo fmt --check                                    exit 0
cargo clippy --locked --all-targets -- -D warnings   exit 0
cargo test --locked                                  102 项全部通过（连跑 3 次一致）
  unittests 89 + ffi_abi 6 + session_loopback 7
cargo build --release                                screencast_core.dll 1,308,239 字节
objdump 导出表                                       14 个符号全部存在
gcc -std=c11 -Wall -Wextra -Werror 头文件检查        通过
g++ -std=c++17 -Wall -Wextra -Werror 头文件检查      通过
python native/tools/abi_smoke.py                     40/40 检查通过（进程外加载真 DLL）
```

回环实测关键项：4000B 与 20000B 帧字节完全一致；伪造 tag 计数丢弃且会话继续；重放被拒且后续包仍收；缺中间分片 300ms 内无帧；100ms 保活两端 Alive；5 条控制事件严格按序交付。

**复核（本轮 lead 独立执行）**：

```
objdump -p screencast_core.dll | 导出表   14 个 sc_* 符号全部存在
python native/tools/abi_smoke.py <dll>    40/40 checks passed
  （进程外加载真 DLL，两个会话真实 UDP 收发加密帧字节一致 + 控制事件 + 统计）
```

DLL 依赖 `libmcfgthread-2.dll`（MinGW 运行时），由脚本自行配置加载路径；部署到 Android/macOS 时使用各自 target 的静态库，无此依赖。

### 已知限制

- **SCSH 信令握手未实现**（P-256 ECDH/cookie/签名），密钥由平台层注入。
- **TURN 未实现**（STUN Binding 已实现并测过）。
- **打洞与路径切换未实现**，会话只接受显式对端地址。
- 编解码、采集、解码、输入注入不在 Rust 层（由各平台原生 API 负责，这是设计选择）。
- 与 C# 对端通信时 Rust 侧路径会显示 Down：C# 的 `MediaSession` 不发 channel 3 探测。视频与控制不受影响，但要在 C# 侧补探测才能消除。

## 进行中：B 阶段 Android 与 MacBook 客户端

| 平台 | 状态 |
|---|---|
| **Android** | 代码已交付（3,231 行 / 17 文件）；**本地真实 Gradle 构建成功，lint 零问题，APK 已打出（36 KB）**；缺 `.so` 是因为本机无 NDK，交叉编译交给 CI |
| **MacBook** | 代码已交付（4,154 行 / 22 文件），**未在 macOS 编译或运行验证**——本机无 Swift 工具链与 xcodebuild |

### Android 已实现

- `NativeCore` 13 个 native 方法 ↔ **13 个 JNI 符号全部对上**（10 个会话符号在 `native/src/android_jni.rs`，3 个旧符号在 `lib.rs`，无重复定义）
- `sc_event_t::data` 在 JNI 层立刻拷成 `byte[]`，指针一步都不进 Java
- `nativePoll` 把 12 个标量塞进调用方预分配的 `long[]`，只返回 payload，帧率下零对象分配
- **MediaCodec** 解码到 `Surface`：`KEY_LOW_LATENCY` 仅 API 30+；不给解码器传关键帧标志（IDR 由码流判断）；`INFO_OUTPUT_FORMAT_CHANGED` 尺寸变化自动重建 Surface 与解码器
- **手势**：单指拖动=移动、单指点击=左键、双指点击=右键、双指拖动=滚轮、双击=收起面板
- **按键**：`KeyEvent.getScanCode()` 是 Linux 输入码，**绝不转发**；用固定表映射到 PC Set 1（含 extended 位）；文本输入无扫描码，明确提示不猜

### Android 实测

```
javac -source 17 -target 17 -Xlint:all（对真实 API 35 android.jar）  0 error / 0 warning
cargo check（JNI 层，宿主 scratch crate）                            0 warning
cargo clippy -- -D warnings                                         干净
rustfmt --check                                                     干净
./gradlew lintDebug                                                 No issues found.（warningsAsErrors=true）
./gradlew assembleDebug                                             BUILD SUCCESSFUL，app-debug.apk 36,004 字节
```

顺手修了一个真坑：旧清单的 `android:allowBackup="false"` 在 targetSdk 35 下触发 lint **error**（`DataExtractionRules`），会让 `scripts/build-android.sh lint` 直接失败。已补 `data_extraction_rules.xml` + `backup_rules.xml`。

以上 Gradle/lint/APK 数据产生于 **UI 重写之前**（2026-11-21 16:59）。重写后的源码只做了下面的编译校验，`build/` 里的 36 KB APK 不是当前界面。

### 本轮（2026-11-22）：Android / macOS 界面重写

目标是把两端做成接近 UU 远程的形态：暗色 + 荧光绿、9 位连接码卡片、设备列表、会话悬浮控制条。

| 项目 | 结果 |
|---|---|
| Android 新增/重写 | `UiKit`、`CodeFieldView`、`DeviceBook`、`HomeView`、`SessionView`，重写 `MainActivity`、`strings.xml`、图标 |
| Android 编译 | 真实 API 35 `android.jar` + `javac --release 17 -Xlint:all`：**0 error / 0 warning** |
| Android 资源 | 脚本核对 `strings.xml` 与 `drawable/*` 的引用，无未使用项；删除 `ic_link.xml`、`ic_chevron.xml` |
| Android lint/APK | **已重跑**：Gradle 8.10.2 + AGP 8.7.3 执行 `lintDebug assembleDebug`，BUILD SUCCESSFUL，lint **No issues found**；APK 94 KB（本机无 NDK，不含 `.so`，CI 打包时带上） |
| macOS 新增/重写 | `Theme`、`DeviceBook`、`SettingsPanel`，重写 `ScreenCastMacApp`、`SharedViews`、`ViewerPanel`、`HostPanel` |
| macOS 语法 | tree-sitter-swift 解析 `Sources/**/*.swift` 全部无 `ERROR` 节点（**仅语法**，不是类型检查） |
| macOS x64 | `project.yml` 固定 `ARCHS: 'arm64 x86_64'`、`ONLY_ACTIVE_ARCH: NO`；`build-macos.sh` 增加 `app-x64` / `app-arm64` |
| macOS 编译 | 本地**未执行** `xcodegen` / `xcodebuild` / `lipo`；新增 `.github/workflows/macos.yml`，推送到 GitHub 后在 `macos-14` 上编译 **x64 与 universal** 两个 App 并上传产物，`release.yml` 已把 macOS 纳入发布 |

两端使用同一套令牌：背景 `#0E1014`、卡片 `#161A20`、主色 `#00D26A`、弱化 `#8D94A3`。连接码是
本机设备簿生成的本地快捷码（IP 直连成功后保存并生成），不是服务端签发，两端 README 都写明了这一点。

### MacBook 已实现

- Viewer：`VTDecompressionSession` 解 H.264 + `AVSampleBufferDisplayLayer` 渲染 + 鼠标键盘转发
- Host：`ScreenCaptureKit` 采集 + `VTCompressionSession` 编码 + `CGEvent` 注入
- 权限：屏幕录制（`CGPreflightScreenCaptureAccess`）、辅助功能（`AXIsProcessTrustedWithOptions`）
- 键盘映射：**97 条双向表**（PC Set 1 ↔ macOS keyCode），左右修饰键靠 extended 位区分
- 注入事件打 `userData` 标记防回环；租约/`ReleaseAll`/过期事件都会弹起按住的键

**故意不发送的键**：Command、Fn、F13–F20、音量键、ISO Section、数字键盘 `=` 和 Clear——PC 侧没有对应扫描码。

**ABI 缺口**：Rust 核心没有"窥视首个数据报来源"的接口，所以 Mac Host 需要手填控制端 IP 与端口（Windows 侧用裸 socket 实现了 `WaitForPeerSources`，Rust 侧没有）。

## 当前确实实现

- C#：64字节应用头、严格长度/版本校验、AES256-GCM、HKDF域分离、1024包认证后提交重放窗口、STUN Binding IPv4/6编解码、Binding事务退避、连接策略状态机、平台抽象。
- 独立服务器：异步UDP Binding基础、显式绑定端点、全局200请求/秒及每IP20请求/秒固定窗口（表最多200项），自包含singlefile发布至artifacts/windows-server；**另含完整 UDP 信令子系统**（设备注册、会话状态机、端点交换、打洞指令、密钥透传、超时清理、幂等 requestId），`--self-test` 11 项真实 UDP 通过。无 TURN、无 Windows Service 宿主。
- Windows：同一EXE双角色界面、显示器枚举、真实DDA单帧诊断、**持续DDA采集 + H.264 编码 + 分片加密发送 + 接收重组**、输入适配器契约。
- Rust：同协议头解析、角色状态、C ABI/JNI版本与角色接口、共享fixture测试。
- Android/iOS：原创角色UI、Rust桥接工程、固定工具链构建脚本、平台README与CI配置。
- CI：Windows构建/格式/测试/publish；Rust检查后Android lint/NDK/APK、iOS XCFramework/simulator/archive；完整签名配置才导出IPA；v* tag触发草稿Release。

## 尚未实现或验证

**A 阶段已完成的边界**：Windows 到 Windows 的实时画面与键鼠控制，在**同一台机器**（同进程与两进程）实测通过。**两台不同机器**之间还差一个能交换端口的信令通道——目前 Host/Viewer 需要手工填写对方的视频与控制端口。真实公网打洞、TURN 中继也未接入。

**B 阶段（Android）与 MacBook 客户端**：进行中，见各自状态。

其余未做：硬件 NVENC MFT 事件驱动、D3D11 纹理零拷贝、日志导出面板、多 Viewer、WGC/GDI 回退、音频、自适应码率。

本机未安装完整Android SDK/NDK，也非macOS；未实际生成APK、XCFramework、iOS archive/IPA。GitHub Actions尚未在线执行；没有上传TestFlight、发布远端Release或连接部署资料中的服务器。iOS签名资源缺省时只构建unsigned archive，不能安装或提交TestFlight。

MVP尚未完成；验收标准是两台不同NAT Windows电脑完成DDA/H264播放及键鼠控制，详见roadmap。此交付为审查、设计和经测试的P0工程基础。

## 复现

```powershell
dotnet build ScreenCast.sln -c Release
dotnet run --project tests/ScreenCast.Core.Tests -c Release --no-build --roll-forward Major
dotnet format ScreenCast.sln --verify-no-changes --no-restore --include src tests
# net8运行时已安装时去掉--roll-forward Major
./artifacts/windows-client/ScreenCast.Windows.exe --mode host --diagnostics ./artifacts/capture-result.json --capture-once
```

完整命令见根guide和各工程README；移动构建脚本在对应工具链环境运行，不拿Windows shell语法检查替代实际打包。

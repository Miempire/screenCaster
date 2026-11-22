# ScreenCast Android 客户端（控制端）

连接 Windows / macOS 被控端，在 `Surface` 上播放远端桌面，并把触摸与按键转发成 ScreenCast
控制事件。画面由 `MediaCodec` 直接解码到 `SurfaceView`，网络层使用 `native/` 的 Rust 核心
（JNI 调用），协议与桌面端逐字节一致。

本版本只实现 **Viewer**。Host 模式（MediaProjection 采集 + 前台服务 + 硬件编码）尚未接入，
因此不申请录屏权限。

界面按 UU 远程的暗色 + 荧光绿风格重写：首页是 9 位连接码卡片 + 设备列表，会话是全屏视频 +
悬浮控制条。

## 界面结构

| 文件 | 作用 |
|---|---|
| `MainActivity.java` | 根 `FrameLayout`，在 `HomeView` / `SessionView` 之间切换；会话回调、按键转发、返回键断开、`onPause` 释放按键 |
| `HomeView.java` | 首页：Logo/标题/「本机」徽章、「远程协助」卡片（9 位码、连接、IP 直连展开区）、「我的设备」列表、底部「设备 / 我的」导航 |
| `SessionView.java` | 会话：视频层 + 顶部悬浮条（返回、设备名、延迟胶囊）+ 底部工具栏（键盘、统计、面板、断开）+ 统计卡 + 提示条 |
| `CodeFieldView.java` | 9 个数字槽 + 隐藏 `EditText`，支持粘贴与退格，焦点槽绿色描边 |
| `DeviceBook.java` | 设备簿：`SharedPreferences` + JSON；保存设备、生成唯一 9 位码、更新最近连接时间、删除 |
| `UiKit.java` | 颜色常量、圆角/描边/涟漪背景、文本、按钮、胶囊、边距 |
| `res/drawable/ic_*.xml` | 单色矢量图标（显示器、返回、键盘、统计、电源、用户），代码里 tint |

设计令牌：背景 `#0E1014`、卡片 `#161A20`、次级面板 `#1F242D`、主色 `#00D26A`、
弱化文字 `#8D94A3`、危险 `#FF5C5C`、警告 `#FFB020`。

### 9 位连接码的含义

连接码由固定信令服务器 `jsspace.xyz:3479` 签发：

1. 被控端开始共享后，服务器生成 9 位数字码（首位非 0），默认 300 秒有效。
2. 本机在首页输入该码，向服务器注册并加入会话；服务器把双方端点与媒体端口互相转发。
3. 媒体密钥由连接码 + 会话 ID 经 HKDF-SHA256 在本机派生，服务器不接触密钥。
4. 之后视频与控制直接走 UDP 点对点。

连接码不会被写入 `DeviceBook`；`DeviceBook` 只保存 IP 直连设备。服务器不可达或码过期时，可展开「使用 IP 地址直连」回退。

信令实现位于 `ServerConfig.java`、`SignalingWire.java`、`SignalingClient.java`、`MediaKeys.java`、`PeerMediaInfo.java`。

## 手势与按键

| 手势 | 事件 |
|---|---|
| 单指按下 / 拖动 | `MouseMove`（触摸板式，不按住左键） |
| 单指点击 | `MouseButton` 左键按下 + 抬起 |
| 双指点击 | `MouseButton` 右键按下 + 抬起 |
| 双指上下拖动 | `Wheel`，每格 120 单位（30 dp 手指位移） |
| 双击 | 收起 / 显示会话控制条 |

- 点击用 `GestureDetector.onSingleTapConfirmed` 判定，避免双击时先向远端注入一次多余点击。
- `KeyEvent.getScanCode()` 是 Linux 输入码，**绝不转发**；`KeyMapper` 用显式表映射到 PC Set 1
  扫描码（含 E0 扩展位）：方向键、Home/End/PgUp/PgDn、Insert/Delete、回车与小键盘回车、
  Backspace、Tab、Esc、空格、左右 Ctrl/Alt/Shift、Win 键、Menu、Caps/Num/Scroll Lock、
  Print Screen、F1–F12、标点、小键盘、字母数字（`ALLOW_LETTER_AND_DIGIT_KEYS`）。
- 没有扫描码的输入（D-pad 中心、媒体键、音量键、软键盘 `commitText`）只提示一次，不猜。
- `onPause` 与断开时发送 `ReleaseAll`，再释放输入租约，避免远端卡住 Ctrl。

## 文件与线程

```
app/src/main/java/org/screencast/app/
├─ NativeCore.java        JNI 声明 + 状态/事件/布局常量
├─ ControlEventType.java  控制事件 id 1..10
├─ InputCodec.java        控制体线格式（与 InputCodec.cs 逐字节一致）
├─ SessionStats.java      sc_stats_t 快照
├─ ViewerSession.java     工作线程：poll、喂解码器、发送输入、丢帧恢复
├─ H264Decoder.java       MediaCodec → Surface，关键帧门控、格式变化重建
├─ KeyMapper.java         Android 键码 → PC 扫描码
├─ TouchRouter.java       手势 → MouseMove / MouseButton / Wheel
├─ VideoSurfaceView.java  按视频宽高 aspect-fit 的 SurfaceView，接收触摸
├─ UiKit.java / CodeFieldView.java / DeviceBook.java / HomeView.java / SessionView.java
└─ MainActivity.java
```

每个会话一个工作线程（`ViewerSession`）独占 Rust 句柄：轮询 `sc_session_poll`、喂解码器、
排空输出。UI 线程只碰 `Surface`、视图和统计文本，工作线程的回调统一 `runOnUiThread`。
解码器方法加 `synchronized`（UI 线程 `setSurface` 与工作线程 `feed`/`drain` 并发），
所有 codec 调用 `timeoutUs == 0`，UI 线程不会等解码。

## 线格式

- 控制体与 `src/ScreenCast.Windows/Input/InputCodec.cs` 完全一致：按键 3 字节
  `scanCode(2) flags(1)`（bit0 扩展 / bit1 按下）；鼠标移动 6 字节 `x(2) y(2) monitor(2)`；
  按键 2 字节；滚轮 4 字节有符号；触摸 7 字节。全部大端。
- 坐标按渲染后的画面归一化到 0..65535，取整方式与 Windows 的 `InputCodec.Normalize` 相同。
- 会话默认值：密钥 `00..1F`（32 字节）、sessionId `A1..B0`（16 字节）、epoch 1、角色 viewer；
  端口与 `docs/deployment.md` 一致。
- 连接时发送 `ControlLease`（体 `01`）取得输入租约；断开时先 `ReleaseAll` 再 `ControlLease` `00`。

## MediaCodec

- `video/avc`，`configure(format, surface, null, 0)`，同步模式。
- `KEY_LOW_LATENCY` 只在 API 30+ 设置。
- 不给解码器传 `BUFFER_FLAG_KEY_FRAME`：IDR 由码流判断，错误标记会让部分解码器报错；
  `SC_EVENT_VIDEO_FRAME` 标志 2（SPS/PPS）才设 `BUFFER_FLAG_CODEC_CONFIG`。
- 第一帧必须是关键帧，之前丢帧并按 500 ms 限流请求关键帧（与 Windows viewer 一致）。
- `INFO_OUTPUT_FORMAT_CHANGED` 尺寸变化（含 crop）会标记重建，并把新尺寸回调给 `SurfaceView`。
- 不连续标志（4）或原生丢帧增加时重置解码器并请求关键帧。

## 构建

固定版本：JDK 17、Gradle **8.10.2**、AGP **8.7.3**、compile/target SDK **35**、minSdk **26**、
NDK **27.2.12479018**、Rust **1.83.0**、cargo-ndk **3.5.4**，ABI `arm64-v8a` + `x86_64`。

```sh
bash scripts/build-android.sh check    # rust fmt/clippy/test
bash scripts/build-android.sh lint     # gradle lintDebug（abortOnError + warningsAsErrors）
bash scripts/build-android.sh native   # cargo-ndk → app/src/main/jniLibs/<abi>/libscreencast_core.so
bash scripts/build-android.sh apk      # assembleDebug
```

`libscreencast_core.so` 是生成物，不进仓库（`jniLibs/` 已忽略）。缺失时面板显示原生库缺失提示，
不会崩溃。Rust crate 还需要导出会话 JNI 符号，见
[`native-jni/README.md`](native-jni/README.md)；否则库能加载、版本能显示，但首次连接报
`reason_bridge_missing`。

## 本轮验证

- 用真实 **API 35 `android.jar`** 对全部 Java 源码执行
  `javac --release 17 -encoding UTF-8 -Xlint:all`：**0 error / 0 warning**。
- 用 Gradle 8.10.2 + AGP 8.7.3 实际执行 `lintDebug assembleDebug`：**BUILD SUCCESSFUL**，
  lint 报告 **No issues found**（工程开了 `abortOnError` + `warningsAsErrors`）。
  lint 抓到一个真问题：直连端口用 `setText("42000")` 触发 `SetTextI18n`，已改为
  `default_video_port` / `default_control_port` 字符串资源。
- GitHub Actions（提交 `532f622`）的 `android` job 全绿：`sdkmanager` 安装
  platform 35 / build-tools 35 / NDK 27.2 → lint → `cargo-ndk` 生成 arm64-v8a + x86_64 的
  `.so` → `assembleDebug`。产物 `screencast-android-debug` **0.53 MB**（含原生库）。
- CI 的 SDK 包含 API 36，lint 因此报 `OldTargetApi`（本地只有 API 35，没触发）。已在
  `app/build.gradle` 显式 `disable 'OldTargetApi'`：升级到 36 需要更新的 AGP/compileSdk，
  这一版有意锁定 35。
- 本机 `app/build/outputs/apk/debug/app-debug.apk` 约 94 KB，不含 `.so`（本机无 NDK），
  只用于本地验证；带原生库的包以 CI 产物为准。

## 未实现

- Host 模式（MediaProjection 同意、前台服务、H.264 编码）。
- 信令 / TURN / 打洞：Viewer 需要知道 Host 的 IP 与两个 UDP 端口，和桌面端一样。
- 文本输入与输入法组合：提交文本没有扫描码，明确提示并丢弃。
- 多显示器选择（`monitorId` 固定 0）、`Touch` 事件、`SetQuality`、`PathSwitch`。

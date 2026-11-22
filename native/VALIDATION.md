# Rust 网络核心验证记录 — 2026-11-21（第六轮）

本轮把 `native/` 从"只有 64 字节头解析 + 角色状态"升级为 Android / macOS 客户端可复用的
真实网络核心：SCT1 帧、AES-256-GCM、可靠控制通道、视频分片重组、双 socket 媒体会话、
保活探测、STUN Binding，以及完整的 C ABI。**协议以 `docs/protocol.md` 为契约、以 C# 实现为
标准答案，逐字节对齐。**

## 执行环境

- Windows，隔离 Rust 1.83.0 GNU 工具链（仅存在于被忽略的 `native/.tools`）。
- 本机无 Rust 在 PATH：一律用绝对路径并设置
  `CARGO_HOME=native/.tools/cargo`、`RUSTUP_HOME=native/.tools/rustup`。
- 本机 MinGW 16（MCF 线程模型）链接需要 `RUSTFLAGS='-C link-arg=-lmcfgthread'`；
  CI 在 Linux 上不需要，也不写进构建配置。

## 真实执行的命令与结果

```text
cargo fmt --manifest-path native/Cargo.toml -- --check
  exit 0（无输出）

cargo clippy --manifest-path native/Cargo.toml --locked --all-targets -- -D warnings
  exit 0（Finished `dev` profile）

cargo test --manifest-path native/Cargo.toml --locked
  exit 0
  unittests src/lib.rs         : 89 passed; 0 failed
  tests/ffi_abi.rs             :  6 passed; 0 failed
  tests/session_loopback.rs    :  7 passed; 0 failed
  doc-tests                    :  0 passed
  合计 102 项通过，0 失败
```

连跑 3 次结果完全一致（UDP 回环测试无抖动/无 flaky）。

额外真实执行（不属于 `cargo test`）：

```text
gcc -std=c11   -Wall -Wextra -Werror -c -I native/include native/tools/header_smoke.c
g++ -std=c++17 -Wall -Wextra -Werror -x c++ -c -I native/include native/tools/header_smoke.c
  exit 0 / exit 0   —— 头文件是合法 C11 与 C++17，且 offsetof/sizeof 静态断言全部成立

cargo build --release
  exit 0 -> native/target/release/screencast_core.dll (1,308,239 字节)

objdump -p screencast_core.dll   （导出表）
  sc_core_version, sc_get_role, sc_set_role,
  sc_session_create, sc_session_bind, sc_session_set_peer, sc_session_start,
  sc_session_send_frame, sc_session_send_control, sc_session_poll,
  sc_session_stats, sc_session_local_ports, sc_frame_release, sc_session_destroy
  —— 14 个符号，3 个旧 + 11 个新，全部导出

python native/tools/abi_smoke.py native/target/release/screencast_core.dll
  40/40 checks passed  —— 进程外 ctypes 加载真实 DLL，两个会话真实 UDP 收发加密视频帧
  与控制事件，字节完全一致；含 sizeof(sc_event_t)=64、sizeof(sc_stats_t)=160、
  data 偏移 48 的布局断言
```

## 与 C# 逐字节一致的验证方式

| 项 | 标准答案来源 | 验证 |
|---|---|---|
| 64 字节头全部字段 | `Packets.cs` + `native/tests/vectors/header-v1.hex` | `matches_shared_wire_vector` 等 6 项 |
| HKDF info / nonce / AAD / tag | `docs/packet-vector.md` | `known_answer_vector_matches_packet_vector_md`：整包 hex 完全一致（含 tag `0a92411a…` 与密文 `c8226b`） |
| AES-256 分组 | FIPS-197 C.3 | `8ea2b7ca516745bfeafc49904b496089` |
| AES-256-GCM | NIST 全零向量 | `cea7403d…` / `d0d1c8a7…` |
| SHA-256 / HMAC / HKDF | FIPS 180-4、RFC 4231、RFC 5869 | 全部通过 |
| 32 字节控制头、哨兵 ackBase | `ControlCodec.cs` | 编解码 + 畸形拒绝 |
| 64 发送窗口 / 64 重排窗口 | `ReliableControlChannel.cs` | 第 65 条入队返回 WindowFull；窗口外 Data 丢弃但仍回 ACK |
| 重复 Data 必回 ACK | 同上 | `duplicate_data_is_acknowledged_again_but_executed_once` |
| RTO = clamp(SRTT+4·RTTVAR,30ms,1s)、Karn | 同上 | 21 ms 样本 → 63 ms RTO；重传样本不采样 |
| 8 次 / 2 秒过期并上报 | 同上 | 两条独立测试 |
| 32 字节分片头与上限 | `VideoFraming.cs` | 分片数、缺片丢弃、过期清理、4 帧并发 |
| 32 字节探测报文 | `PathProbe.cs` | 发送不刷新存活、3 次静默 Down、迟到响应复活 |
| STUN 头/cookie/XOR/FINGERPRINT | `StunBindingCodec.cs` | IPv4/IPv6 往返、CRC 校验、420 响应 |

## 真实 UDP 回环实测（`tests/session_loopback.rs`）

| 测试 | 结果 |
|---|---|
| 两个会话互发 4000 字节与 20000 字节加密视频帧 | 字节完全一致，viewer 重组 2 帧，0 拒绝 |
| viewer → host 可靠控制事件 | 按序到达，body 一致，host 有 ACK 字节计数 |
| 伪造 tag / 非 SCT1 数据报 | 计数丢弃，无帧产出，会话继续 |
| 重放同一数据报 | `replay_rejected == 1`，不产生第二帧；随后新包仍被接受 |
| 缺中间分片 | 300 ms 内无帧，`assembled_frames == 0`，`partial_frames == 1` |
| 100 ms 保活探测 | 两端都收到 Alive，`probes_answered > 0`，RTT ≥ 0 |
| 连续 5 条控制事件 | 严格按 0..4 顺序交付，无重复 |

## 未实现 / 未验证（明确边界）

- **SCSH 信令握手**（ClientHello/Cookie/ClientAuth/ServerAuth/Finished、P-256 ECDH、
  transcript 签名）未实现，密钥由平台层通过 `sc_session_create` 直接注入。
- **TURN** 未实现（STUN Binding 发现公网映射已实现并有测试；TURN 的 Allocate/Refresh/
  ChannelBind/MESSAGE-INTEGRITY 全部没有）。
- **打洞与候选选择**（`PunchCoordinator`/`LinkController` 等价物）未移植；会话只接受显式
  对端地址。
- **抖动缓冲、自适应延迟、FEC、关键帧请求策略、编解码、采集、解码、输入注入**均未实现。
- **一个 socket 对多会话分派**未实现：一个会话独占一对 socket。
- **Android 交叉编译检查未在本机跑通**：`cargo clippy --target aarch64-linux-android --lib`
  在交叉编译时不会把 `RUSTFLAGS` 的 `-lmcfgthread` 传给宿主 build script，而本机 MinGW 的
  MCF 运行时必须显式链接（`jni` 的 build script 因此链接失败）。这是**本机工具链限制**，
  与代码无关：CI 在 ubuntu-24.04 上跑同样的命令（`scripts/build-android.sh check` 与
  Android job 的双 ABI clippy），那里没有 MCF 问题。本轮未在 CI 上实际触发。
- 未做性能基准。AES 与 GHASH 是字节级参考实现（无 AES-NI/PCLMUL 优化），当前媒体码率下
  够用，但大码率场景应替换为硬件加速实现。

## 与 C# 互通的现状

字节层完全一致（同一已知答案向量、同一 HKDF/nonce/AAD、同一 ACK 语义），但**尚未做
Rust ↔ C# 进程间对跑**：C# 侧的 `MediaSession` 目前不发 channel 3 探测，所以 Rust 端对着
C# 对端时探测不会得到响应，30 秒后本地路径会显示 Down（视频/控制不受影响）。C# 侧补上
探测后即可消除该差异。

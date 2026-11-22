# 实施路线、风险与MVP目录

## 交付顺序

本轮先完成资产审查与根guide，再协议/架构/接口/状态机，再可构建工程基础和CI。完整产品需要硬件、两端公网与签名资源验收；基础工程不能称为UU Remote等价成品。

| 阶段 | 实现范围 | 完成门槛 |
|---|---|---|
| P0 本轮基础 | 文档、包格式、AES-GCM/重放、STUN Binding、连接策略、平台工程/CI | .NET构建和测试实测；移动CI配置与平台依赖可追踪 |
| P1 Windows本地媒体 | DDA生命周期/多屏/dirty+move，D3D11色彩转换，单一可用H.264硬编，硬解显示 | 本机loopback1080p30连续30分钟，无COM/内存增长；测捕获/编码/解码耗时 |
| P2 公网直连MVP | 认证UDP信令、设备/Host授权、P-256协商、候选交换、双socket P2P、可靠键鼠 | 两台不同NAT电脑，DDA+H264+P2P+键鼠；丢包/乱序不重复按键；断线ReleaseAll |
| P3 Windows Server TURN | 标准认证、分配、permission/channel、刷新、配额、清理、Windows Service | Windows Server2022独立部署，独立TURN测试客户端互通；3轮失败/对称NAT自动中继 |
| P4 可靠性与体验 | WGC/GDI回退，NVENC/AMF/QSV硬件矩阵，AV1协商，自适应，状态面板/日志导出 | 三厂商测试，弱设备H264，受限网络恢复，多屏/DPI/旋转 |
| P5 移动Viewer | Rust完整协议与.NET向量互通，Android MediaCodec，Swift VideoToolbox | APK真机播放控制Windows；iPhone/iPad真机播放；签名IPA导出 |
| P6 移动Host/扩展 | Android MediaProjection，iOS ReplayKit/Broadcast，音频，多Viewer/虚拟屏 | 明确平台能力、后台恢复、输入权限；每Viewer独立密钥，输入租约 |
| P7 区域与规模优化 | ROI、独立图块协议、FEC、连接迁移、TURN TCP/TLS、压测 | 图块基线恢复、弱网画面不永久污染、容量与延迟基线 |

MVP指P1+P2验收，不包括仅能打开界面的原型。公网正式使用还需P3兜底。先H.264跑通，再AV1；优先网络/输入正确性，再画质扩展。

## 验证矩阵与量化指标

- 协议：固定字节向量、C#/Rust互读、空包/短包/超长/未知version、IPv4/6 STUN、tag篡改、epoch错、窗口边界、重复包；随机fuzz输入不触发大分配。
- 可靠控制：0/1/5/10%丢包，乱序/重复，ACK丢失，键按下后断网；有界窗口、只执行一次、释放状态。
- 媒体：Windows NVIDIA/AMD/Intel；DDA AccessLost、锁屏/解锁、显示器插拔、HDR→SDR、DPI125%/150%、旋转；encoder拒绝参数时明确降级。
- 性能起点：1080p30 SDR、H.264 8Mbps、端到端LAN p95目标<100ms、输入LAN p95<40ms；这是目标不是实测结论。采集/编码/发送/解码/显示分别计时。
- 网络：同LAN、异运营商、双NAT、CGNAT、对称NAT、UDP限速、单方向阻断、TURN故障、端口池耗尽、休眠恢复。只跑localhost不能证明打洞成功。
- 服务：4核/4GB机器24小时soak，活动会话/句柄/端口/内存回到基线，限流下合法控制仍有预算；外部标准客户端验证TURN。
- 发布：Windows自包含EXE在无SDK的Server2022/Windows10+上运行；APK真机；iOS archive与签名IPA分开记录。Actions绿灯不代替GPU/公网验收。

## 风险清单与处理

| 风险/触发 | 处理与验收 |
|---|---|
| 反作弊或特定窗口使DDA黑屏 | 用户选择WGC窗口采集，检测连续黑帧但避免误把黑桌面视为故障；实测目标应用，承认可能仍受限制 |
| DRM/受保护视频 | 明确标识捕获受保护内容时的黑屏状态，不承诺WGC/GDI能恢复 |
| ISP UDP QoS/限速 | pacing/带宽上限/自适应fps分辨率，记录loss和jitter；TURN UDP同样可能受限，后续TCP/TLS扩展 |
| 对称NAT/多层CGNAT | 观测到对称映射直接TURN，Unknown走3轮实际探测；不把单STUN失败归为对称 |
| 只有一个公网IP | STUN/TURN正常使用，四类NAT诊断降为Unknown；需要第二IP才能完整RFC5780 |
| 旧设备AV1硬解不足 | decode能力+性能协商，用户禁用AV1、强制H264、720p30；MacBook2015例子是能力约束，macOS app另排计划 |
| GPU驱动/硬编会话数限制 | 实际Probe+试编码，按adapter选择，限制多Viewer档位数；fallback必须在状态面板展示 |
| dirty rect误用 | MVP跳静止帧+ROI，完整H264语义；图块另协议、基帧/关键刷新 |
| GCM nonce重用/ACK伪造 | 分方向/流密钥、epoch+序号、不回绕；认证后重放提交；测试重传与重连 |
| 视频拥塞挤占控制 | 两socket/队列+video pacing；共享网卡仍有竞争，发送预算保留控制优先级 |
| Host按键卡住/本地锁定 | 控制租约、超时ReleaseAll、紧急热键、禁止注入事件回采 |
| iOS系统输入限制 | Host能力声明仅屏幕分享；Viewer可控制Windows；公开API不支持的能力明确显示 |
| Android投屏/后台限制 | 每次依法定平台流程获取MediaProjection token，前台service，系统撤销即结束；网络唤醒重连 |
| Apple签名资源缺失 | 无签名archive保留为构建产物，有证书/profile/Team才导出IPA；不伪装TestFlight包 |
| 自研TURN规范复杂 | 分阶段实现401/438/permission/channel等状态，与独立实现互通后才标RFC合规 |
| 参考ZIP依赖不完整 | 仅学习证据中可见模块；独立实现与锁定依赖；不依赖本地缺失子模块 |
| .NET8生命周期 | 按用户要求采用.NET8；建立后续LTS迁移任务及WindowsServer2022回归矩阵 |

## MVP目录布局

```text
ScreenCast/
  guide.md
  ScreenCast.sln
  Directory.Build.props
  .editorconfig / .gitignore
  docs/
    architecture.md       文本架构、模块依赖、接口
    protocol.md           固定头、加密、控制、视频、RFC边界
    state-machines.md     NAT/打洞/中继/恢复/清理
    roadmap.md            迭代、风险、验收
    validation.md         实际检查与待验收项
  src/
    ScreenCast.Core/      net8协议/加密/状态与抽象
    ScreenCast.Server/    net8独立服务，逐阶段STUN/Signal/TURN
    ScreenCast.Windows/   WPF统一角色UI、Vortice平台适配
  tests/ScreenCast.Core.Tests/
  native/                 Rust移动核心、C头文件、跨语言向量
  apps/android/           Java/Gradle/JNI集成
  apps/ios/               Swift/XcodeGen/C ABI集成
  scripts/               构建/打包/部署辅助
  .github/workflows/     windows/mobile/release
  artifacts/             忽略，CI与本地产物
  preference/            保留只读参考，不加入新工程编译
  Client for */          原有空输出目录保留
  p2p Server Info/        本地部署资料，忽略提交
  Server Software/       原有输出目录保留
```

统一输出artifacts防止空格目录成为脚本隐患；旧目录保留原状。首次Git初始化/远端发布独立于本地源码创建，本轮不自动上传服务器信息或参考ZIP。

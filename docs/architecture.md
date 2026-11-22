# 架构与模块依赖

## 定位与边界

Windows统一客户端一个自包含EXE，`--mode host|viewer`或UI切换；切换时停止当前会话并释放采集/输入租约。服务端是单独.NET8 Windows Service/控制台程序；一个进程托管三个子系统，独立队列与资源配额。无GPU要求，不转码。移动端共享Rust网络/协议实现，Java/Swift负责平台采集、硬件codec和显示。macOS目录现仅预留，首轮不将iOS当作macOS客户端。

## 文本架构图

```text
 Windows Host / Android Host / iOS Screen Share
   平台采集 -> 脏区域/变更判断 -> GPU硬编 -> 帧分片 -> AES-GCM
       ^                                                |
   显式开启Host与可见会话状态                     Video UDP socket
       |                                                |
   输入策略 <- 去重/有序控制 <- AES-GCM <------- Control UDP socket
                                                        |
                   P2P直连优先 / TURN透明中继             |
                                                        v
 Viewer: Video UDP -> 认证/去重 -> 有界重组/抖动缓冲 -> 硬解码 -> Surface
         Control UDP <- 可靠队列 <- 鼠标/键盘/触摸/UI

 两端独立信令客户端 ------------ 加密UDP信令 ------------+
                                                        |
 Windows Server 2022 (.NET8)                             |
 +------------------------------------------------------+
 | Signaling: 设备身份/授权、会话、候选、SDP、公钥转发、租约 |
 | STUN: Binding + 可选RFC5780双IP行为探测                 |
 | TURN: 认证、allocation、permission、channel、配额/转发   |
 | 公共: 单调计时、限流、结构化日志、指标、过期清理          |
 +------------------------------------------------------+
```

## 模块依赖图

```text
Windows.App(WPF) -> Client.Session -> Core.Contracts / Core.Protocol
                -> Windows.Capture(DDA/WGC/GDI) -> Vortice.DXGI/D3D11
                -> Windows.Codec -> NVENC/AMF/QSV adapter
                -> Windows.Render -> D3D11 decode/render
                -> Windows.Input -> SendInput / RawInput
Client.Session -> Signaling.Client / NatDiscovery / LinkController
LinkController -> DirectTransport / Turn.Client -> AsyncUdpSocket
Transport -> PacketProtection / ControlReliability / VideoFraming / Metrics
Server.App -> Signaling.Server + Stun.Server + Turn.Server
Server subsystems -> SessionRegistry / Credentials / Quotas / Core.Protocol
Android.Java -> JNI -> Rust.Core -> protocol/crypto/nat/transport/session
Android.Java -> MediaProjection / MediaCodec / Surface
Swift.App -> C ABI -> Rust.Core
Swift.App -> ReplayKit / VideoToolbox / Metal
```

Core不依赖UI、GPU、WindowsService。Server不引用任何采集/codec程序集。协议fixture同时供.NET与Rust测试；两套实现保持同一wire version。

## 关键模块接口

接口签名是契约，具体适配器按里程碑实现；所有异步方法接受CancellationToken（Rust等价取消句柄），所有流和队列有容量。

| 模块 | 输入/输出契约 | 生命周期/错误 |
|---|---|---|
| ICaptureSource | EnumerateDisplays→DisplayInfo[]；Start(CaptureOptions)；Capture→FrameLease(texture,dirtyRects,moveRects,cursor,pts) | 单owner线程；Dispose归还帧；AccessLost重建；显示器移除重新选择 |
| IVideoEncoder | Probe→CodecCapabilities；Configure(profile)；Encode(FrameLease)→EncodedFrame[]；RequestKeyFrame；Reconfigure | NV12转换/adapter LUID兼容；不支持返回CapabilityError，不静默伪装硬编 |
| IVideoDecoder | Probe；Configure(configId,codec)；Decode(EncodedFrame)→DecodedSurfaceLease | 参数变化重建；损坏参考链丢弃并请求IDR |
| IRenderer | Present(surface,viewport)；SetZoom；SetFullscreen | 显示线程拥有交换链；Surface释放明确 |
| IInputSource | InputEvents→有界流；SetMonitor/CoordinateTransform | 区分本地/注入输入，避免回环 |
| IInputSink | AcquireLease(viewerId)；Apply(InputEvent)；ReleaseAll | Host显式授权；断线/失焦/租约结束释放键状态 |
| ISignalingClient | Register；Create/Accept；ExchangeCapabilities/Keys/Candidates；Events | requestId幂等、取消、设备认证、转录确认 |
| INatDiscovery | Discover(boundSocket,servers)→NatObservation(mapping,filtering,legacyLabel,confidence,endpoints,time) | 每component独立；Unknown是合法结果 |
| ILinkController | Start(session)；OnProbe/Timeout/NetworkChange；SwitchPath | 控制状态机唯一写者；3次失败relay；新generation丢弃旧结果 |
| IDatagramTransport | Send(channel,ReadOnlyMemory)；Receive→Datagram(remote,bytes) | socket固定，有界池化buffer，取消释放 |
| ITurnClient | Allocate(component)；CreatePermission(peer)；BindChannel；Send；Refresh；Dispose | allocation/permission/channel三个独立计时器 |
| IPacketProtector | Seal(header,plain)；Open(datagram)→verified payload | nonce唯一、认证后重放提交、密钥销毁 |
| IReliableControl | Enqueue(event)；OnAck；Tick→重传；OrderedEvents | 消息序号与packet seq分离；窗口64；总超时重连 |
| IVideoAssembler | Push(fragment)；Poll(deadline)→完整帧 | 限制帧数/字节/片数，过期丢弃，无重传 |
| IQualityController | Observe(LinkStats,DecodeStats)→EncoderSettings | EWMA和滞回，避免频繁变更；带宽上限硬约束 |
| ISessionRegistry | Create；Find；TouchAuthenticated；Close；Sweep | 随机ID、每Host最大Viewer数、超时原子移除 |
| IStunService | Handle(bytes,source,localEndpoint)→response | RFC parser；速率限制；无持久未认证会话 |
| ITurnService | HandleControl/ChannelData；RelayReceive；Sweep |五元组、身份、配额、端口池；绝不解码视频 |
| IDiagnosticLog | Write(level,eventId,fields)；Export(target) | 滚动大小/保留天数；密钥和输入内容排除 |

数据契约：FrameLease采用显式引用计数/IDisposable，禁止在DDA ReleaseFrame后继续异步使用未复制纹理；EncodedFrame含frameId/configId/codec/keyframe/pts及租借buffer；Capability包含encode与decode分开列表、profile/level、最大尺寸/fps、位深、硬件标记。

移动C ABI只使用固定宽度整数、指针+长度、opaque handle；`sc_create(role)`、`sc_start(handle,config,len)`、`sc_poll_event(handle,buffer,capacity)`、`sc_submit_encoded_frame`、`sc_send_input`、`sc_stop`、`sc_destroy`；跨FFI不抛异常、不传Rust String/Swift对象。平台持有Surface，Rust仅持opaque token并回调归还；回调转发到正确UI/codec队列。具体当前导出函数以代码为准。

## 媒体与输入策略

MVP固定H.264 8bit SDR/1080p30起步，B帧关闭、低延迟、短GOP。后续双方Probe确认AV1硬件encode+decode且性能足够才优先AV1；强制codec无共同能力时明确协商失败，用户可禁用AV1。NVENC/AMF/QSV不是按厂商名称盲选，按adapter、可用codec、试编码耗时选择。弱设备H.264/720p30；软编仅显式fallback并限制分辨率。

DDA读取dirty/move rect，维护GPU完整桌面缓存，先move后dirty，变更为空跳过；光标另外更新。ROI给支持的encoder；不支持ROI仍全帧编码，帧间预测处理未变区域。真正只发变化图块需后续TileFrame协议、基线版本、周期全屏刷新和区域合成，不能把任意矩形截出来作为同一H.264流。

WGC优先用于用户选定窗口，遇DDA AccessLost/黑帧时提供明确选择；受保护内容和部分反作弊捕获限制仍可能黑屏。GDI仅兼容。多显示器记录坐标、DPI、旋转；输入坐标用捕获区域逆变换。可选本地输入独占由短期租约控制，紧急热键始终可释放；不将BlockInput长期状态作为唯一实现。

Android Host使用MediaProjection显式系统授权、前台mediaProjection service、VirtualDisplay→MediaCodec输入Surface；Viewer用MediaCodec→Surface。iOS应用内采集用RPScreenRecorder，跨应用共享需Broadcast Upload Extension，采样、持续编码及网络发送在该扩展进程中运行并受其资源预算约束，不能依赖主App在后台常驻；iOS公开API不支持通用系统输入注入，Host为屏幕分享能力。App能力声明inputInjection=false，Viewer仍能控制Windows Host。移动省电/后台挂起需重连，不能承诺无限后台socket保活。

## 服务器部署容量

默认UDP信令3479，STUN/TURN3478，行为探测备用3477；TURN中继UDP49152–49251，均配置化。RFC5780需要A1/A2两公网IP且每IP开放同两STUN端口；一个IP时普通STUN/TURN仍可用，NAT详细类别为Unknown。Windows Firewall与云防火墙都按端口配置，公网IP映射必须准确；不自动远程部署。

4核/4GB机器初始上限32会话、每设备2会话、每Host单Viewer默认、每allocation10Mbps与全局100Mbps可配置；实际取决于链路，不能保证32路1080p。TURN转发一条码率R流产生R入站+R出站，双端都relay时还有relay间转发成本。使用ArrayPool和固定队列预算，单会话最多4MiB待发，超过时丢视频、保留控制预算；中继端口池耗尽返回508。

服务以专用低权限账号运行，配置与身份密钥由部署管理员设置；提供Windows Service恢复策略与控制台诊断模式。每源IP令牌桶、每认证设备配额、全局会话/端口/内存上限、短期cookie防伪源、异常报文静默丢弃或标准受限错误响应。服务端日志不记录SDP原文中的私密地址或密钥。

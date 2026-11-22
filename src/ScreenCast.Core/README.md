# ScreenCast.Core 实现边界

目标框架 net8.0；仅依赖 BCL。以 ../../docs/protocol.md 的 v0.1 为契约，没有复制 preference 源码。

## 已实现

- SCT1 固定64字节头：逐字段网络端序，SessionId 原始16字节，严格校验版本、长度、保留位、channel、epoch 和1200字节UDP上限。
- AES-256-GCM：AAD 为头前48字节，16字节Tag，nonce=epoch大端4字节+sequence大端8字节。HKDF-SHA256盐为原始SessionId；info 为 ASCII `ScreenCast/AES-256-GCM` + version(u8=1) + session(16) + epoch(u32 BE) + direction(u8，HostToViewer=0/ViewerToHost=1) + channel(u8) + stream(u32 BE)。该精确HKDF布局是本实现对协议通用说明的具体化，其他语言端必须逐字节匹配并添加共享向量验收。
- 每个 PacketProtector 固定一个会话/方向/channel/stream/epoch，上锁保护发送序号和接收窗口；序号含ACK从0分配，溢出前停止，认证成功后提交1024包环形防重放窗口；篡改包不会推进窗口。
- RFC5389 Binding 20字节头、属性TLV四字节对齐、IPv4/IPv6 XOR-MAPPED-ADDRESS、成功响应来源/事务校验、未知必需请求属性420及去重的UNKNOWN-ATTRIBUTES。响应允许同时携带合法MAPPED-ADDRESS，使用XOR-MAPPED-ADDRESS作为结果；FINGERPRINT属性出现时校验CRC32/长度/末尾位置（RFC5769向量测试），当前编码器未自动添加FINGERPRINT。
- 可注入稳定socket传输的Binding异步事务：初始RTO500ms，7次发送，指数退避，末次8秒等待；取消和超时可观察。接收源由单一socket owner分派，使用IBindingTransport接入。实际使用时可基于网络测量调整RTO，当前默认固定。
- 连接策略：两路Video/Control均确认才进入Direct；三次失败进入Relay请求态；对称NAT观察直接进入Relay；两路真实分配及路径确认后才由调用者进入ConnectedRelay。Relay并非成功标志。支持generation忽略旧路径完成事件。
- ISignalingClient、ITurnClient、IFrameCapture、IVideoEncoder 是未来实现接口，无伪成功占位实现。

## 责任与未实现项

PacketProtector 不执行身份认证、ECDH或SCSH握手。sharedSecret 必须来自经过验证的会话密钥协商。调用者必须保证同一会话/方向/channel/stream/epoch只创建一个发送实例并保留至epoch结束；进程重启必须使用全新共享秘密/会话或安全推进epoch，不能复用旧上下文从零发送。本库不持久化密钥和计数器、不自动轮换密钥。释放后的对象不可复用。Encode和Decode共用单个方向context并不表示双向密钥通用；发送端的HostToViewer实例与对端的HostToViewer接收实例互通，反向必须建立ViewerToHost上下文；每个方向由单一socket owner管理和分派。

STUN仅为公开Binding基础；没有MESSAGE-INTEGRITY、长期认证、TURN Allocate/Refresh/Permission/ChannelBind、信令注册、媒体采集/编码、可靠控制重传、视频重组、带宽控制或生产运维能力。服务将所遇到的全部comprehension-required属性视为当前服务未支持属性，返回420；可选属性忽略。因此不能宣称完整RFC5389认证客户端/服务端，更不能宣称TURN/完整远程桌面已经实现。

NatClassifier仅比较同一媒体socket对不同目的地址获得的映射；映射变化观察为Symmetric，映射相等返回Unknown，无法仅凭一次Binding证明NAT类型或可打洞。调用方必须保障“同一socket”前提。状态机不执行网络探测、不计时、不注入输入；确认方法只应在实际认证路径成功后调用。Video与Control需要分别发现/确认。

## 验证

从仓库根目录执行：

```powershell
dotnet build src/ScreenCast.Core/ScreenCast.Core.csproj
dotnet run --project tests/ScreenCast.Core.Tests/ScreenCast.Core.Tests.csproj -p:RollForward=Major
```

测试为无依赖console harness，断言失败会抛异常并返回非零；它不是dotnet test/xUnit适配工程。覆盖网络端序、篡改后有效包仍可接受、重复/过期/乱序包、上下文密钥隔离、长度/保留位、ACK序号、RFC5769 IPv4/IPv6 XOR向量、来源/事务检查、420、三失败/对称NAT、真实IPv4/IPv6 loopback UDP收发。RollForward=Major用于仅安装.NET10运行时的开发机，部署建议安装.NET8运行时或按目标RID发布self-contained。

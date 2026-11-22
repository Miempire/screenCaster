# NAT、P2P、TURN与恢复状态机

## NAT行为探测

四个传统标签是观测简写，映射与过滤行为分别存储，附测试时间与置信度。RFC5389只给反射地址；RFC5780是实验性行为诊断，实际连通性由打洞验证。[标准依据](https://www.rfc-editor.org/rfc/rfc5780.html)。

服务器A1:P1、A1:P2、A2:P1、A2:P2。每个视频/控制component分别测试。过滤测试使用尚未联系A2的全新socket，并保持它为实际业务socket；必须先测试过滤，再进行向A2的映射测试，避免主动发包改变过滤结果。

```text
Bind stable socket
 -> Binding A1:P1（随机96bit txid，RTO500ms指数退避，有总期限）
    无响应 -> 尝试备用可信STUN
       全部无响应 -> UdpUnreachableOrServerUnavailable（不能断言对称NAT）
    成功 -> 验证cookie/txid/source/长度 -> 记公网M1
       无OTHER-ADDRESS或服务器不支持行为探测 -> Unknown + 继续实际P2P
       有A2:P2 -> Filtering Test:
          对A1:P1发CHANGE-IP+PORT，请求A2:P2回应
          收到匹配响应 -> EndpointIndependentFiltering
          否 -> 对A1:P1发CHANGE-PORT，请求A1:P2回应
                 收到 -> AddressDependentFiltering
                 否 -> AddressAndPortDependentFiltering（多次探测仍一致才高置信）
       Mapping Test（仍同一业务socket）:
          Binding A2:P1 -> M2
          M1==M2 -> EndpointIndependentMapping
          M1!=M2 -> Binding A2:P2 -> M3
             M2==M3 -> AddressDependentMapping
             M2!=M3 -> AddressAndPortDependentMapping
```

映射测试缺响应记Unknown，不能把超时当映射变化。公网端口相同且与实际路由选择的本地IP/端口相同可记NoNatObserved；不能拿IPAddress.Any:port比较。双层NAT结果代表整条路径当时的行为。地址相关映射或地址端口相关映射按用户策略标记Symmetric并直接TURN。

| 映射 | 过滤 | 传统UI标签 |
|---|---|---|
|EndpointIndependent|EndpointIndependent|FullCone|
|EndpointIndependent|AddressDependent|RestrictedCone|
|EndpointIndependent|AddressAndPortDependent|PortRestrictedCone|
|AddressDependent或AddressAndPortDependent|任意|Symmetric|
|未知/测试缺失|任意|Unknown|

IPv6优先直接候选，不套用IPv4 cone类别。局域网候选也需认证探测，避免同NAT无hairpin导致误判。

## 建链完整流程

```text
Idle -> BindSockets -> SignalingHandshake -> Registered
 -> Create/Join -> AwaitHostAccept -> PeerKeyExchange -> KeyConfirmed
 -> DiscoverEachComponent -> ExchangeCandidates(generation)
 -> SelectStrategy
     任一端任一必需component为Symmetric -> AllocatingRelay
     其他 -> Punching(attempt=1)
        同步起点相对接收PunchStart计时，不依赖墙钟一致
        0/100/250/500/900ms向允许候选发加密Probe，每轮最长1500ms
        双方向有效Probe/Reply + 双component确认 -> CommitDirect -> StreamingDirect
        轮超时 -> attempt+1；attempt<=3继续（可刷新候选）
        第3轮失败 -> AllocatingRelay
 -> AllocatingRelay
     每component Allocate -> 401 realm/nonce -> 完整认证重试
     成功 -> 交换relay候选 -> CreatePermission -> ChannelBind
     经relay加密Probe+KeyConfirm -> CommitRelay -> StreamingRelay
     438 -> 更新nonce有界重试
     486/508/认证失败/总超时 -> Reconnecting或Failed（带原因）
```

一次attempt是一轮候选检查，不是一个UDP包；3轮上限约4.5秒，另加信令/探测时长。两个component必须都可用才提交同一path，MVP避免半直连半中继；后续允许混合路径须UI分别展示。PunchStart绑定session、generation、密钥epoch，任何旧generation应答无效。

每个relay allocation先验证peer permission才转发；一侧或双侧relay均可用。两端都中继时各自permission指向对方relay地址，通过中继地址转发；不要用对方信令端口代替。SCT1内层始终保持端到端加密。

## 在线、切换与恢复

```text
StreamingDirect/Relay
 -> 每socket 10s keepalive，连续N=3次无有效回复 -> Suspect
 -> 暂停输入、ReleaseAll、保留短期显示最后一帧并标离线
 -> Reconnecting：取消旧generation定时任务、递增generation
 -> 指数退避1/2/4/8/16/30s（±20% jitter）
 -> SignalingHandshake -> 新PeerKeyExchange/epoch -> 再探测/打洞

Direct质量劣化持续3个统计窗口
 -> 并行准备Relay，旧路径仍工作
 -> 两component RelayProbe通过 -> PathSwitch提议(generation+1)
 -> 对端ACK/Commit -> 新路径启用 -> 请求新关键帧
 -> 旧路径2秒宽限后释放，去重防双注入

Relay在线 -> 每60s允许一次Direct探测（仅未知/非Symmetric且配置允许）
 -> 连续3次探测好且RTT收益>20ms持续5s -> PathSwitch/ACK/Commit
 -> 失败保留Relay；切换后30s冷却，避免抖动
```

网络接口/IP变化立即触发新generation并重新建socket/发现映射；密钥重新协商，禁止复用旧epoch密钥重置packet sequence。设备睡眠/唤醒、显示模式变化分开处理：网络恢复不等于DDA资源恢复。

UDP完全封锁时UDP TURN也不可达；用户当前UDP传输范围内返回明确错误，未来TURN over TCP/TLS可改善客户端到中继段，不能在设计中保证UDP TURN解决封锁。

## 服务器与资源状态

Session: Created(30s邀请期限) -> Accepted -> Negotiating(30s) -> Active -> Closing -> Removed。
设备每15s认证信令心跳，45s租约超时；会话每10s touch，45s无认证活动清理。定时器每5s扫描，单调时钟，清理幂等并先取消任务再关闭socket/归还端口。TURN lifetime遵循自己的标准计时，不能只靠信令touch代替Refresh。

清理包括：映射、短期凭证、allocation/permission/channel、发包队列、密钥、防重放状态、重组缓存、输入租约。多Viewer按会话独立清理；Host退出广播终止并释放全部租约。

## 验收事件序列

1. 可直连两端：两component Ready、三轮内Direct、关闭信令服务后现有数据面短期继续但授权租约到期终止。
2. 对称映射：零次Punch、立即Allocate；检查401→认证→permission→channel→加密数据。
3. Drop前两轮：第三轮成功；Drop三轮：恰好一次启动Relay。
4. 乱序/重放/伪造ACK：合法输入一次且有序，错误tag不推进接收窗口。
5. 单方向断网/一component断网：N周期断线，两侧释放按键；恢复后新epoch。
6. TURN刷新失败/端口耗尽：明确错误、有界重试、无资源泄漏。
7. 地区/运营商/CGNAT/双NAT/无hairpin/IPv6分别实测，记录抓包与状态时间线。

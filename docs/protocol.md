# ScreenCast UDP 协议 v0.1（设计契约）

状态：实现与测试以 validation.md 为准。所有整数网络字节序；禁止序列化原生 struct 内存。Windows C# 与移动 Rust 使用同一向量。

## 1. 应用数据报

固定头64字节；总UDP负载上限1200字节（TURN ChannelData还占4字节，TURN Send indication按实际属性开销进一步缩减）。应用明文最大1136字节，TURN ChannelData模式最大1132。路径MTU探测只允许减小到已验证尺寸。

| 偏移 | 长度 | 字段 |
|---:|---:|---|
|0|4|Magic ASCII SCT1（0x53435431）|
|4|1|Version=1|
|5|1|Channel: 0=信令,1=视频,2=可靠控制,3=链路探测,4=可选音频|
|6|2|Flags: bit0=ACK-only，其余保留为0|
|8|16|SessionId，16个随机原始字节（不是Guid混合端序）|
|24|4|KeyEpoch，从1开始；密钥更新递增|
|28|4|StreamId，逻辑流/可靠性域|
|32|8|PacketSequence，每个方向/通道/流/epoch从0递增|
|40|2|PayloadLength，仅密文长度，等于明文长度|
|42|2|HeaderLength=64|
|44|4|Reserved=0|
|48|16|AES-GCM Tag|
|64|N|AES-256-GCM密文|

AAD=头部[0,48)，Tag长度16。密钥通过HKDF-SHA256从每对端会话共享秘密派生，info明确含版本、SessionId、epoch、方向(HostToViewer/ViewerToHost)、channel、streamId及用途。nonce为epoch(4字节)||PacketSequence(8字节)。每个方向/通道/流使用不同密钥；同一密钥/epoch禁止序号回绕或进程重启后归零。ACK也消耗独立packet sequence。接收端按会话/方向/通道/流/epoch保持1024包防重放位图，认证成功后才能提交窗口。

## 2. 信令和密钥协商

UDP 连接不是 TLS；不能明文发送 AES key。首阶段握手单独封装为 SCSH（magic4/version1/type1/length2/transactionId16/body），最大1200字节，不带应用加密头。握手消息只允许ClientHello/Cookie/ClientAuth/ServerAuth/Finished，含32字节随机数、P-256临时公钥、身份公钥与签名。部署时钉住服务端身份公钥；设备预先登记公钥。签名覆盖协议版本、角色、双方nonce、公钥、cookie、transactionId及协商算法，验证身份后ECDH+HKDF生成客户端-服务器信令方向密钥，Finished对转录哈希做确认。Cookie由服务端HMAC绑定源地址、事务和短期时间桶，回包不超过初始请求，验证前不分配大状态。逐字段线格式、公钥/签名编码、转录、信令连接ID及Finished定义见[handshake.md](handshake.md)。此握手需独立审查、完整已知答案向量和负向测试；核心AES类不是握手实现。

加密信令channel0消息使用有界UTF-8 JSON：`type, requestId, body`；requestId是128位随机字符串，缓存幂等响应30s。每条消息<=8KiB，分片器限制8片/2秒/每设备4条并发。每片有messageId16/index2/count2/totalLength4，按应用包独立认证；无界SDP拒绝。

消息契约：Register(deviceId,roles,capabilities)、CreateSession(hostId)、Invite(sessionId,viewerId)、Accept/Reject、CandidateUpdate(generation,component,endpoints,natObservation)、Offer/Answer(sdp,codecCaps,maxBitrate,fps,dimensions)、PeerKeyOffer/Answer(ephemeralKey,deviceSignature,transcriptHash)、PunchStart(generation,attempt,durationMs)、RelayCredentials(username,password,expires,realm)、PathCommit(generation,path,components)、Heartbeat、Close(reason)。Host批准并校验Viewer身份后才交换PeerKeyOffer/Answer；服务器只转发签名临时公钥，端点派生媒体/控制密钥并做KeyConfirm，服务器无媒体密钥。每个Viewer独立sessionId、密钥和权限。

SDP按RFC8866表达媒体能力，使用私有transport标识明确为ScreenCast，不宣称与WebRTC/DTLS-SRTP互通。候选endpoint与实际媒体socket绑定，不使用信令socket地址代替。

## 3. 双通道与控制可靠性

每个会话两个稳定UDP socket：Video与Control，各自STUN/打洞/TURN allocation/keepalive；探测走各自socket。信令可独立socket。一个接收循环先匹配完整SCT1/SCSH magic，再检查STUN类型与cookie，再检查TURN ChannelData。ScreenCast分配TURN channel时保留0x5343（它与应用magic前两字节重合）。ChannelData必须来自已登记TURN服务端端点，匹配活跃allocation、已绑定channel及准确长度；未知来源或channel丢弃。Windows端不依赖SO_REUSEADDR把多个接收器绑定同端口，端口复用指同一socket复用；多会话在明确的单一socket owner中分派，初版每会话独立socket对。

控制明文：kind1（Data=1/Ack=2）+ reserved3 + messageSequence8 + ackBase8 + ackMask8 + eventType2 + bodyLength2 + body。ackBase为已连续接收最大messageSequence，初始UINT64_MAX表示尚无连续消息；ackMask bit i代表ackBase+1+i已收到（哨兵时从0）。可靠消息序号与加密packet sequence分开：重传相同messageSequence，使用全新packet sequence重新加密。接收方认证后去重，副作用恰好执行一次；每次收到已认证的重复Data都重新发送当前累计/选择ACK，可在5ms内合并，绝不重复注入按键。ACK-only不被ACK，不进入可靠发送队列。UINT64_MAX哨兵专门分支处理，消息序号禁止回绕；ACK确认未曾发送的序号属于协议错误。接收窗口外Data丢弃并回复当前ACK，不扩容；只丢首次ACK时仍须靠重发Data完成确认。

发送窗口64条、接收重排窗口64条；累计ACK+选择ACK；3个不同后续消息被ACK且缺口仍在时快重传，每条消息每RTT最多一次。RTO=clamp(SRTT+4*RTTVAR,30ms,1000ms)，重传样本不用于RTT（Karn）；最多8次或2秒消息期限，失败则暂停输入、释放按键并重连。ACK是32字节纯头（bodyLength=0），收到Data即回，不做延迟合并；RTT由发送方本地测量"首次发送→ACK到达"，因此ACK不携带时间戳。ACK最多延迟5ms；按键/按钮转换严格有序，鼠标移动可在入队前合并，入队后不得改写序号内容。

事件：Key(scanCode,extended,down)、MouseMove(normalizedX/Y 0..65535,monitorId)、MouseButton(button,down)、Wheel(dx,dy)、Touch(contactId,phase,x,y)、ReleaseAll、RequestKeyFrame、SetQuality、PathSwitch、ControlLease。键盘扫描码与Unicode文本分开；限定键状态集合，断线清零。每个Host最多一个活跃输入租约；多Viewer默认仅观看。

## 4. 视频封装与恢复

视频明文前缀32字节：FrameId u64、CaptureTimestampUs u64（发送端单调时钟）、ConfigId u32、FragmentIndex u16、FragmentCount u16、FrameLength u32、Flags u8（key=1/config=2/discontinuity=4）、Codec u8（H264=1/AV1=2）、Reserved u16。其后为H.264 Annex-B或AV1 low-overhead OBU分片。最长帧8MiB、最多8192片、同时缓存最多4帧/32MiB；超界拒绝。参数集和配置版本先经可靠控制确认，首个新配置帧必须关键帧。

无视频重传。抖动缓冲默认20ms，自适应10–60ms；超期残缺帧丢弃，参考链受损请求新关键帧（限每500ms一次）。关键帧请求是新帧请求，不是补发旧帧。后续可加FEC，但须独立版本协商。零变化帧跳过编码；光标可作为独立可靠状态更新。脏矩形ROI优化保留完整视频帧语义；区域图块协议另设版本，包含基帧和重同步。

## 5. 探测、统计与保活

Probe kind1/reserved7/token8/sendMonotonicUs8/echoMonotonicUs8，请求与响应均加密，token匹配后测RTT，无需两端墙钟同步。1s反馈窗口报告收到的序号区间、延迟分位数与播放丢帧，网络丢包与解码丢帧分别统计。默认每10s保活（可配置5–30s），连续3次缺失标记down；每socket各自维护“收到有效响应”时刻，发送心跳绝不刷新该时刻。channel3探测固定Video component streamId=1、Control component streamId=2，分别派生密钥与维护序号；Direct/Relay并行探测同一component共用唯一packet序号分配器，路径迁移不重置计数。

## 6. 标准STUN/TURN边界

STUN独立20字节头：type u16、length u16、cookie 0x2112A442、transactionId12；属性TLV按4字节对齐。Binding成功含XOR-MAPPED-ADDRESS(0x0020)，IPv4/IPv6正确异或；未知必需属性返回420+UNKNOWN-ATTRIBUTES；长度/保留位/来源/事务严格校验。事务按RFC5389退避重试，不采用忙循环。

TURN是RFC5766标准报文，不使用SCT1代替Allocate。完整实现门槛：长期凭证401/438 challenge、REALM/NONCE、MESSAGE-INTEGRITY HMAC-SHA1与FINGERPRINT；Allocate(UDP)、Refresh、CreatePermission、ChannelBind、Send/Data Indication、ChannelData；XOR-RELAYED-ADDRESS、XOR-PEER-ADDRESS、XOR-MAPPED-ADDRESS、LIFETIME。SHA1/MD5仅用于RFC5766互通规定的长期认证计算，不用作媒体加密。

allocation按客户端/服务端五元组索引，默认600s；permission300s；channel600s；按寿命一半刷新，Refresh lifetime0显式释放；重复事务幂等。channel编号0x4000–0x7FFF（本客户端不分配0x5343）。permission按peer IP匹配，channel按peer IP+port匹配；ChannelBind同时创建/刷新对应permission，数据流量本身不刷新allocation/permission/channel寿命。TURN只转发已授权peer的内层AES-GCM密文；入站permission先于channel/data转发。MVP先支持IPv4 UDP allocation，IPv6能力需要扩展RFC6156，完整RFC5766实现还需客户端-TURN TCP/TLS传输支持并单独验收，不能将UDP子集标记为全部RFC合规。

标准Binding和TURN管理头本身不是AES-GCM负载；“所有UDP加密”在本项目精确定义为所有应用媒体/控制/会话消息加密，公开STUN发现及认证握手有明文字段。禁止为了字面全包加密破坏RFC互通。

参考标准：[RFC5389](https://www.rfc-editor.org/rfc/rfc5389)、[RFC5766](https://www.rfc-editor.org/rfc/rfc5766)、[RFC5780](https://www.rfc-editor.org/rfc/rfc5780.html)、[RFC8866](https://www.rfc-editor.org/rfc/rfc8866)。

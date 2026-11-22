# 握手细化草案 v0.1

状态：设计契约，尚未实现/互通验证；在P2实现前必须增加已知答案向量及独立密码协议审查。现有PacketProtector只实现会话数据保护，不是完整信令认证协议。

## 线编码

所有长度/整数big endian。SCSH头24字节：magic4=`SCSH`、version1=1、type1、bodyLength2、transactionId16。重传同事务握手发送完全相同字节，服务端缓存相同响应；一个事务每步最多重试5次，RTO500ms指数退避、单事务最长15s。body不使用JSON；签名和哈希按下列字节顺序，禁止运行时对象序列化。

P-256公钥为SEC1 uncompressed65字节（04||X32||Y32），拒绝非法曲线点。ECDSA-P256-SHA256签名为IEEE-P1363 r32||s32，固定64字节；使用低S规范化，验证时拒绝高S。ECDH输出为固定32字节共享X坐标。身份deviceId为SHA256(identityPublicKey65)前16字节，需登记/Host允许；身份密钥与临时ECDH密钥分开。

type1 ClientHello：clientNonce32||clientEphemeral65||clientIdentity65||clientRole1(0=Host,1=Viewer)||reserved1(0)||padding64(全0)。padding使无状态Cookie响应小于请求。

type2 Cookie：clientHelloHash32||serverNonce32||expiresUnixSeconds8||cookie32。cookie=HMAC-SHA256(serverCookieSecret, ASCII `SC-cookie-v1` || addressFamily1 || sourceIP(4或16) || sourcePort2 || transactionId16 || clientHelloHash32 || serverNonce32 || expires8)。有效期30s，前后时间桶密钥限期验证；服务器可无状态返回。ClientHelloHash=SHA256完整ClientHello报文。Cookie只提供返回地址验证，不能被客户端视为服务器身份证明。

type3 ClientAuth：helloLength2||完整ClientHello||cookieLength2||完整Cookie||clientSignature64。签名输入为ASCII `SC-client-auth-v1` || SHA256(ClientHello||Cookie)。服务端验证回显事务匹配、哈希、源地址cookie、有效期及登记身份后分配状态。

type4 ServerAuth：serverIdentity65||serverEphemeral65||serverSignature64。签名输入ASCII `SC-server-auth-v1` || SHA256(ClientHello||Cookie||ClientAuth||serverIdentity65||serverEphemeral65)。客户端固定验证服务端钉住的身份公钥；拒绝不匹配，不自动信任替换key。

T=SHA256(ClientHello||Cookie||ClientAuth||ServerAuth)（各自完整头+body，按上列顺序）。signalConnectionId=SHA256(ASCII `SC-signal-id-v1`||T)前16字节，在创建媒体会话前用于channel0头SessionId。

shared=ECDH(clientEphemeral,serverEphemeral)。signalMaster=HKDF-SHA256(IKM=shared,salt=T,info=ASCII `SC-signal-master-v1`,L=32)。Finished键分别以`SC-finished-client-v1`、`SC-finished-server-v1`作为info从signalMaster派生（salt=T）。

type5 ClientFinished：HMAC-SHA256(clientFinishedKey,T)32；type6 ServerFinished：HMAC-SHA256(serverFinishedKey,T||ClientFinished完整报文)32。双方Finished成功后才发送加密channel0；type5/6不承载应用内容。初始epoch1；重启全新随机nonce/临时密钥/连接ID。

信令数据保护域方向枚举2=ClientToServer、3=ServerToClient，与媒体0=HostToViewer、1=ViewerToHost分离。实现这两个值时需扩展当前Core方向枚举及测试；在此之前不得拿HostToViewer枚举替代信令方向。channel0 stream0。

## 会话端到端协商

加密信令转发PeerKeyOffer/Answer二进制body，可JSON base64包装，但签名覆盖解码后的规范字节：sessionId16||generation4||role1||deviceId16||peerDeviceId16||nonce32||ephemeral65||offerHash32。Offer的offerHash全0；Answer的offerHash=SHA256完整Offer规范body+signature64)。每端签名输入ASCII `SC-peer-key-v1`||body，身份与授权邀请中的deviceId必须一致。

peerTranscript=SHA256(OfferBody||OfferSig||AnswerBody||AnswerSig)。peerMaster=HKDF-SHA256(ECDH结果,salt=peerTranscript,info=ASCII `SC-peer-master-v1`,L=32)。KeyConfirm分别HMAC-SHA256(HKDF(peerMaster,peerTranscript, `SC-peer-confirm-host-v1`或`SC-peer-confirm-viewer-v1`,32),peerTranscript)。经过已认证信令转发；双方确认后允许打洞探测和媒体。每次重连fresh ephemeral与新generation，控制输入等待有效Host授权租约。

## 应用数据HKDF精确编码

当前Core媒体实现：salt=SessionId16；info=ASCII `ScreenCast/AES-256-GCM` || version1(01) || SessionId16 || epoch4 || direction1 || channel1 || streamId4；L=32。IKM传peerMaster32。nonce=epoch4||packetSequence8。未来信令使用同结构但不同IKM=signalMaster、不同SessionId=signalConnectionId及方向2/3。此域分离必须有C#/Rust已知答案测试。

应用包向量由Core测试输出，握手向量需P2实现时固定测试身份/临时私钥并覆盖：错误身份、公钥换绑、角色互换、旧cookie、新源端口、修改capability转录、重复Finished、乱序消息。当前不声称已有完整握手向量或经过审计。

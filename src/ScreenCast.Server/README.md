# ScreenCast.Server：UDP Binding基础

这是net8.0控制台异步UDP STUN Binding服务，仅提供公网映射发现基础，不是TURN服务、信令服务器或可用的远程投屏服务。

```powershell
dotnet build src/ScreenCast.Server/ScreenCast.Server.csproj
dotnet run --project src/ScreenCast.Server/ScreenCast.Server.csproj -p:RollForward=Major -- --bind 127.0.0.1 --port 3478
# IPv6单独实例
dotnet run --project src/ScreenCast.Server/ScreenCast.Server.csproj -p:RollForward=Major -- --bind ::1 --port 3478
```

默认仅监听IPv4 loopback。明确传入 `--bind 0.0.0.0` 或 `--bind ::` 可监听其他接口；IPv6 socket使用IPv6-only。`--port 0`由系统分配端口，启动日志打印实际endpoint。Ctrl+C取消接收并释放socket。

行为：单一异步接收循环解析最长1200字节请求、返回Binding成功的IPv4/IPv6 XOR-MAPPED-ADDRESS、未知必需属性返回420/UNKNOWN-ATTRIBUTES；丢弃格式错误、超长请求和其他STUN方法（包含TURN Allocate）。没有忙循环、每客户端任务或无界请求队列。全局固定一秒窗口限流为每秒最多200个请求（超额丢弃），每IP最多20个；IP计数表每窗口清空，最多200项，内存有界。限制解析和回包工作，窗口边界可产生短时双倍突发，超额来源仍消耗全局接收预算，未保证各IP公平调度。

未实现：长期认证、MESSAGE-INTEGRITY校验、TURN中继、SCSH/ECDH握手、设备注册、会话授权、信令转发、账户配额/公网部署防护。可选属性忽略，未支持的必需属性返回420。仅适合验证Binding codec与传输，不宣称完整RFC5389服务或完整RFC5766 TURN互通。

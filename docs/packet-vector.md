# AES-GCM媒体数据已知答案向量

Core测试实际生成并验证，供Rust加密实现后互通使用。当前Rust仅有头部解析fixture；此文档不代表Rust已实现AES。

- sharedSecret/IKM：`000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f`
- SessionId：`000102030405060708090a0b0c0d0e0f`
- epoch=1、direction=0(HostToViewer)、channel=1(Video)、streamId=0、sequence=0
- plaintext：`010203`
- HKDF-SHA256 salt=SessionId16
- info=ASCII `ScreenCast/AES-256-GCM` || `01` || SessionId16 || epochBE4 || direction1 || channel1 || streamBE4
- nonce=`000000010000000000000000`
- AAD=完整包前48字节
- Tag=`0a92411a02b67657d95a187a5a25a623`
- ciphertext=`c8226b`

完整包（hex）：

```text
5343543101010000000102030405060708090A0B0C0D0E0F0000000100000000000000000000000000030040000000000A92411A02B67657D95A187A5A25A623C8226B
```

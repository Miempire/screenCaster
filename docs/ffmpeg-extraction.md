# 从 FFmpeg 源码提取的解码/呈现机制

本文记录**实际读过**的 FFmpeg 源码位置，以及我们据此做的设计取舍。原则：只提取机制与不变量，不复制代码。所有行号对应本仓库 `preference/FFmpeg-master` 快照。

## 1. 解码输出纹理的正确创建方式

**来源**：`libavutil/hwcontext_d3d11va.c:221-247`（`d3d11va_alloc_single`）、`:716-726`

提取到的不变量：

- 解码器输出纹理必须 `D3D11_USAGE_DEFAULT`、`MipLevels = 1`、`SampleDesc.Count = 1`。
- `ArraySize` 就是解码表面池大小；FFmpeg 用纹理数组 + 数组切片（`ArraySlice = i`）表示一帧，而不是每帧一张纹理。
- `BindFlags` 不是固定值，而是按用途按需打开：`D3D11_BIND_SHADER_RESOURCE`（采样）、`D3D11_BIND_UNORDERED_ACCESS`、`D3D11_BIND_RENDER_TARGET`、`MiscFlags |= D3D11_RESOURCE_MISC_SHARED`（跨设备共享）。
- 解码器本身需要 `D3D11_BIND_DECODER`，这条在 `libavcodec/dxva2.c` 的 frames 约束里体现。

**我们的取舍**：先用 Media Foundation 解码 MFT（它内部会自己管解码表面），所以不自己建解码纹理数组；但上面这套"按用途开 BindFlags、共享要显式开 SHARED"的规则，在后续接 `ID3D11VideoDecoder` 直通时照用。

## 2. GPU 纹理回读 CPU 的标准三步

**来源**：`libavutil/hwcontext_d3d11va.c:388-411`（建 staging）、`:464-479`（下载）

提取到的机制：

1. 建一张 `D3D11_USAGE_STAGING` 纹理，`CPUAccessFlags = D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE`，其余描述与源纹理一致（宽高、格式、MipLevels 1、ArraySize 1）。
2. `CopySubresourceRegion(staging, 0, 0,0,0,0, texture, index, NULL)` —— 注意第 6 个参数是**数组切片索引**，不是 mip。
3. `Map(staging, 0, D3D11_MAP_READ, 0, &map)`，然后用 `map->RowPitch`（不是 width×bpp）作为行跨度，`av_image_fill_pointers` 算平面指针，拷完 `Unmap`。

**关键坑（FFmpeg 明确规避的）**：`hwcontext_d3d11va.c:380-381` 注释写着 opaque 格式（`DXGI_FORMAT_420_OPAQUE`）**不支持 transfer**，实际访问会失败。所以拿到 `420_OPAQUE` 就必须走硬件路径或换格式，不能直接回读。

**我们的取舍**：解码 MFT 输出 NV12 到系统内存时不需要自己建 staging；但呈现层若走 D3D11 纹理，回读必须按上述三步，且 `RowPitch` 必须按实际值算——这正是很多实现花屏的原因。

## 3. 解码器配置与逐帧提交

**来源**：`libavcodec/dxva2.c:540-580`（建解码器与输出视图）、`:998-1016`（BeginFrame 重试）、`:1099`、`:1121`（Submit/EndFrame）

提取到的机制：

- 解码器描述里 `SampleWidth/SampleHeight` 用的是 **coded_width/coded_height**（编码尺寸），不是显示尺寸——两者在有裁剪/非 16 对齐时不同。
- 每个数组切片要单独建一个 `ID3D11VideoDecoderOutputView`，`ViewDimension = D3D11_VDOV_DIMENSION_TEXTURE2D`，`ArraySlice = i`。
- `DecoderBeginFrame` 可能返回 `E_PENDING`：FFmpeg 的循环是**最多 50 次、每次睡 2000 µs**，然后才判失败。不重试就会在硬件忙时随机丢帧。

**我们的取舍**：MFT 路径里对应的就是 `MF_E_TRANSFORM_NEED_MORE_INPUT` / `MF_E_TRANSFORM_STREAM_CHANGE` 的重试与重建语义；我们把 `E_PENDING` 式的等待也做成有界重试，不用无界忙等。

## 4. NV12 → RGB 的精确系数

**来源**：`libswscale/yuv2rgb.c:36-59`

FFmpeg 的系数表是 `{crv, cbu, cgu, cgv}` 四个 16.16 定点数，推导公式在注释里：

```
crv = (255 / 224) * 65536 * (1 - cr) / 0.5
cbu = (255 / 224) * 65536 * (1 - cb) / 0.5
cgu = (255 / 224) * 65536 * (cb / cg) * (1 - cb) / 0.5
cgv = (255 / 224) * 65536 * (cr / cg) * (1 - cr) / 0.5
```

实际数值（表第 1、6 行是 BT.601 / SMPTE 170M，第 2 行是 BT.709）：

| 色彩空间 | crv | cbu | cgu | cgv |
|---|---:|---:|---:|---:|
| BT.601 (SMPTE 170M) | 104597 | 132201 | 25675 | 53279 |
| BT.709 | 117489 | 138438 | 13975 | 34925 |

`(255/224)` 就是 limited range 展开；`yuv2rgb.c:860` 还有 `cy = (cy * 255) / 219` 的亮度缩放。

**我们的取舍**：`Nv12ToBgra` 直接采用这四个系数与 `255/219` 亮度缩放，实现整数版公式，BT.601/BT.709 可选。这与 FFmpeg 输出在数值上一致，不需要引入 swscale。

## 5. 编码侧（已用于本项目）的对应关系

**来源**：`libavfilter/vsrc_ddagrab.c:687-765`、`libavcodec/nvenc.c:2270-2289`、`libavcodec/amfenc.c:456-480`、`libavutil/hwcontext_qsv.c:318`

- DDA 拿到的是 `ID3D11Texture2D`，FFmpeg 直接把它当 GPU 帧传给硬件编码器（NVENC 用 `RegisterDirectXResource`，AMF 用 `CreateSurfaceFromDX11Native`）。
- 我们当前走的是 CPU 回读 + NV12 转换，因为 `MFCreateDXGISurfaceBuffer` 在本机四种纹理配置下都返回 `E_INVALIDARG`（见 `artifacts/media/media-smoke-report.json` 的 Probe 段）。这是**已知的性能缺口**，不是设计选择。

## 6. 明确没有照搬的部分

- **没有**重写 `dxva2_h264.c` 的 slice header / picture parameter 解析。那是把 H.264 码流拆成 DXVA 参数结构，约 700 行高度依赖规范的位域解析，属于"自己写一个解码器前端"，风险远大于收益。
- **没有**引入 FFmpeg 运行时（LGPL/GPL 组合与体积代价）。解码用 Windows 自带 MFT，符合"不复制源码、只提取机制"的要求。
- **没有**做 D3D11 交换链直出。当前呈现走 `WriteableBitmap`，先保证画面正确；零拷贝呈现是后续优化项。

## 7. 实测推翻的三个假设（比提取本身更有价值）

从 FFmpeg 读到的机制不足以直接照搬，以下三条是我们自己实测纠正的，记录在此避免重犯：

| 假设 | 实测结果 |
|---|---|
| MF 解码器要 MP4 风格的长度前缀样本 | **错**。六组合矩阵实测：**Annex-B 起始码解码，长度前缀 0 帧**。avcC/AnnexB/无序列头三种都能配，决定成败的是样本帧格式 |
| 解码器自己会从 SPS 得到输出几何 | **错**。输入类型必须显式带 `MF_MT_FRAME_SIZE`，否则解码器只提供 1920x1080 占位类型 |
| 按 FFmpeg 系数做的转换可以直接用 | **错**。`(Y-16)*255*65536` 在 Int32 溢出成负数，整幅画面被钳成黑色。必须用 64 位中间量 |

## 8. 一句话总结

FFmpeg 教给我们的是**不变量**：解码纹理的用法与绑定标志、回读必须用 `RowPitch`、opaque 格式不可回读、coded size 而非显示尺寸、`E_PENDING` 要有界重试、NV12→RGB 的定点系数。这些已经落到本项目的解码与呈现代码里；FFmpeg 的**代码本身**没有进入本仓库。

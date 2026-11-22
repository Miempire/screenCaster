//
//  HostPanel.swift
//  The controlled side's page.
//

import AppKit
import SwiftUI

struct HostPage: View {
    @ObservedObject var host: HostSession

    @State private var showNetwork = true
    @State private var showStats = true

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                HStack(alignment: .top) {
                    SectionHeader(title: "被控 · 这台 Mac",
                                  subtitle: "允许其他设备连接并控制这台电脑")
                    Spacer()
                    Badge(text: host.running ? "共享中" : "未共享",
                          tint: host.running ? Theme.accent : Theme.dim,
                          fill: host.running ? Theme.accentSoft : Theme.surfaceAlt)
                }

                HStack(alignment: .top, spacing: 18) {
                    VStack(spacing: 18) {
                        statusCard
                        connectionCard
                        displayCard
                        permissionsCard
                    }

                    VStack(spacing: 18) {
                        statsCard
                        tipsCard
                    }
                    .frame(width: 320)
                }
            }
            .padding(26)
        }
        .task {
            await host.loadDisplays()
        }
    }

    // MARK: - Cards

    private var statusCard: some View {
        SurfaceCard {
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 12) {
                    IconTile(systemName: "display",
                             tint: host.running ? Theme.accent : Theme.dim,
                             size: 44)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(host.running ? "正在共享这台 Mac" : "开始共享这台 Mac")
                            .font(.system(size: 16, weight: .semibold))
                            .foregroundStyle(Theme.text)
                        Text(host.statusText)
                            .font(.system(size: 11))
                            .foregroundStyle(Theme.dim)
                            .lineLimit(2)
                    }
                    Spacer()
                    Circle()
                        .fill(host.running ? Theme.accent : Theme.faint)
                        .frame(width: 7, height: 7)
                }

                HStack(spacing: 10) {
                    portTile(title: "本机视频端口", value: host.boundPorts.map { "\($0.video)" }
                             ?? host.localVideoPort)
                    portTile(title: "本机控制端口", value: host.boundPorts.map { "\($0.control)" }
                             ?? host.localControlPort)
                    portTile(title: "对端", value: host.running
                             ? "\(host.peerAddress):\(host.peerVideoPort)/\(host.peerControlPort)"
                             : "连接前填写")
                }

                Button {
                    if host.running {
                        Task { await host.stop() }
                    } else {
                        Task { await host.start() }
                    }
                } label: {
                    HStack(spacing: 8) {
                        Image(systemName: host.running ? "stop.fill" : "play.fill")
                            .font(.system(size: 11, weight: .semibold))
                        Text(host.running ? "停止共享" : "开始共享")
                    }
                }
                .buttonStyle(AccentButtonStyle(fill: host.running ? Theme.surfaceHigh : Theme.accent,
                                              foreground: host.running ? Theme.text : Theme.accentInk))

                if !host.lastError.isEmpty {
                    NoticeBanner(text: host.lastError)
                }
            }
        }
    }

    private var connectionCard: some View {
        SurfaceCard {
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text("连接信息")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Theme.text)
                    Spacer()
                    Text("C ABI 不会自动学习对端地址")
                        .font(.system(size: 10))
                        .foregroundStyle(Theme.faint)
                }

                LabeledInput(title: "控制端 IP 地址", text: $host.peerAddress)
                HStack(alignment: .bottom, spacing: 10) {
                    LabeledInput(title: "控制端视频端口", text: $host.peerVideoPort, width: 150)
                    LabeledInput(title: "控制端控制端口", text: $host.peerControlPort, width: 150)
                    Spacer()
                }

                DisclosureGroup(isExpanded: $showNetwork) {
                    HStack(alignment: .bottom, spacing: 10) {
                        LabeledInput(title: "本机视频端口", text: $host.localVideoPort, width: 140)
                        LabeledInput(title: "本机控制端口", text: $host.localControlPort, width: 140)
                        Spacer()
                    }
                    .padding(.top, 6)
                } label: {
                    Text("本机监听端口（0 为自动分配）")
                        .font(.system(size: 11))
                        .foregroundStyle(Theme.dim)
                }
                .tint(Theme.dim)

                Text("两端必须使用同一份共享密钥与会话 ID；当前版本不含信令，端口需要手动对齐。")
                    .font(.system(size: 10))
                    .foregroundStyle(Theme.faint)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var displayCard: some View {
        SurfaceCard {
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text("画面与编码")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Theme.text)
                    Spacer()
                    Button {
                        Task { await host.loadDisplays() }
                    } label: {
                        Label("刷新显示器", systemImage: "arrow.clockwise")
                    }
                    .buttonStyle(QuietButtonStyle(tint: Theme.dim, height: 26))
                }

                if host.displays.isEmpty {
                    Text(host.screenRecordingTrusted
                         ? "没有检测到可采集的显示器。"
                         : "授予“屏幕录制”权限后才能列出显示器。")
                        .font(.system(size: 11))
                        .foregroundStyle(Theme.dim)
                } else {
                    Picker("显示器", selection: $host.selectedDisplayID) {
                        ForEach(host.displays) { display in
                            Text(display.name).tag(display.id)
                        }
                    }
                    .pickerStyle(.menu)
                    .labelsHidden()
                    .tint(Theme.text)
                }

                HStack(alignment: .bottom, spacing: 10) {
                    LabeledInput(title: "最大宽度", text: $host.maxWidthText, width: 100)
                    LabeledInput(title: "最大高度", text: $host.maxHeightText, width: 100)
                    LabeledInput(title: "帧率", text: $host.fpsText, width: 76)
                    LabeledInput(title: "码率 (Mbps)", text: $host.bitrateText, width: 94)
                    Spacer()
                }

                if let display = host.selectedDisplay {
                    Text("当前显示器：\(display.name) · \(Int(display.width)) × \(Int(display.height))")
                        .font(.system(size: 10))
                        .foregroundStyle(Theme.faint)
                }
            }
        }
    }

    private var permissionsCard: some View {
        SurfaceCard {
            VStack(alignment: .leading, spacing: 10) {
                Text("系统权限")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.text)

                PermissionRow(title: "屏幕录制",
                              granted: host.screenRecordingTrusted,
                              grantedDetail: "已允许采集屏幕画面",
                              missingDetail: "需要授权后才能共享画面",
                              action: { openPrivacy("Privacy_ScreenCapture") })
                PermissionRow(title: "辅助功能",
                              granted: host.accessibilityTrusted,
                              grantedDetail: "已允许注入远程键鼠",
                              missingDetail: "需要授权后才能控制这台 Mac",
                              action: { openPrivacy("Privacy_Accessibility") })

                HStack {
                    Spacer()
                    Button {
                        host.refreshPermissions()
                        Task { await host.loadDisplays() }
                    } label: {
                        Label("重新检查", systemImage: "checkmark.shield")
                    }
                    .buttonStyle(QuietButtonStyle(tint: Theme.dim, height: 26))
                }
            }
        }
    }

    private var statsCard: some View {
        SurfaceCard {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("实时统计")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Theme.text)
                    Spacer()
                    Button(showStats ? "收起" : "展开") {
                        showStats.toggle()
                    }
                    .buttonStyle(QuietButtonStyle(tint: Theme.dim, height: 24))
                }

                if showStats {
                    StatsGrid(rows: [
                        ("分辨率", "\(host.stats.width) × \(host.stats.height)"),
                        ("编码帧率", String(format: "%.1f fps", host.stats.fps)),
                        ("平均编码", String(format: "%.1f ms", host.stats.encodeMilliseconds)),
                        ("视频延迟", rtt(host.stats.videoRTTMilliseconds)),
                        ("控制延迟", rtt(host.stats.controlRTTMilliseconds)),
                        ("已发送", bytes(host.stats.sentBytes)),
                        ("关键帧", "\(host.stats.keyFrames)"),
                        ("按住按键", "\(host.stats.pressedKeys)"),
                        ("输入租约", host.stats.leaseOwner.isEmpty ? "空闲" : host.stats.leaseOwner),
                        ("视频通路", host.stats.videoPath),
                        ("控制通路", host.stats.controlPath)
                    ])
                }
            }
        }
    }

    private var tipsCard: some View {
        SurfaceCard {
            VStack(alignment: .leading, spacing: 9) {
                Text("使用提示")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.text)
                tip("1", "先授予“屏幕录制”和“辅助功能”，再刷新显示器。")
                tip("2", "填写控制端的 IP 与端口；对端端口要与控制端监听端口一致。")
                tip("3", "控制端接管后会出现输入租约；断开时会自动释放所有按键。")
            }
        }
    }

    // MARK: - Pieces

    private func portTile(title: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title)
                .font(.system(size: 9))
                .foregroundStyle(Theme.faint)
            Text(value)
                .font(.system(size: 11, weight: .medium, design: .monospaced))
                .foregroundStyle(Theme.text)
                .lineLimit(1)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.surfaceAlt)
        .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
    }

    private func tip(_ index: String, _ text: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(index)
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(Theme.accent)
                .frame(width: 16, height: 16)
                .background(Circle().fill(Theme.accentSoft))
            Text(text)
                .font(.system(size: 11))
                .foregroundStyle(Theme.dim)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
    }

    private func rtt(_ value: Double) -> String {
        value >= 0 ? String(format: "%.1f ms", value) : "—"
    }

    private func bytes(_ count: Int) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(count), countStyle: .file)
    }

    private func openPrivacy(_ anchor: String) {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)")
        else { return }
        NSWorkspace.shared.open(url)
    }
}

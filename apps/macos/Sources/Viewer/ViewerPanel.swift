//
//  ViewerPanel.swift
//  The controlling side's pages: saved devices, remote assistance, and the live session overlay.
//

import AppKit
import SwiftUI

struct DevicesPage: View {
    @ObservedObject var book: DeviceBook
    @ObservedObject var viewer: ViewerSession
    let onConnect: (SavedDevice) -> Void
    let onConnectByCode: (String) -> Void
    let onDirectConnect: (String, Int, Int) -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .top) {
                    SectionHeader(title: "设备",
                                  subtitle: "输入 9 位连接码，或从已保存设备中直接连接")
                    Spacer()
                    Badge(text: "控制端", tint: Theme.dim, fill: Theme.surfaceAlt)
                }

                RemoteAssistCard(viewer: viewer,
                                 onConnectByCode: onConnectByCode,
                                 onDirectConnect: onDirectConnect)

                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("我的设备")
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(Theme.text)
                    Badge(text: "\(book.devices.count) 台", tint: Theme.dim,
                          fill: Theme.surfaceAlt)
                    Spacer()
                    if !book.devices.isEmpty {
                        Text("本机快捷码，不经过服务器")
                            .font(.system(size: 10))
                            .foregroundStyle(Theme.faint)
                    }
                }

                if book.devices.isEmpty {
                    EmptyDevicesCard()
                } else {
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 12),
                                        GridItem(.flexible(), spacing: 12)],
                              spacing: 12) {
                        ForEach(book.devices) { device in
                            SavedDeviceCard(device: device,
                                            onConnect: { onConnect(device) },
                                            onRemove: { book.remove(code: device.code) })
                        }
                    }
                }

                if !viewer.lastError.isEmpty {
                    NoticeBanner(text: viewer.lastError)
                }
            }
            .padding(26)
        }
    }
}

struct AssistPage: View {
    @ObservedObject var book: DeviceBook
    @ObservedObject var viewer: ViewerSession
    let onConnect: (SavedDevice) -> Void
    let onConnectByCode: (String) -> Void
    let onDirectConnect: (String, Int, Int) -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                SectionHeader(title: "远程协助",
                              subtitle: "用 9 位连接码连接对方设备，或通过 IP 地址直连")

                HStack(alignment: .top, spacing: 18) {
                    RemoteAssistCard(viewer: viewer,
                                     onConnectByCode: onConnectByCode,
                                     onDirectConnect: onDirectConnect)
                        .frame(maxWidth: 640)

                    VStack(spacing: 14) {
                        SurfaceCard {
                            VStack(alignment: .leading, spacing: 12) {
                                Text("连接步骤")
                                    .font(.system(size: 13, weight: .semibold))
                                    .foregroundStyle(Theme.text)
                                StepRow(index: 1, text: "在被控电脑上开始共享，记下服务端签发的 9 位连接码。")
                                StepRow(index: 2, text: "在这台 Mac 输入连接码，双方通过 jsspace.xyz 交换媒体地址。")
                                StepRow(index: 3, text: "连接成功后画面直接显示；IP 直连保留在高级模式。")
                            }
                        }

                        SurfaceCard {
                            VStack(alignment: .leading, spacing: 8) {
                                Text("当前限制")
                                    .font(.system(size: 13, weight: .semibold))
                                    .foregroundStyle(Theme.text)
                                Text("通过 jsspace.xyz 配对，两端仍使用 UDP 点对点传输，负载 AES-256-GCM 加密；没有 TURN 时，严格 NAT 后可能无法连通。")
                                    .font(.system(size: 11))
                                    .foregroundStyle(Theme.dim)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                    .frame(width: 300)
                }

                if !viewer.lastError.isEmpty {
                    NoticeBanner(text: viewer.lastError)
                }
            }
            .padding(26)
        }
    }
}

// MARK: - Remote assistance card

struct RemoteAssistCard: View {
    @ObservedObject var viewer: ViewerSession
    let onConnectByCode: (String) -> Void
    let onDirectConnect: (String, Int, Int) -> Void

    @State private var code = DeviceIdentity.lastCode
    @State private var showingDirect = false
    @State private var host = "127.0.0.1"
    @State private var videoPort = String(CoreDefaults.hostVideoPort)
    @State private var controlPort = String(CoreDefaults.hostControlPort)
    @State private var hint: String?

    var body: some View {
        SurfaceCard {
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 12) {
                    IconTile(systemName: "dot.radiowaves.left.and.right", size: 40)
                    VStack(alignment: .leading, spacing: 3) {
                        Text("远程协助")
                            .font(.system(size: 17, weight: .semibold))
                            .foregroundStyle(Theme.text)
                        Text("输入对方设备上的 9 位连接码")
                            .font(.system(size: 11))
                            .foregroundStyle(Theme.dim)
                    }
                    Spacer()
                    Badge(text: "服务器 jsspace.xyz", tint: Theme.accent, fill: Theme.accentSoft)
                }

                CodeEntryView(code: $code)

                Button(action: submit) {
                    HStack(spacing: 8) {
                        Text("连接")
                        Image(systemName: "arrow.right")
                            .font(.system(size: 12, weight: .semibold))
                    }
                }
                .buttonStyle(AccentButtonStyle())
                .keyboardShortcut(.defaultAction)

                HStack(spacing: 6) {
                    Button(showingDirect ? "收起 IP 直连" : "高级：使用 IP 地址直连") {
                        showingDirect.toggle()
                        hint = nil
                    }
                    .buttonStyle(.plain)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(Theme.dim)

                    Image(systemName: showingDirect ? "chevron.up" : "chevron.down")
                        .font(.system(size: 8, weight: .bold))
                        .foregroundStyle(Theme.faint)

                    Spacer()

                    Text("服务器 jsspace.xyz · UDP 3479")
                        .font(.system(size: 10))
                        .foregroundStyle(Theme.faint)
                }

                if showingDirect {
                    VStack(alignment: .leading, spacing: 9) {
                        LabeledInput(title: "对方 IP 地址", text: $host)
                        HStack(alignment: .bottom, spacing: 9) {
                            LabeledInput(title: "视频端口", text: $videoPort, width: 130)
                            LabeledInput(title: "控制端口", text: $controlPort, width: 130)
                            Spacer()
                        }
                        Text("直连不经过服务器，需要两端手动对齐密钥与端口。")
                            .font(.system(size: 10))
                            .foregroundStyle(Theme.faint)
                    }
                    .padding(.top, 2)
                }

                if let hint {
                    HStack(spacing: 6) {
                        Image(systemName: "info.circle.fill")
                            .font(.system(size: 10))
                        Text(hint)
                            .font(.system(size: 11))
                    }
                    .foregroundStyle(Theme.warning)
                }
            }
        }
    }

    private func submit() {
        hint = nil

        let digits = code.filter { $0.isNumber }
        if digits.count == 9, digits.first != "0" {
            onConnectByCode(digits)
            return
        }
        if showingDirect, !host.trimmingCharacters(in: .whitespaces).isEmpty {
            guard let video = Int(videoPort.trimmingCharacters(in: .whitespaces)),
                  let control = Int(controlPort.trimmingCharacters(in: .whitespaces)),
                  (1 ... 65535).contains(video),
                  (1 ... 65535).contains(control),
                  video != control else {
                hint = "端口必须是 1–65535，且不能相同。"
                return
            }
            onDirectConnect(host.trimmingCharacters(in: .whitespaces), video, control)
            return
        }
        hint = "请输入 9 位连接码，或在高级模式中填写 IP 地址。"
    }
}

// MARK: - Device cards

struct SavedDeviceCard: View {
    let device: SavedDevice
    let onConnect: () -> Void
    let onRemove: () -> Void

    var body: some View {
        SurfaceCard(padding: 14) {
            HStack(spacing: 12) {
                IconTile(systemName: "display", size: 42)
                VStack(alignment: .leading, spacing: 4) {
                    Text(device.name)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Theme.text)
                        .lineLimit(1)
                    Text(device.addressText)
                        .font(.system(size: 10, design: .monospaced))
                        .foregroundStyle(Theme.dim)
                        .lineLimit(1)
                    HStack(spacing: 6) {
                        Text("快捷码 \(device.spacedCode)")
                            .font(.system(size: 10, design: .monospaced))
                            .foregroundStyle(Theme.accent)
                        Text("· \(relativeText)")
                            .font(.system(size: 10))
                            .foregroundStyle(Theme.faint)
                    }
                }
                Spacer(minLength: 4)
                VStack(alignment: .trailing, spacing: 8) {
                    Button("连接", action: onConnect)
                        .buttonStyle(.plain)
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(Theme.accent)
                        .padding(.horizontal, 11)
                        .padding(.vertical, 5)
                        .background(Capsule().fill(Theme.accentSoft))
                    Button(action: onRemove) {
                        Image(systemName: "xmark")
                            .font(.system(size: 8, weight: .bold))
                            .foregroundStyle(Theme.faint)
                    }
                    .buttonStyle(.plain)
                    .help("删除设备")
                }
            }
        }
    }

    private var relativeText: String {
        let formatter = RelativeDateTimeFormatter()
        formatter.locale = Locale(identifier: "zh_Hans_CN")
        formatter.unitsStyle = .short
        return formatter.localizedString(for: device.lastUsed, relativeTo: Date())
    }
}

struct EmptyDevicesCard: View {
    var body: some View {
        VStack(spacing: 8) {
            Image(systemName: "rectangle.stack.badge.plus")
                .font(.system(size: 22, weight: .light))
                .foregroundStyle(Theme.faint)
            Text("还没有设备")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Theme.text)
            Text("用 IP 地址连接成功后，设备会保存在这里。")
                .font(.system(size: 11))
                .foregroundStyle(Theme.dim)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 30)
        .background(Theme.surface.opacity(0.5))
        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .strokeBorder(Theme.divider,
                              style: StrokeStyle(lineWidth: 1, dash: [5, 4])))
    }
}

struct StepRow: View {
    let index: Int
    let text: String

    var body: some View {
        HStack(alignment: .top, spacing: 9) {
            Text("\(index)")
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(Theme.accent)
                .frame(width: 18, height: 18)
                .background(Circle().fill(Theme.accentSoft))
            Text(text)
                .font(.system(size: 11))
                .foregroundStyle(Theme.dim)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
    }
}

struct NoticeBanner: View {
    let text: String

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 11))
                .foregroundStyle(Theme.warning)
            Text(text)
                .font(.system(size: 11))
                .foregroundStyle(Theme.text)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(12)
        .background(Theme.warning.opacity(0.1))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .stroke(Theme.warning.opacity(0.28), lineWidth: 1))
    }
}

// MARK: - Live session

struct SessionOverlay: View {
    @ObservedObject var viewer: ViewerSession
    let deviceName: String

    @State private var chromeVisible = true
    @State private var showStats = true

    var body: some View {
        ZStack {
            Color.black

            SurfaceRepresentable(surface: viewer.surface)

            VStack(spacing: 0) {
                if chromeVisible {
                    topBar
                        .transition(.move(edge: .top).combined(with: .opacity))
                }
                Spacer(minLength: 0)
                if chromeVisible {
                    bottomBar
                        .transition(.move(edge: .bottom).combined(with: .opacity))
                }
            }
            .padding(16)

            if showStats {
                VStack {
                    HStack {
                        Spacer()
                        statsPanel
                    }
                    Spacer()
                }
                .padding(.top, 78)
                .padding(.trailing, 18)
            }

            if !chromeVisible {
                VStack {
                    Button("显示控制条") {
                        withAnimation(.easeOut(duration: 0.18)) { chromeVisible = true }
                    }
                    .buttonStyle(QuietButtonStyle(tint: Theme.dim, height: 26))
                    .padding(.top, 10)
                    Spacer()
                }
            }

            if !viewer.lastError.isEmpty {
                VStack {
                    Spacer()
                    Text(viewer.lastError)
                        .font(.system(size: 11))
                        .foregroundStyle(Theme.danger)
                        .padding(.horizontal, 14)
                        .padding(.vertical, 9)
                        .background(Theme.sidebar.opacity(0.94))
                        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                        .padding(.bottom, 92)
                }
            }
        }
        .ignoresSafeArea()
    }

    private var topBar: some View {
        HStack(spacing: 10) {
            Button {
                viewer.disconnect()
            } label: {
                Image(systemName: "chevron.left")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(Theme.text)
                    .frame(width: 30, height: 30)
                    .background(RoundedRectangle(cornerRadius: 8).fill(Theme.surfaceHigh))
            }
            .buttonStyle(.plain)
            .help("断开连接")

            VStack(alignment: .leading, spacing: 2) {
                Text(deviceName)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(Theme.text)
                    .lineLimit(1)
                Text(viewer.statusText)
                    .font(.system(size: 9))
                    .foregroundStyle(Theme.dim)
                    .lineLimit(1)
            }

            Spacer()

            HStack(spacing: 5) {
                Circle()
                    .fill(viewer.stats.videoRTTMilliseconds >= 0 ? Theme.accent : Theme.faint)
                    .frame(width: 5, height: 5)
                Text(rttText)
                    .font(.system(size: 10, weight: .semibold, design: .monospaced))
                    .foregroundStyle(viewer.stats.videoRTTMilliseconds >= 0 ? Theme.accent : Theme.dim)
            }
            .padding(.horizontal, 9)
            .padding(.vertical, 5)
            .background(Capsule().fill(Theme.surfaceAlt))

            toolbarIcon("chart.bar.xaxis", active: showStats) {
                showStats.toggle()
            }
            toolbarIcon("chevron.up", active: false) {
                withAnimation(.easeOut(duration: 0.18)) { chromeVisible = false }
            }
        }
        .padding(8)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Theme.sidebar.opacity(0.92)))
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .stroke(Theme.divider, lineWidth: 1))
    }

    private var bottomBar: some View {
        HStack(spacing: 8) {
            sessionButton("chart.bar.xaxis", "统计", active: showStats) {
                showStats.toggle()
            }
            sessionButton("keyboard", "释放按键", active: false) {
                viewer.surface.releaseEverything()
            }
            sessionButton("arrow.up.left.and.arrow.down.right", "全屏", active: false) {
                NSApp.keyWindow?.toggleFullScreen(nil)
            }
            Spacer()
            Button {
                viewer.disconnect()
            } label: {
                HStack(spacing: 5) {
                    Image(systemName: "power")
                        .font(.system(size: 10, weight: .semibold))
                    Text("断开")
                        .font(.system(size: 11, weight: .semibold))
                }
                .foregroundStyle(Theme.danger)
                .padding(.horizontal, 12)
                .frame(height: 30)
                .background(Theme.danger.opacity(0.12))
                .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
            }
            .buttonStyle(.plain)
        }
        .padding(8)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Theme.sidebar.opacity(0.92)))
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .stroke(Theme.divider, lineWidth: 1))
    }

    private var statsPanel: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack {
                Text("会话统计")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(Theme.text)
                Spacer()
                Text(viewer.stats.videoPath)
                    .font(.system(size: 9))
                    .foregroundStyle(Theme.dim)
            }
            StatsGrid(rows: [
                ("分辨率", "\(viewer.stats.width) × \(viewer.stats.height)"),
                ("帧率", String(format: "%.1f fps", viewer.stats.fps)),
                ("视频延迟", rtt(viewer.stats.videoRTTMilliseconds)),
                ("控制延迟", rtt(viewer.stats.controlRTTMilliseconds)),
                ("平均解码", String(format: "%.1f ms", viewer.stats.decodeMilliseconds)),
                ("丢帧", "\(viewer.stats.droppedFrames)"),
                ("已接收", bytes(viewer.stats.receivedBytes))
            ])
        }
        .padding(13)
        .frame(width: 236)
        .background(
            RoundedRectangle(cornerRadius: 13, style: .continuous)
                .fill(Theme.sidebar.opacity(0.94)))
        .overlay(
            RoundedRectangle(cornerRadius: 13, style: .continuous)
                .stroke(Theme.divider, lineWidth: 1))
    }

    private func toolbarIcon(_ name: String, active: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: name)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(active ? Theme.accent : Theme.dim)
                .frame(width: 30, height: 30)
                .background(RoundedRectangle(cornerRadius: 8).fill(Theme.surfaceHigh))
        }
        .buttonStyle(.plain)
    }

    private func sessionButton(_ icon: String, _ title: String, active: Bool,
                               action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 5) {
                Image(systemName: icon)
                    .font(.system(size: 10, weight: .semibold))
                Text(title)
                    .font(.system(size: 11, weight: .medium))
            }
            .foregroundStyle(active ? Theme.accent : Theme.dim)
            .padding(.horizontal, 11)
            .frame(height: 30)
            .background(Theme.surfaceHigh)
            .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
        }
        .buttonStyle(.plain)
    }

    private var rttText: String {
        let value = viewer.stats.videoRTTMilliseconds
        return value >= 0 ? String(format: "%.0f ms", value) : "— ms"
    }

    private func rtt(_ value: Double) -> String {
        value >= 0 ? String(format: "%.1f ms", value) : "—"
    }

    private func bytes(_ count: Int) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(count), countStyle: .file)
    }
}

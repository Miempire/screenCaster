//
//  ScreenCastMacApp.swift
//  The macOS workspace: a quiet sidebar, a remote-assistance desk, the host page, and a
//  full-window session overlay.
//

import SwiftUI

enum WorkspaceSection: String, CaseIterable, Identifiable {
    case devices
    case assist
    case host
    case settings

    var id: String { rawValue }

    var title: String {
        switch self {
        case .devices: return "设备"
        case .assist: return "远程协助"
        case .host: return "被控这台 Mac"
        case .settings: return "设置"
        }
    }

    var icon: String {
        switch self {
        case .devices: return "rectangle.stack"
        case .assist: return "dot.radiowaves.left.and.right"
        case .host: return "display"
        case .settings: return "gearshape"
        }
    }
}

@main
struct ScreenCastMacApp: App {
    var body: some Scene {
        WindowGroup("ScreenCast") {
            RootView()
                .preferredColorScheme(.dark)
        }
        .defaultSize(width: 1180, height: 780)
        .windowResizability(.contentMinSize)
    }
}

struct RootView: View {
    @StateObject private var viewer = ViewerSession()
    @StateObject private var host = HostSession()
    @StateObject private var book = DeviceBook()

    @State private var section: WorkspaceSection = .devices
    @State private var activeDeviceName = ""

    var body: some View {
        HStack(spacing: 0) {
            SidebarView(section: $section, viewer: viewer, host: host)
            Rectangle()
                .fill(Theme.divider)
                .frame(width: 1)

            Group {
                switch section {
                case .devices:
                    DevicesPage(book: book,
                                viewer: viewer,
                                onConnect: connect,
                                onConnectByCode: connectByCode,
                                onDirectConnect: connectDirect)
                case .assist:
                    AssistPage(book: book,
                               viewer: viewer,
                               onConnect: connect,
                               onConnectByCode: connectByCode,
                               onDirectConnect: connectDirect)
                case .host:
                    HostPage(host: host)
                case .settings:
                    SettingsPage(book: book)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Theme.bg)
        }
        .frame(minWidth: 1060, minHeight: 700)
        .background(Theme.bg)
        .overlay {
            if viewer.connected {
                SessionOverlay(viewer: viewer, deviceName: activeDeviceName)
                    .transition(.opacity)
            }
        }
        .animation(.easeInOut(duration: 0.18), value: viewer.connected)
        .onChange(of: viewer.connected) { connected in
            if !connected {
                activeDeviceName = ""
            }
        }
    }

    private func connect(_ device: SavedDevice) {
        activeDeviceName = device.name
        viewer.hostAddress = device.host
        viewer.hostVideoPort = String(device.videoPort)
        viewer.hostControlPort = String(device.controlPort)
        viewer.connect()
        book.touch(code: device.code)
    }

    private func connectByCode(_ code: String) {
        guard !viewer.connected else { return }
        activeDeviceName = "连接码 \(code)"
        Task { await viewer.connectByCode(code) }
    }

    private func connectDirect(_ host: String, _ videoPort: Int, _ controlPort: Int) {
        let saved = book.remember(name: host,
                                  host: host,
                                  videoPort: videoPort,
                                  controlPort: controlPort)
        connect(saved)
    }
}

struct SidebarView: View {
    @Binding var section: WorkspaceSection
    @ObservedObject var viewer: ViewerSession
    @ObservedObject var host: HostSession

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                ZStack {
                    RoundedRectangle(cornerRadius: 9, style: .continuous)
                        .fill(Theme.accent)
                    Image(systemName: "display")
                        .font(.system(size: 14, weight: .bold))
                        .foregroundStyle(Theme.accentInk)
                }
                .frame(width: 32, height: 32)

                VStack(alignment: .leading, spacing: 2) {
                    Text("ScreenCast")
                        .font(.system(size: 15, weight: .semibold))
                        .foregroundStyle(Theme.text)
                    Text("安全远程桌面")
                        .font(.system(size: 10))
                        .foregroundStyle(Theme.faint)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 16)
            .padding(.top, 18)
            .padding(.bottom, 22)

            Text("工作台")
                .font(.system(size: 9, weight: .semibold))
                .foregroundStyle(Theme.faint)
                .padding(.horizontal, 18)

            VStack(spacing: 4) {
                ForEach(WorkspaceSection.allCases) { item in
                    navButton(item)
                }
            }
            .padding(.horizontal, 10)
            .padding(.top, 9)

            Spacer(minLength: 12)

            statusCard
        }
        .frame(width: 220)
        .background(Theme.sidebar)
    }

    private func navButton(_ item: WorkspaceSection) -> some View {
        let active = section == item
        return Button {
            section = item
        } label: {
            HStack(spacing: 10) {
                Image(systemName: item.icon)
                    .font(.system(size: 12, weight: .semibold))
                    .frame(width: 18)
                Text(item.title)
                    .font(.system(size: 12, weight: active ? .semibold : .regular))
                Spacer(minLength: 0)
                if item == .host && host.running {
                    Circle()
                        .fill(Theme.accent)
                        .frame(width: 5, height: 5)
                }
            }
            .foregroundStyle(active ? Theme.accent : Theme.dim)
            .padding(.horizontal, 10)
            .frame(height: 36)
            .background(
                RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .fill(active ? Theme.accentSoft : Color.clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private var statusCard: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 6) {
                Circle()
                    .fill(viewer.connected ? Theme.accent : (host.running ? Theme.warning : Theme.faint))
                    .frame(width: 6, height: 6)
                Text(statusTitle)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(Theme.text)
                Spacer(minLength: 0)
            }

            Text(statusDetail)
                .font(.system(size: 9))
                .foregroundStyle(Theme.faint)
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)

            Divider()
                .overlay(Theme.divider)

            Text("Rust 核心 \(CoreVersion.text)")
                .font(.system(size: 9))
                .foregroundStyle(Theme.faint)
        }
        .padding(12)
        .background(Theme.surface.opacity(0.65))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .stroke(Theme.divider, lineWidth: 1))
        .padding(12)
    }

    private var statusTitle: String {
        if viewer.connected { return "控制中" }
        if host.running { return "被控中" }
        return "本机空闲"
    }

    private var statusDetail: String {
        if viewer.connected { return viewer.statusText }
        if host.running { return host.statusText }
        return "服务器 jsspace.xyz · UDP 3479"
    }
}

//
//  SettingsPanel.swift
//  About, security notes, keyboard reference and local data.
//

import SwiftUI

struct SettingsPage: View {
    @ObservedObject var book: DeviceBook

    @State private var confirmClear = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                SectionHeader(title: "设置", subtitle: "版本信息、连接说明与本地数据")

                aboutCard
                securityCard
                SurfaceCard {
                    KeyMapReference()
                }
                dataCard
            }
            .padding(26)
            .frame(maxWidth: 860, alignment: .leading)
        }
    }

    private var aboutCard: some View {
        SurfaceCard {
            HStack(spacing: 14) {
                ZStack {
                    RoundedRectangle(cornerRadius: 14, style: .continuous)
                        .fill(Theme.accent)
                    Image(systemName: "display")
                        .font(.system(size: 22, weight: .semibold))
                        .foregroundStyle(Theme.accentInk)
                }
                .frame(width: 56, height: 56)

                VStack(alignment: .leading, spacing: 4) {
                    Text("ScreenCast")
                        .font(.system(size: 18, weight: .semibold))
                        .foregroundStyle(Theme.text)
                    Text("安全点对点远程桌面 · macOS")
                        .font(.system(size: 11))
                        .foregroundStyle(Theme.dim)
                    HStack(spacing: 6) {
                        Badge(text: "版本 \(versionText)")
                        Badge(text: "Rust 核心 \(CoreVersion.text)", tint: Theme.dim,
                              fill: Theme.surfaceAlt)
                    }
                    .padding(.top, 2)
                }
                Spacer()
            }
        }
    }

    private var securityCard: some View {
        SurfaceCard {
            VStack(alignment: .leading, spacing: 10) {
                Text("连接与安全")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.text)

                InfoRow(title: "传输方式", value: "UDP 点对点，视频与控制分两条通路")
                InfoRow(title: "负载加密", value: "AES-256-GCM，密钥由 HKDF 从共享密钥派生")
                InfoRow(title: "连接码", value: "9 位本地快捷码，只保存在这台 Mac 上")
                InfoRow(title: "信令", value: "未包含；两端需要网络可达", valueColor: Theme.warning)
                InfoRow(title: "输入租约", value: "同一时刻仅一个控制端可注入键鼠")
            }
        }
    }

    private var dataCard: some View {
        SurfaceCard {
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Text("本地数据")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(Theme.text)
                    Spacer()
                    Badge(text: "\(book.devices.count) 台设备", tint: Theme.dim,
                          fill: Theme.surfaceAlt)
                }

                Text("设备名、地址、端口和连接码保存在 UserDefaults 中，不上传。")
                    .font(.system(size: 11))
                    .foregroundStyle(Theme.dim)

                HStack {
                    Button("清空设备列表") {
                        confirmClear = true
                    }
                    .buttonStyle(QuietButtonStyle(tint: Theme.danger, height: 30))
                    .disabled(book.devices.isEmpty)
                    Spacer()
                }
            }
        }
        .confirmationDialog("清空所有已保存设备？", isPresented: $confirmClear,
                            titleVisibility: .visible) {
            Button("清空", role: .destructive) {
                book.removeAll()
            }
            Button("取消", role: .cancel) {}
        } message: {
            Text("连接码与设备记录会一并删除，之后需要重新用 IP 直连。")
        }
    }

    private var versionText: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
    }
}

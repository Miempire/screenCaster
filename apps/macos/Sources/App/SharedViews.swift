//
//  SharedViews.swift
//  Small presentation helpers used by the workspace pages.
//

import AppKit
import SwiftUI

/// Hosts the `RemoteSurfaceView` inside SwiftUI.
///
/// The view itself is owned by `ViewerSession` (not created here) so the input callbacks and the
/// decoded frames always address the same instance, even across SwiftUI body re-evaluations.
struct SurfaceRepresentable: NSViewRepresentable {
    let surface: RemoteSurfaceView

    func makeNSView(context: Context) -> RemoteSurfaceView {
        surface
    }

    func updateNSView(_ nsView: RemoteSurfaceView, context: Context) {
        // Nothing to push: the session writes into the view directly.
    }
}

/// Nine boxes over a hidden text field. The hidden field owns the digits and the focus so paste
/// and backspace behave exactly like a normal text field.
struct CodeEntryView: View {
    @Binding var code: String
    var boxHeight: CGFloat = 52

    @FocusState private var focused: Bool
    private let length = 9

    var body: some View {
        ZStack {
            HStack(spacing: 5) {
                ForEach(0 ..< length, id: \.self) { index in
                    ZStack {
                        RoundedRectangle(cornerRadius: 11, style: .continuous)
                            .fill(Theme.surfaceAlt)
                        RoundedRectangle(cornerRadius: 11, style: .continuous)
                            .stroke(active(index) ? Theme.accent : Theme.divider,
                                    lineWidth: active(index) ? 1.5 : 1)
                        Text(character(at: index))
                            .font(.system(size: 20, weight: .semibold, design: .monospaced))
                            .foregroundStyle(Theme.text)
                    }
                    .frame(maxWidth: .infinity)
                    .frame(height: boxHeight)
                }
            }

            TextField("", text: $code)
                .textFieldStyle(.plain)
                .focused($focused)
                .frame(width: 1, height: 1)
                .opacity(0.01)
                .onChange(of: code) { value in
                    let digits = value.filter { $0.isNumber }
                    let limited = String(digits.prefix(length))
                    if limited != code {
                        code = limited
                    }
                }
        }
        .contentShape(Rectangle())
        .onTapGesture { focused = true }
    }

    private func active(_ index: Int) -> Bool {
        focused && index == min(code.count, length - 1)
    }

    private func character(at index: Int) -> String {
        guard index < code.count else { return "" }
        return String(Array(code)[index])
    }
}

/// Two-column read-only key/value grid.
struct StatsGrid: View {
    let rows: [(String, String)]

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    Text(row.0)
                        .foregroundStyle(Theme.dim)
                        .frame(width: 92, alignment: .leading)
                    Text(row.1)
                        .foregroundStyle(Theme.text)
                        .monospacedDigit()
                        .textSelection(.enabled)
                    Spacer(minLength: 0)
                }
            }
        }
        .font(.system(size: 11))
    }
}

/// A permission line with an action button, used by the Host tab.
struct PermissionRow: View {
    let title: String
    let granted: Bool
    let grantedDetail: String
    let missingDetail: String
    let action: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: granted ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                .font(.system(size: 13))
                .foregroundStyle(granted ? Theme.accent : Theme.warning)
            Text(title)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(Theme.text)
                .frame(width: 78, alignment: .leading)
            Text(granted ? grantedDetail : missingDetail)
                .font(.system(size: 11))
                .foregroundStyle(granted ? Theme.dim : Theme.warning)
            Spacer(minLength: 0)
            if !granted {
                Button("打开设置", action: action)
                    .buttonStyle(QuietButtonStyle(tint: Theme.warning, height: 26))
            }
        }
    }
}

/// Collapsible reference for the keyboard translation table.
struct KeyMapReference: View {
    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            ScrollView {
                Text(KeyMap.tableDescription)
                    .font(.system(size: 10, design: .monospaced))
                    .foregroundStyle(Theme.dim)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(10)
            }
            .frame(height: 130)
            .background(Theme.bg)
            .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
        } label: {
            Text("键位映射 · PC 扫描码 ↔ macOS keyCode（\(KeyMap.bindings.count) 条）")
                .font(.system(size: 11))
                .foregroundStyle(Theme.dim)
        }
        .tint(Theme.dim)
    }
}

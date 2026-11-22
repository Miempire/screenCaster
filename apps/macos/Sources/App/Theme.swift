//
//  Theme.swift
//  Shared visual language for the macOS client: near-black surfaces, one green accent, and
//  quiet secondary text. Kept in one file so the pages cannot drift apart.
//

import SwiftUI

enum Theme {
    static let bg = Color(red: 0.055, green: 0.063, blue: 0.078)
    static let sidebar = Color(red: 0.043, green: 0.051, blue: 0.063)
    static let surface = Color(red: 0.086, green: 0.102, blue: 0.125)
    static let surfaceAlt = Color(red: 0.122, green: 0.141, blue: 0.176)
    static let surfaceHigh = Color(red: 0.149, green: 0.173, blue: 0.212)
    static let divider = Color.white.opacity(0.07)
    static let text = Color(red: 0.949, green: 0.957, blue: 0.973)
    static let dim = Color(red: 0.553, green: 0.580, blue: 0.639)
    static let faint = Color(red: 0.373, green: 0.400, blue: 0.451)
    static let accent = Color(red: 0.0, green: 0.824, blue: 0.416)
    static let accentInk = Color(red: 0.024, green: 0.125, blue: 0.082)
    static let accentSoft = Color(red: 0.071, green: 0.212, blue: 0.149)
    static let danger = Color(red: 1.0, green: 0.361, blue: 0.361)
    static let warning = Color(red: 1.0, green: 0.690, blue: 0.125)
}

struct SurfaceCard<Content: View>: View {
    private let padding: CGFloat
    private let content: Content

    init(padding: CGFloat = 18, @ViewBuilder content: () -> Content) {
        self.padding = padding
        self.content = content()
    }

    var body: some View {
        content
            .padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Theme.surface)
            .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 18, style: .continuous)
                    .stroke(Theme.divider, lineWidth: 1))
    }
}

struct SectionHeader: View {
    let title: String
    var subtitle: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title)
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(Theme.text)
            if let subtitle {
                Text(subtitle)
                    .font(.system(size: 11))
                    .foregroundStyle(Theme.dim)
            }
        }
    }
}

struct Badge: View {
    let text: String
    var tint: Color = Theme.accent
    var fill: Color = Theme.accentSoft

    var body: some View {
        Text(text)
            .font(.system(size: 10, weight: .semibold))
            .foregroundStyle(tint)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(Capsule().fill(fill))
    }
}

struct IconTile: View {
    let systemName: String
    var tint: Color = Theme.accent
    var background: Color = Theme.surfaceAlt
    var size: CGFloat = 42

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.3, style: .continuous)
                .fill(background)
            Image(systemName: systemName)
                .font(.system(size: size * 0.42, weight: .semibold))
                .foregroundStyle(tint)
        }
        .frame(width: size, height: size)
    }
}

struct AccentButtonStyle: ButtonStyle {
    var fill: Color = Theme.accent
    var foreground: Color = Theme.accentInk
    var height: CGFloat = 42

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(foreground)
            .padding(.horizontal, 18)
            .frame(maxWidth: .infinity)
            .frame(height: height)
            .background(fill.opacity(configuration.isPressed ? 0.78 : 1))
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            .contentShape(Rectangle())
    }
}

struct QuietButtonStyle: ButtonStyle {
    var tint: Color = Theme.text
    var height: CGFloat = 32

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(tint)
            .padding(.horizontal, 12)
            .frame(height: height)
            .background(Theme.surfaceHigh.opacity(configuration.isPressed ? 0.6 : 1))
            .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
            .contentShape(Rectangle())
    }
}

struct LabeledInput: View {
    let title: String
    @Binding var text: String
    var width: CGFloat?

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title)
                .font(.system(size: 10))
                .foregroundStyle(Theme.faint)
            TextField(title, text: $text)
                .textFieldStyle(.plain)
                .font(.system(size: 12))
                .foregroundStyle(Theme.text)
                .padding(.horizontal, 10)
                .frame(height: 36)
                .background(Theme.surfaceAlt)
                .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: 9, style: .continuous)
                        .stroke(Theme.divider, lineWidth: 1))
                .frame(maxWidth: width ?? .infinity, alignment: .leading)
        }
    }
}

struct InfoRow: View {
    let title: String
    let value: String
    var valueColor: Color = Theme.text

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title)
                .font(.system(size: 11))
                .foregroundStyle(Theme.dim)
                .frame(width: 92, alignment: .leading)
            Text(value)
                .font(.system(size: 11))
                .foregroundStyle(valueColor)
                .textSelection(.enabled)
            Spacer(minLength: 0)
        }
    }
}

extension View {
    func pageBackground() -> some View {
        background(Theme.bg)
    }
}

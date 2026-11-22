import SwiftUI
import ScreenCastCore

@main
struct ScreenCastApp: App {
    var body: some Scene {
        WindowGroup { ContentView() }
    }
}

struct ContentView: View {
    @State private var role = Int(sc_get_role())

    private var version: String {
        let value = sc_core_version()
        return "\((value >> 16) & 255).\((value >> 8) & 255).\(value & 255)"
    }

    var body: some View {
        NavigationStack {
            Form {
                Section("Local role") {
                    Picker("Role", selection: $role) {
                        Text("Host").tag(0)
                        Text("Viewer").tag(1)
                    }
                    .pickerStyle(.segmented)
                    .onChange(of: role) { value in
                        _ = sc_set_role(UInt8(value))
                        role = Int(sc_get_role())
                    }
                }
                Section("Foundation status") {
                    LabeledContent("Rust core", value: version)
                    LabeledContent("Role", value: role == 0 ? "Host" : "Viewer")
                    LabeledContent("Session", value: "Idle")
                    Text("Local role selection only. Streaming is not implemented.")
                }
                Section("Pending integrations") {
                    Text("ReplayKit capture, codecs, authenticated networking, video rendering and input control.")
                }
            }
            .navigationTitle("ScreenCast")
        }
    }
}

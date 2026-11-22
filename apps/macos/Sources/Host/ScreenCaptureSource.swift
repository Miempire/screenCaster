//
//  ScreenCaptureSource.swift
//  ScreenCaptureKit capture for the Host.
//
//  ScreenCaptureKit (macOS 12.3+) replaces the deprecated CGDisplayStream / CGWindowListCreateImage
//  paths. Flow:
//
//      SCShareableContent.excludingDesktopWindows  ->  SCDisplay list
//      SCContentFilter(display:excludingWindows:)  ->  what to capture
//      SCStreamConfiguration                       ->  size, frame interval, pixel format
//      SCStream + SCStreamOutput                   ->  CMSampleBuffer per frame
//
//  The configured width/height are also the encoder's dimensions: VideoToolbox requires the
//  pixel buffer to match the session, so both come from `targetSize(for:)` here.
//

import AppKit
import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

struct CaptureDisplay: Identifiable, Hashable {
    let id: UInt32
    let width: Int
    let height: Int
    let name: String
}

final class ScreenCaptureSource: NSObject, SCStreamOutput, SCStreamDelegate {
    enum CaptureError: LocalizedError {
        case noDisplay
        case shareableContentFailed(String)
        case startFailed(String)

        var errorDescription: String? {
            switch self {
            case .noDisplay:
                return "没有找到可共享的显示器。"
            case let .shareableContentFailed(message):
                return "SCShareableContent 失败：\(message)"
            case let .startFailed(message):
                return "SCStream 启动失败：\(message)"
            }
        }
    }

    private var stream: SCStream?
    private var handler: ((CVPixelBuffer, UInt64) -> Void)?
    private let sampleQueue = DispatchQueue(label: "org.screencast.macos.capture", qos: .userInitiated)

    /// True when the process already holds the Screen Recording permission.
    static var hasScreenRecordingPermission: Bool {
        CGPreflightScreenCaptureAccess()
    }

    /// Asks the system to show the Screen Recording prompt (only has an effect once per launch).
    static func requestScreenRecordingPermission() {
        _ = CGRequestScreenCaptureAccess()
    }

    /// Enumerates the displays ScreenCaptureKit will let this process capture.
    /// `NSScreen` is a main-thread API, hence the main actor.
    @MainActor
    static func availableDisplays() async throws -> [CaptureDisplay] {
        let content = try await shareableContent()
        return content.displays.map { display in
            let identifier = display.displayID
            let name = NSScreen.screens.first { screen in
                let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
                return number?.uint32Value == identifier
            }?.localizedName ?? "显示器 \(identifier)"
            return CaptureDisplay(id: identifier, width: display.width, height: display.height, name: name)
        }
    }

    private static func shareableContent() async throws -> SCShareableContent {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<SCShareableContent, Error>) in
            SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true) { content, error in
                if let error {
                    continuation.resume(throwing: CaptureError.shareableContentFailed(error.localizedDescription))
                    return
                }
                guard let content else {
                    continuation.resume(throwing: CaptureError.noDisplay)
                    return
                }
                continuation.resume(returning: content)
            }
        }
    }

    /// Computes the encode/capture size: the display's aspect ratio, capped on the long edge, with
    /// both dimensions even (H.264 4:2:0 requires even dimensions).
    static func targetSize(for display: CaptureDisplay, maxWidth: Int, maxHeight: Int) -> (width: Int, height: Int) {
        let sourceWidth = max(2, display.width)
        let sourceHeight = max(2, display.height)
        let scale = min(Double(maxWidth) / Double(sourceWidth), Double(maxHeight) / Double(sourceHeight), 1.0)
        var width = Int((Double(sourceWidth) * scale).rounded())
        var height = Int((Double(sourceHeight) * scale).rounded())
        width -= width % 2
        height -= height % 2
        return (max(2, width), max(2, height))
    }

    func start(displayID: CGDirectDisplayID,
               width: Int,
               height: Int,
               fps: Int,
               handler: @escaping (CVPixelBuffer, UInt64) -> Void) async throws {
        await stop()

        let content = try await ScreenCaptureSource.shareableContent()
        guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
            throw CaptureError.noDisplay
        }

        let filter = SCContentFilter(display: display, excludingWindows: [])

        let configuration = SCStreamConfiguration()
        configuration.width = width
        configuration.height = height
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(1, fps)))
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.queueDepth = 6
        configuration.showsCursor = true
        configuration.scalesToFit = true
        // Background colour is left at the default (black) so a failable CGColor initialiser is not
        // part of the build.

        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: sampleQueue)

        self.handler = handler
        self.stream = stream

        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            stream.startCapture { error in
                if let error {
                    continuation.resume(throwing: CaptureError.startFailed(error.localizedDescription))
                } else {
                    continuation.resume()
                }
            }
        }
    }

    func stop() async {
        handler = nil
        guard let stream else { return }
        self.stream = nil
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            stream.stopCapture { _ in
                continuation.resume()
            }
        }
    }

    // MARK: - SCStreamOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid else { return }

        // Frames whose status is not `.complete` are duplicates or placeholders (the screen did not
        // change). Encoding them would burn bitrate for nothing.
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let statusRaw = attachments.first?[.status] as? Int,
              let status = SCFrameStatus(rawValue: statusRaw),
              status == .complete else {
            return
        }

        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        let presentationTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let timestampUS: UInt64
        if presentationTime.isValid, presentationTime.seconds.isFinite, presentationTime.seconds >= 0 {
            timestampUS = UInt64((presentationTime.seconds * 1_000_000).rounded())
        } else {
            timestampUS = DispatchTime.now().uptimeNanoseconds / 1000
        }

        handler?(pixelBuffer, timestampUS)
    }

    // MARK: - SCStreamDelegate

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        handler = nil
        self.stream = nil
        NSLog("ScreenCaptureKit 停止：%@", error.localizedDescription)
    }
}

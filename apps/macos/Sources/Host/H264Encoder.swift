//
//  H264Encoder.swift
//  VideoToolbox H.264 encode for the Host.
//
//  VideoToolbox hands back AVCC (4-byte length prefixed NAL units) inside a CMBlockBuffer, while
//  the ScreenCast wire format carries Annex-B. The conversion here is the exact inverse of what
//  `H264Decoder` does on the receiving side:
//
//      CMBlockBuffer (AVCC)  ->  [00 00 00 01][NAL] ...  (Annex-B)
//
//  The SPS/PPS from the format description are prepended to every IDR access unit. The Windows
//  decoder rebuilds its format description from the access unit it receives, so a key frame that
//  did not carry its parameter sets would be undecodable — that is also what
//  `H264Bitstream.NormalizeToAnnexB` does for the Media Foundation encoder on Windows.
//

import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

final class H264Encoder {
    struct Output {
        let annexB: [UInt8]
        let isKeyFrame: Bool
        /// Capture timestamp carried through VideoToolbox as the presentation timestamp.
        let presentationTimeUS: UInt64
    }

    private var session: VTCompressionSession?
    let width: Int
    let height: Int
    private let fps: Int
    private let bitrate: Int
    private let handler: (Output) -> Void

    private let counterLock = NSLock()
    private var encodedCount = 0
    private var keyFrameCount = 0
    private var totalEncodeMicroseconds = 0
    private var lastError = ""

    var encodedFrames: Int {
        counterLock.lock()
        defer { counterLock.unlock() }
        return encodedCount
    }

    var keyFrames: Int {
        counterLock.lock()
        defer { counterLock.unlock() }
        return keyFrameCount
    }

    var averageEncodeMilliseconds: Double {
        counterLock.lock()
        defer { counterLock.unlock() }
        guard encodedCount > 0 else { return 0 }
        return Double(totalEncodeMicroseconds) / Double(encodedCount) / 1000
    }

    var errorDescription: String {
        counterLock.lock()
        defer { counterLock.unlock() }
        return lastError
    }

    init(width: Int, height: Int, fps: Int, bitrate: Int, handler: @escaping (Output) -> Void) {
        self.width = width
        self.height = height
        self.fps = max(1, fps)
        self.bitrate = max(200_000, bitrate)
        self.handler = handler
    }

    deinit {
        close()
    }

    /// Creates the compression session. Called after `init` so `self` can be used as the callback
    /// refcon.
    func prepare() -> Bool {
        if session != nil { return true }

        var specification: [String: Any] = [
            kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder as String: true,
        ]
        // Prefer the hardware encoder, but do not fail on machines where it is unavailable.
        specification[kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder as String] = false

        var attributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any](),
        ]
        attributes[kCVPixelBufferWidthKey as String] = width
        attributes[kCVPixelBufferHeightKey as String] = height

        var created: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: Int32(width),
            height: Int32(height),
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: specification as CFDictionary,
            imageBufferAttributes: attributes as CFDictionary,
            compressedDataAllocator: nil,
            outputCallback: { refcon, _, callbackStatus, _, sampleBuffer in
                guard let refcon else { return }
                Unmanaged<H264Encoder>.fromOpaque(refcon).takeUnretainedValue()
                    .didEncode(status: callbackStatus, sampleBuffer: sampleBuffer)
            },
            refcon: Unmanaged.passUnretained(self).toOpaque(),
            compressionSessionOut: &created)

        guard status == noErr, let created else {
            recordError("VTCompressionSessionCreate 失败：\(status)")
            return false
        }
        session = created

        // Real time, no B-frames: a remote desktop cannot afford reordering latency.
        setProperty(kVTCompressionPropertyKey_RealTime, NSNumber(value: true))
        setProperty(kVTCompressionPropertyKey_AllowFrameReordering, NSNumber(value: false))
        setProperty(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_High_AutoLevel)
        setProperty(kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: bitrate))
        setProperty(kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: fps))
        setProperty(kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: fps * 2))
        setProperty(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, NSNumber(value: 2))
        // Windows' decoder is fed by a plain Annex-B parser, so keep the stream simple.
        setProperty(kVTCompressionPropertyKey_AllowOpenGOP, NSNumber(value: false))

        let prepareStatus = VTCompressionSessionPrepareToEncodeFrames(created)
        if prepareStatus != noErr {
            recordError("VTCompressionSessionPrepareToEncodeFrames 失败：\(prepareStatus)")
            return false
        }
        return true
    }

    private func setProperty(_ key: CFString, _ value: CFTypeRef) {
        guard let session else { return }
        let status = VTSessionSetProperty(session, key: key, value: value)
        if status != noErr {
            // Not fatal: not every encoder supports every property.
            recordError("VTSessionSetProperty 失败：\(status)")
        }
    }

    @discardableResult
    func encode(pixelBuffer: CVPixelBuffer, presentationTimeUS: UInt64, forceKeyFrame: Bool) -> Bool {
        guard let session else { return false }

        let pts = CMTime(value: CMTimeValue(presentationTimeUS), timescale: 1_000_000)
        var frameProperties: CFDictionary?
        if forceKeyFrame {
            let properties: [String: Any] = [
                kVTEncodeFrameOptionKey_ForceKeyFrame as String: NSNumber(value: true),
            ]
            frameProperties = properties as CFDictionary
        }

        let started = DispatchTime.now().uptimeNanoseconds
        let status = VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: pts,
            duration: .invalid,
            frameProperties: frameProperties,
            sourceFrameRefcon: nil,
            infoFlagsOut: nil)
        let elapsed = (DispatchTime.now().uptimeNanoseconds - started) / 1000

        counterLock.lock()
        totalEncodeMicroseconds += Int(min(elapsed, UInt64(Int.max)))
        counterLock.unlock()

        if status != noErr {
            recordError("VTCompressionSessionEncodeFrame 失败：\(status)")
            return false
        }
        return true
    }

    func close() {
        if let session {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(session)
        }
        session = nil
    }

    // MARK: - Output

    private func didEncode(status: OSStatus, sampleBuffer: CMSampleBuffer?) {
        guard status == noErr else {
            recordError("编码回调返回 \(status)")
            return
        }
        guard let sampleBuffer, CMSampleBufferDataIsReady(sampleBuffer) else { return }
        guard let output = Self.annexB(from: sampleBuffer) else { return }

        counterLock.lock()
        encodedCount += 1
        if output.isKeyFrame { keyFrameCount += 1 }
        counterLock.unlock()

        handler(output)
    }

    /// AVCC `CMBlockBuffer` -> Annex-B access unit, with SPS/PPS in front of every IDR.
    static func annexB(from sampleBuffer: CMSampleBuffer) -> Output? {
        guard let formatDescription = CMSampleBufferGetFormatDescription(sampleBuffer),
              let dataBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else {
            return nil
        }

        // Parameter sets live in the format description, not in the AVCC sample.
        var nalLengthSize: Int32 = 4
        var spsPointer: UnsafePointer<UInt8>?
        var spsSize = 0
        var parameterSetCount = 0
        let spsStatus = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            formatDescription,
            parameterSetIndex: 0,
            parameterSetPointerOut: &spsPointer,
            parameterSetSizeOut: &spsSize,
            parameterSetCountOut: &parameterSetCount,
            nalUnitHeaderLengthOut: &nalLengthSize)

        var ppsPointer: UnsafePointer<UInt8>?
        var ppsSize = 0
        if parameterSetCount > 1 {
            _ = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                formatDescription,
                parameterSetIndex: 1,
                parameterSetPointerOut: &ppsPointer,
                parameterSetSizeOut: &ppsSize,
                parameterSetCountOut: nil,
                nalUnitHeaderLengthOut: nil)
        }

        let totalLength = CMBlockBufferGetDataLength(dataBuffer)
        guard totalLength > 0 else { return nil }

        var sample = [UInt8](repeating: 0, count: totalLength)
        let copyStatus: OSStatus = sample.withUnsafeMutableBytes { raw -> OSStatus in
            guard let base = raw.baseAddress else { return OSStatus(-1) }
            return CMBlockBufferCopyDataBytes(dataBuffer,
                                              atOffset: 0,
                                              dataLength: totalLength,
                                              destination: base)
        }
        guard copyStatus == noErr else { return nil }

        let lengthSize = Int(max(1, min(nalLengthSize, 4)))
        var output: [UInt8] = []
        output.reserveCapacity(totalLength + 64)
        var cursor = 0
        var hasIDR = false

        while cursor + lengthSize <= totalLength {
            var nalLength = 0
            for index in 0 ..< lengthSize {
                nalLength = (nalLength << 8) | Int(sample[cursor + index])
            }
            cursor += lengthSize
            guard nalLength > 0, cursor + nalLength <= totalLength else { break }
            let nalType = sample[cursor] & 0x1F
            if nalType == AnnexB.nalTypeIDR { hasIDR = true }
            AnnexB.appendAnnexB(Array(sample[cursor ..< cursor + nalLength]), to: &output)
            cursor += nalLength
        }

        guard !output.isEmpty else { return nil }

        if hasIDR, spsStatus == noErr, let spsPointer, spsSize > 0 {
            var prefixed: [UInt8] = []
            prefixed.reserveCapacity(output.count + spsSize + ppsSize + 8)
            AnnexB.appendAnnexB(Array(UnsafeBufferPointer(start: spsPointer, count: spsSize)), to: &prefixed)
            if let ppsPointer, ppsSize > 0 {
                AnnexB.appendAnnexB(Array(UnsafeBufferPointer(start: ppsPointer, count: ppsSize)), to: &prefixed)
            }
            prefixed.append(contentsOf: output)
            output = prefixed
        }

        return Output(annexB: output, isKeyFrame: hasIDR, presentationTimeUS: presentationTimeUS(of: sampleBuffer))
    }

    /// Recovers the capture timestamp that was handed to `encode` as the presentation timestamp.
    private static func presentationTimeUS(of sampleBuffer: CMSampleBuffer) -> UInt64 {
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        guard pts.isValid, pts.seconds.isFinite, pts.seconds >= 0 else { return 0 }
        return UInt64((pts.seconds * 1_000_000).rounded())
    }

    private func recordError(_ message: String) {
        counterLock.lock()
        lastError = message
        counterLock.unlock()
    }
}

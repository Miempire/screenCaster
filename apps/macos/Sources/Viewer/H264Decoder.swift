//
//  H264Decoder.swift
//  VideoToolbox H.264 decode for the Viewer.
//
//  Pipeline per access unit:
//      Annex-B bytes
//        -> split into NAL units
//        -> SPS(7)/PPS(8) build a CMVideoFormatDescription via
//           CMVideoFormatDescriptionCreateFromH264ParameterSets
//        -> remaining NAL units rewritten as AVCC (4-byte length prefix)
//        -> CMSampleBuffer -> VTDecompressionSessionDecodeFrame
//        -> CVPixelBuffer -> CMSampleBuffer wrapped for AVSampleBufferDisplayLayer
//
//  The format description is rebuilt whenever the parameter sets change; the decoder session is
//  invalidated with it, because a VTDecompressionSession is bound to one format description.
//

import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

final class H264Decoder {
    /// Called on a VideoToolbox thread with a display-ready sample buffer.
    var onFrame: ((CMSampleBuffer, CGSize) -> Void)?
    /// Called on a VideoToolbox thread with a human readable failure reason.
    var onError: ((String) -> Void)?

    private var session: VTDecompressionSession?
    private var formatDescription: CMVideoFormatDescription?
    private var sps: [UInt8]?
    private var pps: [UInt8]?

    private let counterLock = NSLock()
    private var decodedCount = 0
    private var droppedCount = 0
    private var totalDecodeMicroseconds = 0

    var decodedFrames: Int {
        counterLock.lock()
        defer { counterLock.unlock() }
        return decodedCount
    }

    var droppedFrames: Int {
        counterLock.lock()
        defer { counterLock.unlock() }
        return droppedCount
    }

    var averageDecodeMilliseconds: Double {
        counterLock.lock()
        defer { counterLock.unlock() }
        guard decodedCount > 0 else { return 0 }
        return Double(totalDecodeMicroseconds) / Double(decodedCount) / 1000
    }

    /// Destination pixel format handed to the decoder.
    ///
    /// NV12 bi-planar video range is what the hardware decoder produces natively, and
    /// AVSampleBufferDisplayLayer accepts it without a conversion. Switch to
    /// `kCVPixelFormatType_32BGRA` if a particular machine refuses it.
    private static let destinationPixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange

    private var destinationAttributes: [String: Any] {
        [
            kCVPixelBufferPixelFormatTypeKey as String: H264Decoder.destinationPixelFormat,
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any](),
        ]
    }

    // MARK: - Decode

    func decode(annexB data: [UInt8], presentationTimeUS: UInt64) {
        guard !data.isEmpty else { return }
        let started = DispatchTime.now().uptimeNanoseconds
        var decodedSomething = false
        defer {
            if decodedSomething {
                let elapsed = DispatchTime.now().uptimeNanoseconds - started
                counterLock.lock()
                totalDecodeMicroseconds += Int(min(elapsed / 1000, UInt64(Int.max)))
                counterLock.unlock()
            }
        }

        let units = AnnexB.split(data)
        guard !units.isEmpty else { return }

        var parameterSetsChanged = false
        for unit in units {
            switch unit.type {
            case AnnexB.nalTypeSPS:
                let payload = AnnexB.bytes(of: unit, from: data)
                if !payload.isEmpty {
                    sps = payload
                    parameterSetsChanged = true
                }
            case AnnexB.nalTypePPS:
                let payload = AnnexB.bytes(of: unit, from: data)
                if !payload.isEmpty {
                    pps = payload
                    parameterSetsChanged = true
                }
            default:
                break
            }
        }

        if parameterSetsChanged || formatDescription == nil {
            guard rebuildFormatDescription() else {
                onError?("参数集不完整，无法建立解码格式（等待关键帧）。")
                return
            }
        }

        var avcc: [UInt8] = []
        avcc.reserveCapacity(data.count)
        var hasSlice = false
        for unit in units {
            // SPS/PPS live in the format description, not in the sample.
            if unit.type == AnnexB.nalTypeSPS || unit.type == AnnexB.nalTypePPS {
                continue
            }
            let payload = AnnexB.bytes(of: unit, from: data)
            guard !payload.isEmpty else { continue }
            if unit.type == AnnexB.nalTypeNonIDR || unit.type == AnnexB.nalTypeIDR {
                hasSlice = true
            }
            AnnexB.appendAVCC(payload, to: &avcc)
        }

        guard hasSlice, !avcc.isEmpty else { return }
        guard let session, let formatDescription else { return }

        let pts = CMTime(value: CMTimeValue(presentationTimeUS), timescale: 1_000_000)
        guard let sampleBuffer = makeSampleBuffer(avcc: avcc,
                                                  formatDescription: formatDescription,
                                                  presentationTime: pts) else {
            recordDrop()
            onError?("无法为解码器构造 CMSampleBuffer。")
            return
        }

        var infoFlags = VTDecodeInfoFlags()
        let status = VTDecompressionSessionDecodeFrame(
            session,
            sampleBuffer: sampleBuffer,
            flags: [],
            frameRefcon: nil,
            infoFlagsOut: &infoFlags)

        decodedSomething = true
        if status != noErr {
            recordDrop()
            onError?("VTDecompressionSessionDecodeFrame 失败：\(status)")
        }
    }

    private func makeSampleBuffer(avcc: [UInt8],
                                  formatDescription: CMVideoFormatDescription,
                                  presentationTime: CMTime) -> CMSampleBuffer? {
        var blockBuffer: CMBlockBuffer?
        let createStatus = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: avcc.count,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: avcc.count,
            flags: 0,
            blockBufferOut: &blockBuffer)
        guard createStatus == noErr, let blockBuffer else { return nil }

        let memoryStatus = CMBlockBufferAssureBlockMemory(blockBuffer)
        guard memoryStatus == noErr else { return nil }

        let copyStatus = avcc.withUnsafeBytes { raw -> OSStatus in
            guard let base = raw.baseAddress else { return OSStatus(-1) }
            return CMBlockBufferReplaceDataBytes(with: base,
                                                 blockBuffer: blockBuffer,
                                                 offsetIntoDestination: 0,
                                                 dataLength: avcc.count)
        }
        guard copyStatus == noErr else { return nil }

        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: presentationTime,
            decodeTimeStamp: .invalid)

        var sampleBuffer: CMSampleBuffer?
        let sampleStatus = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            formatDescription: formatDescription,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 0,
            sampleSizeArray: nil,
            sampleBufferOut: &sampleBuffer)
        guard sampleStatus == noErr else { return nil }
        return sampleBuffer
    }

    // MARK: - Format description / session

    private func rebuildFormatDescription() -> Bool {
        guard let sps, let pps, !sps.isEmpty, !pps.isEmpty else { return false }

        var description: CMVideoFormatDescription?
        let status: OSStatus = sps.withUnsafeBufferPointer { spsBuffer in
            pps.withUnsafeBufferPointer { ppsBuffer in
                var pointers: [UnsafePointer<UInt8>] = []
                if let base = spsBuffer.baseAddress { pointers.append(base) }
                if let base = ppsBuffer.baseAddress { pointers.append(base) }
                var sizes: [Int] = [sps.count, pps.count]
                return pointers.withUnsafeBufferPointer { pointerBuffer in
                    sizes.withUnsafeMutableBufferPointer { sizeBuffer in
                        CMVideoFormatDescriptionCreateFromH264ParameterSets(
                            allocator: kCFAllocatorDefault,
                            parameterSetCount: pointerBuffer.count,
                            parameterSetPointers: pointerBuffer.baseAddress!,
                            parameterSetSizes: sizeBuffer.baseAddress!,
                            nalUnitHeaderLength: 4,
                            formatDescriptionOut: &description)
                    }
                }
            }
        }

        guard status == noErr, let description else {
            onError?("CMVideoFormatDescriptionCreateFromH264ParameterSets 失败：\(status)")
            return false
        }

        formatDescription = description
        recreateSession(formatDescription: description)
        return true
    }

    private func recreateSession(formatDescription: CMVideoFormatDescription) {
        if let session {
            VTDecompressionSessionWaitForAsynchronousFrames(session)
            VTDecompressionSessionInvalidate(session)
        }
        session = nil

        var record = VTDecompressionOutputCallbackRecord(
            decompressionOutputCallback: { refcon, _, status, infoFlags, imageBuffer, presentationTime, _ in
                guard let refcon else { return }
                Unmanaged<H264Decoder>.fromOpaque(refcon).takeUnretainedValue()
                    .didDecode(status: status,
                               infoFlags: infoFlags,
                               imageBuffer: imageBuffer,
                               presentationTime: presentationTime)
            },
            decompressionOutputRefCon: Unmanaged.passUnretained(self).toOpaque())

        var created: VTDecompressionSession?
        let status = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: formatDescription,
            videoDecoderSpecification: nil,
            destinationImageBufferAttributes: destinationAttributes as CFDictionary,
            outputCallback: &record,
            decompressionSessionOut: &created)

        if status == noErr, let created {
            session = created
        } else {
            session = nil
            onError?("VTDecompressionSessionCreate 失败：\(status)")
        }
    }

    private func didDecode(status: OSStatus,
                           infoFlags: VTDecodeInfoFlags,
                           imageBuffer: CVImageBuffer?,
                           presentationTime: CMTime) {
        guard status == noErr else {
            recordDrop()
            onError?("VT 解码返回 \(status)")
            return
        }
        if infoFlags.contains(.frameDropped) {
            recordDrop()
            return
        }
        guard let imageBuffer else {
            recordDrop()
            return
        }
        guard let displayBuffer = H264Decoder.makeDisplaySampleBuffer(
            imageBuffer: imageBuffer,
            presentationTime: presentationTime) else {
            recordDrop()
            return
        }

        counterLock.lock()
        decodedCount += 1
        counterLock.unlock()

        onFrame?(displayBuffer, CVImageBufferGetDisplaySize(imageBuffer))
    }

    /// Wraps a decoded pixel buffer so `AVSampleBufferDisplayLayer` can show it.
    ///
    /// `DisplayImmediately` is what removes the need to drive the layer with a timebase: the frame
    /// is shown as soon as it is enqueued, which is the behaviour a remote desktop wants.
    static func makeDisplaySampleBuffer(imageBuffer: CVImageBuffer,
                                        presentationTime: CMTime) -> CMSampleBuffer? {
        var formatDescription: CMVideoFormatDescription?
        let formatStatus = CMVideoFormatDescriptionCreateForImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: imageBuffer,
            formatDescriptionOut: &formatDescription)
        guard formatStatus == noErr, let formatDescription else { return nil }

        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: presentationTime,
            decodeTimeStamp: .invalid)

        var sampleBuffer: CMSampleBuffer?
        let sampleStatus = CMSampleBufferCreateReadyWithImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: imageBuffer,
            formatDescription: formatDescription,
            sampleTiming: &timing,
            sampleBufferOut: &sampleBuffer)
        guard sampleStatus == noErr, let sampleBuffer else { return nil }

        CMSetAttachment(sampleBuffer,
                        key: kCMSampleAttachmentKey_DisplayImmediately,
                        value: kCFBooleanTrue,
                        attachmentMode: kCMAttachmentMode_ShouldPropagate)
        return sampleBuffer
    }

    private func recordDrop() {
        counterLock.lock()
        droppedCount += 1
        counterLock.unlock()
    }

    func close() {
        if let session {
            VTDecompressionSessionWaitForAsynchronousFrames(session)
            VTDecompressionSessionInvalidate(session)
        }
        session = nil
        formatDescription = nil
        sps = nil
        pps = nil
    }

    deinit {
        close()
    }
}

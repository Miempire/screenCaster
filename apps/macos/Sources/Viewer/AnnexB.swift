//
//  AnnexB.swift
//  H.264 Annex-B byte-stream splitting.
//
//  The ScreenCast wire format carries Annex-B (start-code delimited) access units in both
//  directions, while VideoToolbox speaks AVCC (4-byte big-endian length prefixed) at the decode
//  boundary and emits AVCC from the encoder. This file is the single place where the two forms
//  are converted, so the decoder and the encoder cannot disagree about it.
//

import Foundation

enum AnnexB {
    /// A NAL unit located inside a larger access unit.
    struct NALUnit {
        /// Offset of the first payload byte (after the start code).
        let offset: Int
        /// Payload length, trailing zero bytes already trimmed.
        let length: Int
        /// `nal_unit_type` from the first payload byte.
        let type: UInt8
    }

    /// Standard start code.
    static let startCode: [UInt8] = [0x00, 0x00, 0x00, 0x01]

    /// Splits an Annex-B buffer into NAL units.
    ///
    /// Both the 3-byte and the 4-byte start code are accepted, which is what a real bitstream
    /// contains. Trailing zero bytes before the next start code are trimmed: with a 4-byte start
    /// code the leading zero of the *next* code would otherwise land at the end of this payload,
    /// and a NAL whose last byte is 0x00 upsets strict decoders.
    static func split(_ data: [UInt8]) -> [NALUnit] {
        guard data.count >= 4 else { return [] }
        var units: [NALUnit] = []
        var index = 0
        var payloadStart = -1

        while index + 2 < data.count {
            if data[index] == 0, data[index + 1] == 0, data[index + 2] == 1 {
                if payloadStart >= 0 {
                    append(&units, data: data, start: payloadStart, end: index)
                }
                index += 3
                payloadStart = index
                continue
            }
            index += 1
        }

        if payloadStart >= 0 {
            append(&units, data: data, start: payloadStart, end: data.count)
        }
        return units
    }

    private static func append(_ units: inout [NALUnit], data: [UInt8], start: Int, end: Int) {
        var trimmed = end
        while trimmed > start, data[trimmed - 1] == 0 {
            trimmed -= 1
        }
        guard trimmed > start else { return }
        units.append(NALUnit(offset: start, length: trimmed - start, type: data[start] & 0x1F))
    }

    /// Copies one NAL unit's payload out of the source buffer.
    static func bytes(of unit: NALUnit, from data: [UInt8]) -> [UInt8] {
        guard unit.offset >= 0, unit.length > 0, unit.offset + unit.length <= data.count else { return [] }
        return Array(data[unit.offset ..< unit.offset + unit.length])
    }

    /// Wraps a NAL payload in a 4-byte length prefix (AVCC).
    static func appendAVCC(_ payload: [UInt8], to output: inout [UInt8]) {
        let count = payload.count
        output.append(UInt8((count >> 24) & 0xFF))
        output.append(UInt8((count >> 16) & 0xFF))
        output.append(UInt8((count >> 8) & 0xFF))
        output.append(UInt8(count & 0xFF))
        output.append(contentsOf: payload)
    }

    /// Appends `00 00 00 01` followed by the payload.
    static func appendAnnexB(_ payload: [UInt8], to output: inout [UInt8]) {
        output.append(contentsOf: startCode)
        output.append(contentsOf: payload)
    }

    static let nalTypeSPS: UInt8 = 7
    static let nalTypePPS: UInt8 = 8
    static let nalTypeIDR: UInt8 = 5
    static let nalTypeNonIDR: UInt8 = 1
}

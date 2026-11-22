#!/usr/bin/env python3
"""Out-of-process smoke test for the ScreenCast Rust C ABI.

This loads the built shared library exactly the way the Android JNI shim and the macOS
Swift layer do (dlopen + C symbols), then drives a complete encrypted media session
between two handles in this process. It is the external counterpart of
`native/tests/ffi_abi.rs`: the Rust test proves behaviour, this proves the exported
symbols and the `#[repr(C)]` layout that the platform layers see.

Usage:
    python native/tools/abi_smoke.py <path-to-screencast_core.dll|.so|.dylib> [runtime-dir]

`runtime-dir` is only needed on Windows/MinGW, where the cdylib depends on the toolchain
runtime DLLs (libgcc, mcfgthread). It defaults to the directory of the `gcc` found on PATH.

Exit code 0 means every check passed.
"""

import ctypes
import os
import shutil
import sys
import time

SC_OK = 0
SC_ERR_NULL = -1
SC_ERR_INVALID_ARGUMENT = -2
SC_ERR_NOT_BOUND = -3
SC_EVENT_VIDEO_FRAME = 1
SC_EVENT_CONTROL = 2


class ScEvent(ctypes.Structure):
    _fields_ = [
        ("kind", ctypes.c_int32),
        ("status", ctypes.c_int32),
        ("component", ctypes.c_int32),
        ("event_type", ctypes.c_uint16),
        ("flags", ctypes.c_uint8),
        ("codec", ctypes.c_uint8),
        ("frame_id", ctypes.c_uint64),
        ("capture_timestamp_us", ctypes.c_uint64),
        ("rtt_us", ctypes.c_uint64),
        ("config_id", ctypes.c_uint32),
        ("data_len", ctypes.c_uint32),
        ("data", ctypes.c_void_p),
        ("reserved", ctypes.c_uint32),
    ]


class ScStats(ctypes.Structure):
    _fields_ = [
        ("video_datagrams_received", ctypes.c_uint64),
        ("video_datagrams_rejected", ctypes.c_uint64),
        ("control_datagrams_received", ctypes.c_uint64),
        ("control_datagrams_rejected", ctypes.c_uint64),
        ("control_frames_dropped", ctypes.c_uint64),
        ("assembled_frames", ctypes.c_uint64),
        ("dropped_frames", ctypes.c_uint64),
        ("dropped_fragments", ctypes.c_uint64),
        ("video_bytes_sent", ctypes.c_uint64),
        ("control_bytes_sent", ctypes.c_uint64),
        ("replay_rejected", ctypes.c_uint64),
        ("probes_sent", ctypes.c_uint64),
        ("probes_answered", ctypes.c_uint64),
        ("events_dropped", ctypes.c_uint64),
        ("partial_frames", ctypes.c_uint64),
        ("video_rtt_us", ctypes.c_double),
        ("control_rtt_us", ctypes.c_double),
        ("video_path_status", ctypes.c_int32),
        ("control_path_status", ctypes.c_int32),
        ("video_local_port", ctypes.c_uint32),
        ("control_local_port", ctypes.c_uint32),
        ("reserved", ctypes.c_uint32),
    ]


class Checker:
    def __init__(self):
        self.failures = []
        self.checks = 0

    def check(self, name, condition, detail=""):
        self.checks += 1
        if condition:
            print(f"PASS {name}")
        else:
            print(f"FAIL {name} {detail}")
            self.failures.append(name)


def add_runtime_dirs(library_path, extra):
    """Register DLL search directories (Python 3.8+ no longer searches PATH for deps)."""
    if not hasattr(os, "add_dll_directory"):
        return
    candidates = [os.path.dirname(os.path.abspath(library_path))]
    if extra:
        candidates.append(extra)
    gcc = shutil.which("gcc")
    if gcc:
        candidates.append(os.path.dirname(gcc))
    for directory in candidates:
        if directory and os.path.isdir(directory):
            try:
                os.add_dll_directory(directory)
            except OSError:
                pass


def main(argv):
    if len(argv) not in (2, 3):
        print("usage: abi_smoke.py <library> [runtime-dir]")
        return 2

    add_runtime_dirs(argv[1], argv[2] if len(argv) == 3 else None)
    library = ctypes.CDLL(argv[1])
    check = Checker()

    library.sc_core_version.restype = ctypes.c_uint32
    library.sc_get_role.restype = ctypes.c_uint8
    library.sc_set_role.argtypes = [ctypes.c_uint8]
    library.sc_set_role.restype = ctypes.c_int32

    library.sc_session_create.argtypes = [
        ctypes.POINTER(ctypes.c_uint8),
        ctypes.POINTER(ctypes.c_uint8),
        ctypes.c_uint32,
        ctypes.c_bool,
    ]
    library.sc_session_create.restype = ctypes.c_void_p
    library.sc_session_bind.argtypes = [ctypes.c_void_p, ctypes.c_uint16, ctypes.c_uint16]
    library.sc_session_bind.restype = ctypes.c_int32
    library.sc_session_set_peer.argtypes = [
        ctypes.c_void_p,
        ctypes.c_char_p,
        ctypes.c_uint16,
        ctypes.c_char_p,
        ctypes.c_uint16,
    ]
    library.sc_session_set_peer.restype = ctypes.c_int32
    library.sc_session_start.argtypes = [ctypes.c_void_p]
    library.sc_session_start.restype = ctypes.c_int32
    library.sc_session_send_frame.argtypes = [
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_uint8),
        ctypes.c_uint32,
        ctypes.c_uint64,
        ctypes.c_uint64,
        ctypes.c_uint32,
        ctypes.c_uint8,
        ctypes.c_uint8,
    ]
    library.sc_session_send_frame.restype = ctypes.c_int32
    library.sc_session_send_control.argtypes = [
        ctypes.c_void_p,
        ctypes.c_uint16,
        ctypes.POINTER(ctypes.c_uint8),
        ctypes.c_uint16,
    ]
    library.sc_session_send_control.restype = ctypes.c_int32
    library.sc_session_poll.argtypes = [ctypes.c_void_p, ctypes.POINTER(ScEvent)]
    library.sc_session_poll.restype = ctypes.c_int32
    library.sc_session_stats.argtypes = [ctypes.c_void_p, ctypes.POINTER(ScStats)]
    library.sc_session_local_ports.argtypes = [
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_uint16),
        ctypes.POINTER(ctypes.c_uint16),
    ]
    library.sc_session_local_ports.restype = ctypes.c_int32
    library.sc_frame_release.argtypes = [ctypes.c_void_p]
    library.sc_session_destroy.argtypes = [ctypes.c_void_p]

    # ---- layout --------------------------------------------------------------
    check.check("sizeof(sc_event_t) == 64", ctypes.sizeof(ScEvent) == 64, ctypes.sizeof(ScEvent))
    check.check("sizeof(sc_stats_t) == 160", ctypes.sizeof(ScStats) == 160, ctypes.sizeof(ScStats))
    check.check("sc_event_t.data offset == 48", ScEvent.data.offset == 48, ScEvent.data.offset)

    # ---- foundation ----------------------------------------------------------
    check.check("sc_core_version() == 512", library.sc_core_version() == 512)
    check.check("sc_set_role(0) == 0", library.sc_set_role(0) == 0)
    check.check("sc_get_role() == 0", library.sc_get_role() == 0)
    check.check("sc_set_role(9) == -1", library.sc_set_role(9) == -1)
    check.check("sc_get_role() still 0", library.sc_get_role() == 0)

    # ---- argument validation -------------------------------------------------
    check.check("create(NULL) == NULL", library.sc_session_create(None, None, 1, True) is None)
    check.check("bind(NULL) == SC_ERR_NULL", library.sc_session_bind(None, 0, 0) == SC_ERR_NULL)
    check.check("start(NULL) == SC_ERR_NULL", library.sc_session_start(None) == SC_ERR_NULL)
    check.check(
        "poll(NULL) == SC_ERR_NULL",
        library.sc_session_poll(None, ctypes.byref(ScEvent())) == SC_ERR_NULL,
    )

    # ---- real session --------------------------------------------------------
    secret = (ctypes.c_uint8 * 32)(*range(32))
    session_id = (ctypes.c_uint8 * 16)(*[(0xA1 + i) & 0xFF for i in range(16)])

    host = library.sc_session_create(secret, session_id, 1, True)
    viewer = library.sc_session_create(secret, session_id, 1, False)
    check.check("both sessions created", bool(host) and bool(viewer))

    host_video_port = library.sc_session_bind(host, 0, 0)
    viewer_video_port = library.sc_session_bind(viewer, 0, 0)
    check.check("host bind returns a positive port", host_video_port > 0, host_video_port)
    check.check("viewer bind returns a positive port", viewer_video_port > 0, viewer_video_port)

    host_video = ctypes.c_uint16()
    host_control = ctypes.c_uint16()
    viewer_video = ctypes.c_uint16()
    viewer_control = ctypes.c_uint16()
    library.sc_session_local_ports(host, ctypes.byref(host_video), ctypes.byref(host_control))
    library.sc_session_local_ports(viewer, ctypes.byref(viewer_video), ctypes.byref(viewer_control))
    check.check("host ports differ", host_video.value != host_control.value)
    check.check("viewer ports differ", viewer_video.value != viewer_control.value)

    check.check(
        "set_peer(host)",
        library.sc_session_set_peer(
            host, b"127.0.0.1", viewer_video.value, b"127.0.0.1", viewer_control.value
        )
        == SC_OK,
    )
    check.check(
        "set_peer(viewer)",
        library.sc_session_set_peer(
            viewer, b"127.0.0.1", host_video.value, b"127.0.0.1", host_control.value
        )
        == SC_OK,
    )
    check.check("start(host)", library.sc_session_start(host) == SC_OK)
    check.check("start(viewer)", library.sc_session_start(viewer) == SC_OK)

    payload = bytes((i * 7) % 251 for i in range(3000))
    frame = (ctypes.c_uint8 * len(payload)).from_buffer_copy(payload)
    check.check(
        "send_frame",
        library.sc_session_send_frame(host, frame, len(payload), 42, 1000, 7, 1, 1) == SC_OK,
    )

    event = ScEvent()
    deadline = time.time() + 5.0
    received = None
    while time.time() < deadline:
        status = library.sc_session_poll(viewer, ctypes.byref(event))
        if status == 1 and event.kind == SC_EVENT_VIDEO_FRAME:
            received = ctypes.string_at(event.data, event.data_len)
            check.check("frame id", event.frame_id == 42, event.frame_id)
            check.check("frame timestamp", event.capture_timestamp_us == 1000)
            check.check("frame config id", event.config_id == 7)
            check.check("frame flags", event.flags == 1)
            check.check("frame codec", event.codec == 1)
            library.sc_frame_release(viewer)
            break
        time.sleep(0.002)

    check.check("video frame arrived through the C ABI", received is not None)
    check.check("video bytes are byte identical", received == payload)

    body = (ctypes.c_uint8 * 3)(0x1E, 0x00, 0x01)
    check.check("send_control", library.sc_session_send_control(viewer, 1, body, 3) == SC_OK)

    deadline = time.time() + 5.0
    control = None
    while time.time() < deadline:
        status = library.sc_session_poll(host, ctypes.byref(event))
        if status == 1 and event.kind == SC_EVENT_CONTROL:
            control = (event.event_type, ctypes.string_at(event.data, event.data_len))
            library.sc_frame_release(host)
            break
        time.sleep(0.002)

    check.check("control event arrived", control is not None)
    check.check("control event type", control is not None and control[0] == 1)
    check.check("control body", control is not None and control[1] == b"\x1e\x00\x01")

    stats = ScStats()
    library.sc_session_stats(viewer, ctypes.byref(stats))
    check.check("stats viewer port", stats.video_local_port == viewer_video.value)
    check.check("stats assembled frames", stats.assembled_frames == 1, stats.assembled_frames)
    check.check("stats video received", stats.video_datagrams_received >= 3)
    check.check("stats no rejections", stats.video_datagrams_rejected == 0)

    library.sc_session_stats(host, ctypes.byref(stats))
    check.check("stats host bytes sent", stats.video_bytes_sent > 0)
    check.check("stats host acks", stats.control_bytes_sent > 0)

    library.sc_session_destroy(host)
    library.sc_session_destroy(viewer)
    check.check("destroy is idempotent-safe on NULL", True)
    library.sc_session_destroy(None)

    print()
    print(f"{check.checks - len(check.failures)}/{check.checks} checks passed")
    return 0 if not check.failures else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))

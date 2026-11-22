/*
 * Compile-time verification that `screencast_core.h` is valid C and C++ and that the
 * layout the platform layers see matches the Rust `#[repr(C)]` definitions.
 *
 * This file intentionally has no main(): it is compiled with -c only, so it never needs
 * the shared library. That makes it usable on any machine with a C compiler, including one
 * where the toolchain cannot link (a local MinGW install under a non-ASCII path breaks its
 * own linker driver but not its compiler).
 *
 *   gcc -std=c11  -Wall -Wextra -Werror -c -I include tools/header_smoke.c -o header_smoke.o
 *   g++ -std=c++17 -Wall -Wextra -Werror -x c++ -c -I include tools/header_smoke.c -o header_smoke.o
 */

#include "screencast_core.h"

#include <stddef.h>

#if defined(__cplusplus)
#define SC_STATIC_ASSERT(condition, message) static_assert(condition, message)
#else
#define SC_STATIC_ASSERT(condition, message) _Static_assert(condition, message)
#endif

/* Status codes are part of the ABI. */
SC_STATIC_ASSERT(SC_OK == 0, "SC_OK");
SC_STATIC_ASSERT(SC_ERR_NULL == -1, "SC_ERR_NULL");
SC_STATIC_ASSERT(SC_ERR_INVALID_ARGUMENT == -2, "SC_ERR_INVALID_ARGUMENT");
SC_STATIC_ASSERT(SC_ERR_NOT_BOUND == -3, "SC_ERR_NOT_BOUND");
SC_STATIC_ASSERT(SC_ERR_ALREADY_BOUND == -4, "SC_ERR_ALREADY_BOUND");
SC_STATIC_ASSERT(SC_ERR_IO == -5, "SC_ERR_IO");
SC_STATIC_ASSERT(SC_ERR_STATE == -6, "SC_ERR_STATE");
SC_STATIC_ASSERT(SC_ERR_TOO_LARGE == -7, "SC_ERR_TOO_LARGE");
SC_STATIC_ASSERT(SC_ERR_PROTOCOL == -8, "SC_ERR_PROTOCOL");

/* Event kinds and path states. */
SC_STATIC_ASSERT(SC_EVENT_NONE == 0, "SC_EVENT_NONE");
SC_STATIC_ASSERT(SC_EVENT_VIDEO_FRAME == 1, "SC_EVENT_VIDEO_FRAME");
SC_STATIC_ASSERT(SC_EVENT_CONTROL == 2, "SC_EVENT_CONTROL");
SC_STATIC_ASSERT(SC_EVENT_PATH == 3, "SC_EVENT_PATH");
SC_STATIC_ASSERT(SC_EVENT_CONTROL_EXPIRED == 4, "SC_EVENT_CONTROL_EXPIRED");
SC_STATIC_ASSERT(SC_PATH_PROBING == 0, "SC_PATH_PROBING");
SC_STATIC_ASSERT(SC_PATH_ALIVE == 1, "SC_PATH_ALIVE");
SC_STATIC_ASSERT(SC_PATH_DOWN == 2, "SC_PATH_DOWN");

/* sc_event_t: 64 bytes on 64-bit targets, payload pointer at offset 48. */
SC_STATIC_ASSERT(offsetof(sc_event_t, kind) == 0, "event.kind");
SC_STATIC_ASSERT(offsetof(sc_event_t, status) == 4, "event.status");
SC_STATIC_ASSERT(offsetof(sc_event_t, component) == 8, "event.component");
SC_STATIC_ASSERT(offsetof(sc_event_t, event_type) == 12, "event.event_type");
SC_STATIC_ASSERT(offsetof(sc_event_t, flags) == 14, "event.flags");
SC_STATIC_ASSERT(offsetof(sc_event_t, codec) == 15, "event.codec");
SC_STATIC_ASSERT(offsetof(sc_event_t, frame_id) == 16, "event.frame_id");
SC_STATIC_ASSERT(offsetof(sc_event_t, capture_timestamp_us) == 24, "event.timestamp");
SC_STATIC_ASSERT(offsetof(sc_event_t, rtt_us) == 32, "event.rtt_us");
SC_STATIC_ASSERT(offsetof(sc_event_t, config_id) == 40, "event.config_id");
SC_STATIC_ASSERT(offsetof(sc_event_t, data_len) == 44, "event.data_len");
SC_STATIC_ASSERT(offsetof(sc_event_t, data) == 48, "event.data");
SC_STATIC_ASSERT(offsetof(sc_event_t, reserved) == 56, "event.reserved");

/* sc_stats_t: 15 u64 counters, then two doubles, then four u32 fields. */
SC_STATIC_ASSERT(offsetof(sc_stats_t, video_datagrams_received) == 0, "stats.first");
SC_STATIC_ASSERT(offsetof(sc_stats_t, partial_frames) == 14 * 8, "stats.partial_frames");
SC_STATIC_ASSERT(offsetof(sc_stats_t, video_rtt_us) == 15 * 8, "stats.video_rtt_us");
SC_STATIC_ASSERT(offsetof(sc_stats_t, control_rtt_us) == 15 * 8 + 8, "stats.control_rtt_us");
SC_STATIC_ASSERT(offsetof(sc_stats_t, video_path_status) == 15 * 8 + 16, "stats.video_path_status");
SC_STATIC_ASSERT(offsetof(sc_stats_t, reserved) == 15 * 8 + 32, "stats.reserved");

#if UINTPTR_MAX == 0xFFFFFFFFFFFFFFFFu
SC_STATIC_ASSERT(sizeof(sc_event_t) == 64, "sizeof(sc_event_t)");
SC_STATIC_ASSERT(sizeof(sc_stats_t) == 160, "sizeof(sc_stats_t)");
#endif

/* The public surface must be callable with the documented argument types. */
void header_smoke_usage(void) {
    uint8_t secret[32] = {0};
    uint8_t session_id[16] = {0};
    uint16_t video_port = 0;
    uint16_t control_port = 0;
    sc_event_t event;
    sc_stats_t stats;
    sc_session_t *session;

    session = sc_session_create(secret, session_id, 1, true);
    if (session == NULL) {
        return;
    }

    (void)sc_session_bind(session, 0, 0);
    (void)sc_session_set_peer(session, "203.0.113.7", 41000, "203.0.113.7", 41001);
    (void)sc_session_start(session);
    (void)sc_session_send_frame(session, secret, 3, 1, 2, 3, 1, 1);
    (void)sc_session_send_control(session, 1, secret, 2);
    (void)sc_session_poll(session, &event);
    sc_session_stats(session, &stats);
    (void)sc_session_local_ports(session, &video_port, &control_port);
    sc_frame_release(session);
    sc_session_destroy(session);
}

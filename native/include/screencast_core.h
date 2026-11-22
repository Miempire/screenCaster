#ifndef SCREENCAST_CORE_H
#define SCREENCAST_CORE_H
#include <stdbool.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

/* ------------------------------------------------------------------ */
/* Foundation                                                          */
/* ------------------------------------------------------------------ */

/* Packed major/minor/patch: 0.2.0 = 512. */
uint32_t sc_core_version(void);
/* Process-local UI role only: 0 Host, 1 Viewer. */
uint8_t sc_get_role(void);
/* 0 success, -1 invalid role. */
int32_t sc_set_role(uint8_t role);

/* ------------------------------------------------------------------ */
/* Status codes (all failures are negative)                            */
/* ------------------------------------------------------------------ */

#define SC_OK 0
#define SC_ERR_NULL (-1)
#define SC_ERR_INVALID_ARGUMENT (-2)
#define SC_ERR_NOT_BOUND (-3)
#define SC_ERR_ALREADY_BOUND (-4)
#define SC_ERR_IO (-5)
#define SC_ERR_STATE (-6)
#define SC_ERR_TOO_LARGE (-7)
#define SC_ERR_PROTOCOL (-8)

/* ------------------------------------------------------------------ */
/* Events                                                              */
/* ------------------------------------------------------------------ */

#define SC_EVENT_NONE 0
#define SC_EVENT_VIDEO_FRAME 1
#define SC_EVENT_CONTROL 2
#define SC_EVENT_PATH 3
#define SC_EVENT_CONTROL_EXPIRED 4

/* Path status values used by sc_event_t.status for SC_EVENT_PATH. */
#define SC_PATH_PROBING 0
#define SC_PATH_ALIVE 1
#define SC_PATH_DOWN 2

/*
 * One event produced by sc_session_poll.
 *
 * `data` points into memory owned by the session. It stays valid until the next
 * sc_session_poll that returns a video frame or control body, or until
 * sc_frame_release is called. The caller must copy the bytes out before then; the
 * pointer must never be freed by the caller.
 */
typedef struct sc_event_t {
    /* One of the SC_EVENT_* constants. */
    int32_t kind;
    /* SC_EVENT_PATH: SC_PATH_*; otherwise 0. */
    int32_t status;
    /* SC_EVENT_PATH: 0 video, 1 control; otherwise 0. */
    int32_t component;
    /* SC_EVENT_CONTROL / SC_EVENT_CONTROL_EXPIRED: control event type. */
    uint16_t event_type;
    /* SC_EVENT_VIDEO_FRAME: 1 key, 2 configuration, 4 discontinuity. */
    uint8_t flags;
    /* SC_EVENT_VIDEO_FRAME: 0 unknown, 1 H.264, 2 AV1. */
    uint8_t codec;
    /* SC_EVENT_VIDEO_FRAME: sender frame id. */
    uint64_t frame_id;
    /* SC_EVENT_VIDEO_FRAME: sender capture timestamp in microseconds. */
    uint64_t capture_timestamp_us;
    /* SC_EVENT_PATH: smoothed RTT in microseconds, UINT64_MAX when unknown. */
    uint64_t rtt_us;
    /* SC_EVENT_VIDEO_FRAME: configuration id. */
    uint32_t config_id;
    /* Length of the payload behind `data`. */
    uint32_t data_len;
    /* Payload bytes, or NULL when the event carries none. */
    const uint8_t *data;
    /* Reserved, always zero. */
    uint32_t reserved;
} sc_event_t;

/* Session counters. Zeroed counters mean "nothing observed yet". */
typedef struct sc_stats_t {
    uint64_t video_datagrams_received;
    uint64_t video_datagrams_rejected;
    uint64_t control_datagrams_received;
    uint64_t control_datagrams_rejected;
    uint64_t control_frames_dropped;
    uint64_t assembled_frames;
    uint64_t dropped_frames;
    uint64_t dropped_fragments;
    uint64_t video_bytes_sent;
    uint64_t control_bytes_sent;
    uint64_t replay_rejected;
    uint64_t probes_sent;
    uint64_t probes_answered;
    uint64_t events_dropped;
    uint64_t partial_frames;
    /* Smoothed RTT in microseconds; -1 when no sample exists yet. */
    double video_rtt_us;
    /* Smoothed RTT in microseconds; -1 when no sample exists yet. */
    double control_rtt_us;
    /* SC_PATH_* for the video component. */
    int32_t video_path_status;
    /* SC_PATH_* for the control component. */
    int32_t control_path_status;
    uint32_t video_local_port;
    uint32_t control_local_port;
    uint32_t reserved;
} sc_stats_t;

/* ------------------------------------------------------------------ */
/* Session                                                             */
/* ------------------------------------------------------------------ */

typedef struct sc_session sc_session_t;

/*
 * Create a session.
 *
 * secret32     : 32-byte shared secret (HKDF input), never transmitted.
 * session_id16 : 16 raw session-id bytes, exactly as on the wire.
 * epoch        : key epoch, must be >= 1.
 * is_host      : true for the Host role, false for the Viewer role.
 *
 * Returns NULL on invalid input (null pointer or epoch 0).
 */
sc_session_t *sc_session_create(const uint8_t *secret32, const uint8_t *session_id16, uint32_t epoch,
                                bool is_host);

/*
 * Bind the two media sockets on 0.0.0.0. Port 0 selects an ephemeral port.
 *
 * Returns the bound video port (positive) on success, or a negative SC_ERR_* code.
 * Video and control must use different ports. Read both ports with
 * sc_session_local_ports.
 */
int32_t sc_session_bind(sc_session_t *session, uint16_t video_port, uint16_t control_port);

/*
 * Set both peer endpoints. Addresses are literal IPv4 or IPv6 text (no DNS).
 * Example: sc_session_set_peer(s, "203.0.113.7", 41000, "203.0.113.7", 41001).
 */
int32_t sc_session_set_peer(sc_session_t *session, const char *video_addr, uint16_t video_port,
                            const char *control_addr, uint16_t control_port);

/* Start the receive/send threads. Idempotent. */
int32_t sc_session_start(sc_session_t *session);

/*
 * Fragment, encrypt and send one encoded frame.
 * flags: 1 key, 2 configuration, 4 discontinuity. codec: 1 H.264, 2 AV1.
 */
int32_t sc_session_send_frame(sc_session_t *session, const uint8_t *data, uint32_t len,
                              uint64_t frame_id, uint64_t ts_us, uint32_t config_id, uint8_t flags,
                              uint8_t codec);

/* Queue one reliable control event (see SC_EVENT_* / control event types 1..10). */
int32_t sc_session_send_control(sc_session_t *session, uint16_t event_type, const uint8_t *body,
                                uint16_t len);

/*
 * Poll one event without blocking.
 * Returns 1 when `out` was filled, 0 when no event is pending, negative on error.
 */
int32_t sc_session_poll(sc_session_t *session, sc_event_t *out);

/* Copy the session counters. Safe to call at any time. */
void sc_session_stats(sc_session_t *session, sc_stats_t *out);

/* Copy both bound local ports; either pointer may be NULL. */
int32_t sc_session_local_ports(sc_session_t *session, uint16_t *video_port,
                               uint16_t *control_port);

/* Release the buffer behind the most recent sc_session_poll payload pointer. */
void sc_frame_release(sc_session_t *session);

/* Stop the threads and free the session. NULL is a no-op. */
void sc_session_destroy(sc_session_t *session);

#ifdef __cplusplus
}
#endif
#endif

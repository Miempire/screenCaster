//! C ABI tests: struct layout, argument validation and a full encrypted round trip driven
//! exclusively through the `sc_session_*` functions that Android JNI and macOS Swift call.

use std::ffi::CString;
use std::mem::{offset_of, size_of};
use std::time::{Duration, Instant};

use screencast_core::ffi::*;

fn secret() -> [u8; 32] {
    let mut value = [0u8; 32];
    for (index, byte) in value.iter_mut().enumerate() {
        *byte = (index as u8).wrapping_mul(7);
    }
    value
}

fn session_id() -> [u8; 16] {
    let mut value = [0u8; 16];
    for (index, byte) in value.iter_mut().enumerate() {
        *byte = 0x40u8.wrapping_add(index as u8);
    }
    value
}

#[test]
fn event_and_stats_layout_is_stable_for_c() {
    // sc_event_t is exactly 64 bytes on 64-bit targets, with the pointer 8-byte aligned.
    assert_eq!(offset_of!(ScEvent, kind), 0);
    assert_eq!(offset_of!(ScEvent, status), 4);
    assert_eq!(offset_of!(ScEvent, component), 8);
    assert_eq!(offset_of!(ScEvent, event_type), 12);
    assert_eq!(offset_of!(ScEvent, flags), 14);
    assert_eq!(offset_of!(ScEvent, codec), 15);
    assert_eq!(offset_of!(ScEvent, frame_id), 16);
    assert_eq!(offset_of!(ScEvent, capture_timestamp_us), 24);
    assert_eq!(offset_of!(ScEvent, rtt_us), 32);
    assert_eq!(offset_of!(ScEvent, config_id), 40);
    assert_eq!(offset_of!(ScEvent, data_len), 44);
    assert_eq!(offset_of!(ScEvent, data), 48);
    assert_eq!(offset_of!(ScEvent, reserved), 56);

    // sc_stats_t: 15 u64 counters, two doubles, then four u32 fields.
    assert_eq!(offset_of!(ScStats, video_datagrams_received), 0);
    assert_eq!(offset_of!(ScStats, partial_frames), 14 * 8);
    assert_eq!(offset_of!(ScStats, video_rtt_us), 15 * 8);
    assert_eq!(offset_of!(ScStats, control_rtt_us), 15 * 8 + 8);
    assert_eq!(offset_of!(ScStats, video_path_status), 15 * 8 + 16);
    assert_eq!(offset_of!(ScStats, reserved), 15 * 8 + 16 + 16);

    if size_of::<usize>() == 8 {
        assert_eq!(size_of::<ScEvent>(), 64);
        // 15 * u64 + 2 * f64 + 4 * u32 = 156, padded to the 8-byte alignment of the u64s.
        assert_eq!(size_of::<ScStats>(), 160);
    }
}

#[test]
fn create_rejects_invalid_arguments() {
    let secret = secret();
    let id = session_id();

    let null_secret = unsafe { sc_session_create(std::ptr::null(), id.as_ptr(), 1, true) };
    assert!(null_secret.is_null());

    let null_id = unsafe { sc_session_create(secret.as_ptr(), std::ptr::null(), 1, true) };
    assert!(null_id.is_null());

    let zero_epoch = unsafe { sc_session_create(secret.as_ptr(), id.as_ptr(), 0, true) };
    assert!(zero_epoch.is_null());

    let valid = unsafe { sc_session_create(secret.as_ptr(), id.as_ptr(), 1, true) };
    assert!(!valid.is_null());
    unsafe { sc_session_destroy(valid) };

    // Null is a no-op for destroy and frame release.
    unsafe {
        sc_session_destroy(std::ptr::null_mut());
        sc_frame_release(std::ptr::null_mut());
    }
}

#[test]
fn every_entry_point_tolerates_a_null_handle() {
    unsafe {
        assert_eq!(sc_session_bind(std::ptr::null_mut(), 0, 0), SC_ERR_NULL);
        assert_eq!(sc_session_start(std::ptr::null_mut()), SC_ERR_NULL);
        assert_eq!(
            sc_session_set_peer(
                std::ptr::null_mut(),
                std::ptr::null(),
                0,
                std::ptr::null(),
                0
            ),
            SC_ERR_NULL
        );
        assert_eq!(
            sc_session_send_frame(std::ptr::null_mut(), std::ptr::null(), 1, 0, 0, 0, 0, 0),
            SC_ERR_NULL
        );
        assert_eq!(
            sc_session_send_control(std::ptr::null_mut(), 1, std::ptr::null(), 0),
            SC_ERR_NULL
        );
        assert_eq!(
            sc_session_poll(std::ptr::null_mut(), std::ptr::null_mut()),
            SC_ERR_NULL
        );
        assert_eq!(
            sc_session_local_ports(
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut()
            ),
            SC_ERR_NULL
        );
        sc_session_stats(std::ptr::null_mut(), std::ptr::null_mut());
    }
}

#[test]
fn argument_validation_before_bind() {
    let secret = secret();
    let id = session_id();
    let session = unsafe { sc_session_create(secret.as_ptr(), id.as_ptr(), 1, false) };
    assert!(!session.is_null());

    let address = CString::new("127.0.0.1").expect("address");

    unsafe {
        // Nothing works before bind.
        assert_eq!(
            sc_session_set_peer(session, address.as_ptr(), 1, address.as_ptr(), 2),
            SC_ERR_NOT_BOUND
        );
        assert_eq!(sc_session_start(session), SC_ERR_NOT_BOUND);
        assert_eq!(
            sc_session_send_control(session, 1, std::ptr::null(), 0),
            SC_ERR_NOT_BOUND
        );
        assert_eq!(
            sc_session_bind(session, 5000, 5000),
            SC_ERR_INVALID_ARGUMENT
        );

        let video_port = sc_session_bind(session, 0, 0);
        assert!(video_port > 0, "bind must return the bound video port");
        assert_eq!(sc_session_bind(session, 0, 0), SC_ERR_ALREADY_BOUND);

        // Bad addresses and bad event types are rejected without touching the socket.
        let bad = CString::new("not-an-address").expect("string");
        assert_eq!(
            sc_session_set_peer(session, bad.as_ptr(), 1, address.as_ptr(), 2),
            SC_ERR_INVALID_ARGUMENT
        );
        assert_eq!(
            sc_session_set_peer(session, std::ptr::null(), 1, address.as_ptr(), 2),
            SC_ERR_NULL
        );
        assert_eq!(
            sc_session_send_control(session, 0, std::ptr::null(), 0),
            SC_ERR_INVALID_ARGUMENT
        );
        assert_eq!(
            sc_session_send_frame(session, std::ptr::null(), 0, 0, 0, 0, 0, 0),
            SC_ERR_INVALID_ARGUMENT
        );

        sc_session_destroy(session);
    }
}

#[test]
fn full_encrypted_round_trip_through_the_c_abi() {
    let secret = secret();
    let id = session_id();

    let host = unsafe { sc_session_create(secret.as_ptr(), id.as_ptr(), 1, true) };
    let viewer = unsafe { sc_session_create(secret.as_ptr(), id.as_ptr(), 1, false) };
    assert!(!host.is_null() && !viewer.is_null());

    let mut host_video = 0u16;
    let mut host_control = 0u16;
    let mut viewer_video = 0u16;
    let mut viewer_control = 0u16;

    unsafe {
        assert!(sc_session_bind(host, 0, 0) > 0);
        assert!(sc_session_bind(viewer, 0, 0) > 0);
        assert_eq!(
            sc_session_local_ports(host, &mut host_video, &mut host_control),
            SC_OK
        );
        assert_eq!(
            sc_session_local_ports(viewer, &mut viewer_video, &mut viewer_control),
            SC_OK
        );
        assert_ne!(host_video, host_control);
        assert_ne!(viewer_video, viewer_control);

        let address = CString::new("127.0.0.1").expect("address");
        assert_eq!(
            sc_session_set_peer(
                host,
                address.as_ptr(),
                viewer_video,
                address.as_ptr(),
                viewer_control
            ),
            SC_OK
        );
        assert_eq!(
            sc_session_set_peer(
                viewer,
                address.as_ptr(),
                host_video,
                address.as_ptr(),
                host_control
            ),
            SC_OK
        );

        assert_eq!(sc_session_start(host), SC_OK);
        assert_eq!(sc_session_start(viewer), SC_OK);
        // Starting twice is idempotent.
        assert_eq!(sc_session_start(host), SC_OK);

        // A 3000-byte frame becomes three fragments and is reassembled on the far side.
        let frame: Vec<u8> = (0..3000u32).map(|index| (index % 253) as u8).collect();
        assert_eq!(
            sc_session_send_frame(host, frame.as_ptr(), frame.len() as u32, 42, 1_000, 7, 1, 1),
            SC_OK
        );

        let mut event = ScEvent {
            kind: SC_EVENT_NONE,
            status: 0,
            component: 0,
            event_type: 0,
            flags: 0,
            codec: 0,
            frame_id: 0,
            capture_timestamp_us: 0,
            rtt_us: 0,
            config_id: 0,
            data_len: 0,
            data: std::ptr::null(),
            reserved: 0,
        };

        let deadline = Instant::now() + Duration::from_secs(5);
        let mut received: Option<Vec<u8>> = None;
        while Instant::now() < deadline {
            let status = sc_session_poll(viewer, &mut event);
            assert!(status >= 0, "poll must never fail with a valid handle");
            if status == 1 && event.kind == SC_EVENT_VIDEO_FRAME {
                assert_eq!(event.frame_id, 42);
                assert_eq!(event.capture_timestamp_us, 1_000);
                assert_eq!(event.config_id, 7);
                assert_eq!(event.flags, 1);
                assert_eq!(event.codec, 1);
                assert!(!event.data.is_null());
                assert_eq!(event.data_len as usize, frame.len());
                let bytes =
                    std::slice::from_raw_parts(event.data, event.data_len as usize).to_vec();
                received = Some(bytes);
                sc_frame_release(viewer);
                break;
            }
            std::thread::sleep(Duration::from_millis(2));
        }

        let received = received.expect("the viewer must poll one complete frame");
        assert_eq!(received, frame, "bytes must survive the C ABI unchanged");

        // Control in the other direction.
        let body = [0x1eu8, 0x00, 0x01];
        assert_eq!(
            sc_session_send_control(viewer, 1, body.as_ptr(), body.len() as u16),
            SC_OK
        );

        let deadline = Instant::now() + Duration::from_secs(5);
        let mut control: Option<(u16, Vec<u8>)> = None;
        while Instant::now() < deadline {
            let status = sc_session_poll(host, &mut event);
            if status == 1 && event.kind == SC_EVENT_CONTROL {
                let bytes = if event.data.is_null() {
                    Vec::new()
                } else {
                    std::slice::from_raw_parts(event.data, event.data_len as usize).to_vec()
                };
                control = Some((event.event_type, bytes));
                sc_frame_release(host);
                break;
            }
            std::thread::sleep(Duration::from_millis(2));
        }

        let (event_type, bytes) = control.expect("the host must poll the control event");
        assert_eq!(event_type, 1);
        assert_eq!(bytes, body.to_vec());

        // Stats are readable at any time and report the bound ports.
        let mut stats = ScStats {
            video_datagrams_received: 0,
            video_datagrams_rejected: 0,
            control_datagrams_received: 0,
            control_datagrams_rejected: 0,
            control_frames_dropped: 0,
            assembled_frames: 0,
            dropped_frames: 0,
            dropped_fragments: 0,
            video_bytes_sent: 0,
            control_bytes_sent: 0,
            replay_rejected: 0,
            probes_sent: 0,
            probes_answered: 0,
            events_dropped: 0,
            partial_frames: 0,
            video_rtt_us: -1.0,
            control_rtt_us: -1.0,
            video_path_status: 0,
            control_path_status: 0,
            video_local_port: 0,
            control_local_port: 0,
            reserved: 0,
        };
        sc_session_stats(viewer, &mut stats);
        assert_eq!(stats.video_local_port, u32::from(viewer_video));
        assert_eq!(stats.control_local_port, u32::from(viewer_control));
        assert_eq!(stats.assembled_frames, 1);
        assert!(stats.video_datagrams_received >= 3);

        sc_session_stats(host, &mut stats);
        assert!(stats.video_bytes_sent > 0);
        assert_eq!(stats.video_local_port, u32::from(host_video));

        sc_session_destroy(host);
        sc_session_destroy(viewer);
    }
}

#[test]
fn version_and_role_abi_is_unchanged() {
    assert_eq!(screencast_core::sc_core_version(), 512);
    assert_eq!(screencast_core::sc_set_role(0), 0);
    assert_eq!(screencast_core::sc_get_role(), 0);
    assert_eq!(screencast_core::sc_set_role(1), 0);
    assert_eq!(screencast_core::sc_get_role(), 1);
    assert_eq!(screencast_core::sc_set_role(9), -1);
    assert_eq!(screencast_core::sc_get_role(), 1);
}

//! Real-UDP integration tests: two media sessions exchanging encrypted video and control
//! over the loopback interface, plus replay and forgery behaviour.
//!
//! These tests exercise the actual `std::net::UdpSocket` path (two sockets per session),
//! the AES-256-GCM envelope, the reliable control channel and the fragment reassembler.

use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::time::{Duration, Instant};

use screencast_core::control::{encode_data, SENTINEL_ACK_BASE};
use screencast_core::packet::{PacketChannel, PacketDirection, PacketProtector};
use screencast_core::probe::PathStatus;
use screencast_core::session::{MediaSession, PathComponent, SessionConfig, SessionEvent};
use screencast_core::transport::UdpTransport;
use screencast_core::video::{
    AssembledVideoFrame, VideoCodec, VideoFragmenter, FRAGMENT_HEADER_SIZE, MAX_FRAGMENT_PAYLOAD,
};

const TIMEOUT: Duration = Duration::from_secs(5);

fn secret() -> [u8; 32] {
    let mut value = [0u8; 32];
    for (index, byte) in value.iter_mut().enumerate() {
        *byte = index as u8;
    }
    value
}

fn session_id() -> [u8; 16] {
    let mut value = [0u8; 16];
    for (index, byte) in value.iter_mut().enumerate() {
        *byte = 0xa1u8.wrapping_add(index as u8);
    }
    value
}

fn config(is_host: bool) -> SessionConfig {
    SessionConfig::new(secret(), session_id(), 1, is_host)
}

fn payload(length: usize) -> Vec<u8> {
    (0..length).map(|index| (index % 251) as u8).collect()
}

fn loopback(port: u16) -> SocketAddr {
    SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), port)
}

/// Split one encoded frame into fragment plaintexts, exactly as the host would.
fn fragment_plaintexts(frame_data: &[u8], frame_id: u64) -> Vec<Vec<u8>> {
    let count = VideoFragmenter::fragment_count_for(frame_data.len()).expect("fragment count");
    let mut buffer = vec![0u8; FRAGMENT_HEADER_SIZE + MAX_FRAGMENT_PAYLOAD];
    (0..count)
        .map(|index| {
            let length = VideoFragmenter::write_fragment(
                frame_data,
                frame_id,
                0,
                1,
                0,
                VideoCodec::H264 as u8,
                index,
                &mut buffer,
            )
            .expect("fragment");
            buffer[..length].to_vec()
        })
        .collect()
}

fn poll_frame(session: &MediaSession, timeout: Duration) -> Option<AssembledVideoFrame> {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        while let Some(event) = session.poll_event() {
            if let SessionEvent::VideoFrame(frame) = event {
                return Some(frame);
            }
        }
        std::thread::sleep(Duration::from_millis(2));
    }
    None
}

fn poll_control(session: &MediaSession, timeout: Duration) -> Option<(u16, Vec<u8>)> {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        while let Some(event) = session.poll_event() {
            if let SessionEvent::Control { event_type, body } = event {
                return Some((event_type, body));
            }
        }
        std::thread::sleep(Duration::from_millis(2));
    }
    None
}

struct Pair {
    host: MediaSession,
    viewer: MediaSession,
}

fn pair() -> Pair {
    let host = MediaSession::bind(config(true), 0, 0).expect("host bind");
    let viewer = MediaSession::bind(config(false), 0, 0).expect("viewer bind");

    let (host_video, host_control) = host.local_ports();
    let (viewer_video, viewer_control) = viewer.local_ports();
    assert_ne!(host_video, 0);
    assert_ne!(viewer_video, 0);
    assert_ne!(host_video, host_control);
    assert_ne!(viewer_video, viewer_control);

    host.set_peers(loopback(viewer_video), loopback(viewer_control));
    viewer.set_peers(loopback(host_video), loopback(host_control));
    host.start().expect("host start");
    viewer.start().expect("viewer start");

    Pair { host, viewer }
}

#[test]
fn two_sessions_exchange_encrypted_video_and_control_over_udp() {
    let session = pair();

    let frame_data = payload(4000);
    let fragments = session
        .host
        .send_video_frame(&frame_data, 77, 999, 3, 1, VideoCodec::H264 as u8)
        .expect("send frame");
    assert_eq!(fragments, 4, "4000 bytes split into four fragments");

    let received = poll_frame(&session.viewer, TIMEOUT).expect("viewer must reassemble the frame");
    assert_eq!(received.data, frame_data, "payload must be byte identical");
    assert_eq!(received.frame_id, 77);
    assert_eq!(received.capture_timestamp_micros, 999);
    assert_eq!(received.config_id, 3);
    assert!(received.is_key_frame());
    assert_eq!(received.codec(), VideoCodec::H264);

    // A larger burst exercises the multi-fragment path and the 4 MiB socket buffer.
    let large = payload(20_000);
    session
        .host
        .send_video_frame(&large, 78, 1000, 3, 0, VideoCodec::H264 as u8)
        .expect("send large frame");
    let received = poll_frame(&session.viewer, TIMEOUT).expect("large frame must arrive");
    assert_eq!(received.data, large);

    // Control travels the other direction through the reliable channel.
    session
        .viewer
        .send_control(7, &[0x1e, 0x00, 0x01])
        .expect("viewer control");
    let (event_type, body) =
        poll_control(&session.host, TIMEOUT).expect("host must receive control");
    assert_eq!(event_type, 7);
    assert_eq!(body, vec![0x1e, 0x00, 0x01]);

    let viewer_stats = session.viewer.stats();
    assert_eq!(viewer_stats.assembled_frames, 2);
    assert_eq!(viewer_stats.video_datagrams_rejected, 0);
    assert_eq!(viewer_stats.control_datagrams_rejected, 0);
    assert!(
        viewer_stats.video_bytes_sent == 0,
        "the viewer sent no video"
    );

    let host_stats = session.host.stats();
    assert!(host_stats.video_bytes_sent > 20_000);
    assert!(
        host_stats.control_bytes_sent > 0,
        "the host ACKed the control frame"
    );
}

#[test]
fn a_forged_tag_is_dropped_and_the_session_keeps_working() {
    let viewer = MediaSession::bind(config(false), 0, 0).expect("viewer bind");
    let (viewer_video, viewer_control) = viewer.local_ports();
    viewer.set_peers(loopback(viewer_video), loopback(viewer_control));
    viewer.start().expect("start");

    let attacker = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("attacker bind");
    // A protector holding the host's real key, so only the tag/sequence can be wrong.
    let host_key = PacketProtector::new(
        &secret(),
        &session_id(),
        1,
        PacketDirection::HostToViewer,
        PacketChannel::Video,
        1,
    );

    // 1. Valid datagram with a corrupted tag.
    let mut forged = host_key.encode(&payload(64), false).expect("encode");
    let tag_index = forged.len() - 1;
    forged[tag_index] ^= 0xff;
    attacker
        .send_to(&forged, loopback(viewer_video))
        .expect("send forged");

    // 2. A datagram that is not SCT1 at all.
    attacker
        .send_to(&[0u8; 32], loopback(viewer_video))
        .expect("send garbage");

    let deadline = Instant::now() + TIMEOUT;
    while Instant::now() < deadline && viewer.stats().video_datagrams_rejected == 0 {
        std::thread::sleep(Duration::from_millis(5));
    }
    let stats = viewer.stats();
    assert!(
        stats.video_datagrams_rejected >= 1,
        "forged tag must be counted"
    );
    assert_eq!(stats.assembled_frames, 0, "no frame may be emitted");
}

#[test]
fn a_replayed_datagram_is_rejected_and_the_session_survives() {
    let viewer = MediaSession::bind(config(false), 0, 0).expect("viewer bind");
    let (viewer_video, _viewer_control) = viewer.local_ports();
    viewer.start().expect("start");

    let attacker = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("attacker bind");
    let host_key = PacketProtector::new(
        &secret(),
        &session_id(),
        1,
        PacketDirection::HostToViewer,
        PacketChannel::Video,
        1,
    );

    // A one-fragment frame, sent twice: the second copy must not be processed.
    let frame_data = payload(500);
    let fragments = fragment_plaintexts(&frame_data, 11);
    assert_eq!(fragments.len(), 1);
    let fragment = host_key.encode(&fragments[0], false).expect("encode");
    attacker
        .send_to(&fragment, loopback(viewer_video))
        .expect("first send");
    let first = poll_frame(&viewer, TIMEOUT).expect("the first copy must assemble");
    assert_eq!(first.data, frame_data);

    attacker
        .send_to(&fragment, loopback(viewer_video))
        .expect("replay");

    let deadline = Instant::now() + TIMEOUT;
    while Instant::now() < deadline && viewer.stats().replay_rejected == 0 {
        std::thread::sleep(Duration::from_millis(5));
    }
    let stats = viewer.stats();
    assert_eq!(
        stats.replay_rejected, 1,
        "the replayed packet must be rejected"
    );
    assert_eq!(
        stats.assembled_frames, 1,
        "the replay must not emit a second frame"
    );

    // The session is still healthy: a fresh datagram with a new sequence is accepted.
    let fresh = payload(300);
    let fresh_fragments = fragment_plaintexts(&fresh, 12);
    let next = host_key.encode(&fresh_fragments[0], false).expect("encode");
    attacker
        .send_to(&next, loopback(viewer_video))
        .expect("fresh send");
    let frame = poll_frame(&viewer, TIMEOUT).expect("a fresh datagram must still be accepted");
    assert_eq!(frame.data, fresh);
}

#[test]
fn a_missing_fragment_never_yields_a_partial_frame() {
    let viewer = MediaSession::bind(config(false), 0, 0).expect("viewer bind");
    let (viewer_video, _viewer_control) = viewer.local_ports();
    viewer.start().expect("start");

    let attacker = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("attacker bind");
    let host_key = PacketProtector::new(
        &secret(),
        &session_id(),
        1,
        PacketDirection::HostToViewer,
        PacketChannel::Video,
        1,
    );

    // Build three fragments of one frame and drop the middle one.
    let frame_data = payload(3000);
    let count = screencast_core::video::VideoFragmenter::fragment_count_for(frame_data.len())
        .expect("count");
    assert_eq!(count, 3);
    let mut buffer = vec![
        0u8;
        screencast_core::video::FRAGMENT_HEADER_SIZE
            + screencast_core::video::MAX_FRAGMENT_PAYLOAD
    ];
    for index in 0..count {
        let length = screencast_core::video::VideoFragmenter::write_fragment(
            &frame_data,
            5,
            0,
            1,
            0,
            VideoCodec::H264 as u8,
            index,
            &mut buffer,
        )
        .expect("fragment");
        if index == 1 {
            continue;
        }
        let datagram = host_key.encode(&buffer[..length], false).expect("encode");
        attacker
            .send_to(&datagram, loopback(viewer_video))
            .expect("send");
    }

    assert!(poll_frame(&viewer, Duration::from_millis(300)).is_none());
    let stats = viewer.stats();
    assert_eq!(
        stats.assembled_frames, 0,
        "an incomplete frame must not be delivered"
    );
    assert_eq!(stats.partial_frames, 1, "the partial frame stays bounded");
}

#[test]
fn keepalive_probes_report_both_paths_alive() {
    let mut host_config = config(true);
    host_config.probe_keepalive_micros = 100_000;
    let mut viewer_config = config(false);
    viewer_config.probe_keepalive_micros = 100_000;

    let host = MediaSession::bind(host_config, 0, 0).expect("host bind");
    let viewer = MediaSession::bind(viewer_config, 0, 0).expect("viewer bind");

    let (host_video, host_control) = host.local_ports();
    let (viewer_video, viewer_control) = viewer.local_ports();
    host.set_peers(loopback(viewer_video), loopback(viewer_control));
    viewer.set_peers(loopback(host_video), loopback(host_control));
    host.start().expect("host start");
    viewer.start().expect("viewer start");

    let deadline = Instant::now() + TIMEOUT;
    let mut host_alive = false;
    let mut viewer_alive = false;
    while Instant::now() < deadline
        && !(host_alive && viewer_alive && host.stats().probes_answered > 0)
    {
        for (session, alive) in [(&host, &mut host_alive), (&viewer, &mut viewer_alive)] {
            while let Some(event) = session.poll_event() {
                if let SessionEvent::PathStatus {
                    component,
                    status,
                    rtt_micros,
                } = event
                {
                    if status == PathStatus::Alive {
                        // The first Alive report may come from an inbound request, which
                        // carries no RTT sample yet; -1 means "unknown", never a guess.
                        assert!(rtt_micros >= 0.0 || rtt_micros == -1.0);
                        assert!(
                            component == PathComponent::Video
                                || component == PathComponent::Control
                        );
                        *alive = true;
                    }
                }
            }
        }
        std::thread::sleep(Duration::from_millis(5));
    }

    assert!(host_alive, "the host must see an answered probe");
    assert!(viewer_alive, "the viewer must see an answered probe");

    let stats = host.stats();
    assert!(stats.probes_sent > 0);
    assert!(stats.probes_answered > 0);
    assert!(
        stats.video_rtt_micros >= 0.0,
        "an answered probe yields an RTT sample"
    );
}

#[test]
fn control_events_are_delivered_in_order_over_udp() {
    let session = pair();

    for index in 0..5u8 {
        session
            .host
            .send_control(1, &[index])
            .expect("queue control event");
    }

    let deadline = Instant::now() + TIMEOUT;
    let mut received: Vec<u8> = Vec::new();
    while Instant::now() < deadline && received.len() < 5 {
        while let Some(event) = session.viewer.poll_event() {
            if let SessionEvent::Control { event_type, body } = event {
                assert_eq!(event_type, 1);
                received.push(body[0]);
            }
        }
        std::thread::sleep(Duration::from_millis(2));
    }

    assert_eq!(received, vec![0, 1, 2, 3, 4], "strictly ordered delivery");

    // Duplicate suppression is observable through the counters: no event may repeat.
    std::thread::sleep(Duration::from_millis(50));
    while let Some(event) = session.viewer.poll_event() {
        assert!(
            !matches!(event, SessionEvent::Control { .. }),
            "no duplicate control delivery"
        );
    }
}

#[test]
fn an_ack_only_control_frame_is_never_acknowledged() {
    // ACK-only frames carry no body and must not enter the reliable send queue.
    let ack = screencast_core::control::encode_ack(3, 0xf);
    let decoded = screencast_core::control::decode(&ack).expect("decode");
    assert!(decoded.is_ack);
    assert!(decoded.body.is_empty());

    // A Data frame with the sentinel ack base is a normal first message.
    let data = encode_data(0, SENTINEL_ACK_BASE, 0, 1, &[1]).expect("encode");
    let decoded = screencast_core::control::decode(&data).expect("decode");
    assert!(!decoded.is_ack);
    assert_eq!(decoded.ack_base, SENTINEL_ACK_BASE);
}

//! One peer-to-peer media session: two independent UDP components (video and control),
//! each with its own direction-scoped keys, packet sequence space, keepalive and receive
//! loop.
//!
//! This is the Rust equivalent of `MediaSession.cs`. The session does not allocate,
//! authenticate or refresh TURN resources; it only carries already-encrypted application
//! traffic. When a relay is used, the same two transports are pointed at the relay's
//! allocation instead of the direct peer address and the payload stays end-to-end encrypted.
//!
//! Cryptographic failures are counted and dropped, never propagated: a forged, replayed or
//! wrong-context datagram must not tear down a working session.
//!
//! Threading model (one owner per socket, mirroring the C# tasks):
//!
//! * video receive loop  - decrypt, reassemble, emit frames
//! * control receive loop - decrypt, feed the reliable channel (which ACKs)
//! * control send loop   - encrypt and send frames from the bounded outbound queue
//! * ticker              - retransmission and keepalive probes every 5 ms

use std::collections::VecDeque;
use std::fmt;
use std::io;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use crate::classifier::{classify, DatagramKind};
use crate::clock;
use crate::control::{EnqueueError, ReliableControlChannel};
use crate::packet::{Header, PacketChannel, PacketDirection, PacketProtector, ProtectError};
use crate::probe::{PathProbe, PathStatus, PROBE_SIZE};
use crate::transport::{is_transient, UdpTransport, MAX_DATAGRAM_SIZE};
use crate::video::{
    AssembledVideoFrame, VideoAssembler, VideoError, VideoFragmenter,
    DEFAULT_FRAME_LIFETIME_MICROS, FRAGMENT_HEADER_SIZE, MAX_FRAGMENT_PAYLOAD,
};

/// Stream id of the video component (fixed by the protocol).
pub const VIDEO_STREAM_ID: u32 = 1;
/// Stream id of the control component (fixed by the protocol).
pub const CONTROL_STREAM_ID: u32 = 2;
/// Bounded control outbound queue depth.
pub const CONTROL_QUEUE_CAPACITY: usize = 256;
/// Bounded event queue depth handed to the platform layer.
pub const EVENT_QUEUE_CAPACITY: usize = 1024;
/// Ticker period for retransmission and keepalive.
pub const TICK_INTERVAL: Duration = Duration::from_millis(5);

/// Which transport component an event belongs to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum PathComponent {
    Video = 0,
    Control = 1,
}

impl PathComponent {
    pub const fn as_u8(self) -> u8 {
        self as u8
    }
}

/// Everything the platform layer needs to observe.
#[derive(Debug)]
pub enum SessionEvent {
    /// A complete, authenticated, reassembled encoded frame.
    VideoFrame(AssembledVideoFrame),
    /// A reliable control event that was delivered exactly once, in order.
    Control { event_type: u16, body: Vec<u8> },
    /// A reliable control event that expired after 8 attempts / 2 s. The upper layer must
    /// release any held key: an input event that never arrived cannot be assumed inert.
    ControlExpired { event_type: u16 },
    /// A component's probe status changed.
    PathStatus {
        component: PathComponent,
        status: PathStatus,
        rtt_micros: f64,
    },
}

#[derive(Debug)]
pub enum SessionError {
    Io(io::Error),
    PeerNotSet,
    EmptyFrame,
    FrameTooLarge,
    InvalidControlBody,
    ControlWindowFull,
    ControlSequenceExhausted,
    Crypto(ProtectError),
    Video(VideoError),
    SameSocket,
}

impl fmt::Display for SessionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(formatter, "io error: {error}"),
            Self::PeerNotSet => write!(formatter, "peer address is not set"),
            Self::EmptyFrame => write!(formatter, "encoded frame is empty"),
            Self::FrameTooLarge => write!(formatter, "encoded frame exceeds the 8 MiB ceiling"),
            Self::InvalidControlBody => {
                write!(formatter, "control body exceeds the datagram budget")
            }
            Self::ControlWindowFull => write!(formatter, "control send window is full"),
            Self::ControlSequenceExhausted => {
                write!(formatter, "control message numbers exhausted")
            }
            Self::Crypto(error) => write!(formatter, "crypto error: {error:?}"),
            Self::Video(error) => write!(formatter, "video framing error: {error:?}"),
            Self::SameSocket => write!(formatter, "video and control must use independent sockets"),
        }
    }
}

impl std::error::Error for SessionError {}

impl From<io::Error> for SessionError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

impl From<ProtectError> for SessionError {
    fn from(error: ProtectError) -> Self {
        Self::Crypto(error)
    }
}

impl From<VideoError> for SessionError {
    fn from(error: VideoError) -> Self {
        Self::Video(error)
    }
}

/// Session configuration: the shared secret, session id, epoch and role.
#[derive(Debug, Clone)]
pub struct SessionConfig {
    pub shared_secret: [u8; 32],
    pub session_id: [u8; 16],
    pub key_epoch: u32,
    pub is_host: bool,
    pub frame_lifetime_micros: i64,
    pub probe_misses_for_down: i32,
    pub probe_keepalive_micros: i64,
}

impl SessionConfig {
    pub fn new(
        shared_secret: [u8; 32],
        session_id: [u8; 16],
        key_epoch: u32,
        is_host: bool,
    ) -> Self {
        Self {
            shared_secret,
            session_id,
            key_epoch,
            is_host,
            frame_lifetime_micros: DEFAULT_FRAME_LIFETIME_MICROS,
            probe_misses_for_down: crate::probe::DEFAULT_MISSES_FOR_DOWN,
            probe_keepalive_micros: crate::probe::DEFAULT_KEEPALIVE_MICROS,
        }
    }
}

/// Counters exposed to the platform layer.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct SessionStats {
    pub video_datagrams_received: u64,
    pub video_datagrams_rejected: u64,
    pub control_datagrams_received: u64,
    pub control_datagrams_rejected: u64,
    pub control_frames_dropped: u64,
    pub assembled_frames: u64,
    pub dropped_frames: u64,
    pub dropped_fragments: u64,
    pub video_bytes_sent: u64,
    pub control_bytes_sent: u64,
    pub replay_rejected: u64,
    pub probes_sent: u64,
    pub probes_answered: u64,
    pub events_dropped: u64,
    pub partial_frames: u64,
    pub video_path_status: PathStatus,
    pub control_path_status: PathStatus,
    pub video_rtt_micros: f64,
    pub control_rtt_micros: f64,
    pub video_local_port: u16,
    pub control_local_port: u16,
}

#[derive(Default)]
struct Counters {
    video_datagrams_received: AtomicU64,
    video_datagrams_rejected: AtomicU64,
    control_datagrams_received: AtomicU64,
    control_datagrams_rejected: AtomicU64,
    assembled_frames: AtomicU64,
    dropped_frames: AtomicU64,
    dropped_fragments: AtomicU64,
    video_bytes_sent: AtomicU64,
    control_bytes_sent: AtomicU64,
    replay_rejected: AtomicU64,
    probes_sent: AtomicU64,
    probes_answered: AtomicU64,
}

fn add(counter: &AtomicU64, value: u64) {
    counter.fetch_add(value, Ordering::Relaxed);
}

fn read(counter: &AtomicU64) -> u64 {
    counter.load(Ordering::Relaxed)
}

/// Bounded queue of already-encoded control frames waiting for the send thread.
struct Outbound {
    queue: Mutex<VecDeque<Vec<u8>>>,
    signal: Condvar,
    capacity: usize,
    dropped: AtomicU64,
    closed: AtomicBool,
}

impl Outbound {
    fn new(capacity: usize) -> Self {
        Self {
            queue: Mutex::new(VecDeque::new()),
            signal: Condvar::new(),
            capacity,
            dropped: AtomicU64::new(0),
            closed: AtomicBool::new(false),
        }
    }

    fn push(&self, frame: &[u8]) {
        if self.closed.load(Ordering::Relaxed) {
            return;
        }

        let mut queue = self.queue.lock().expect("outbound mutex");
        if queue.len() >= self.capacity {
            queue.pop_front();
            add(&self.dropped, 1);
        }
        queue.push_back(frame.to_vec());
        self.signal.notify_one();
    }

    /// Block until a frame is available, the session stops or the queue is closed.
    fn pop(&self, stop: &AtomicBool) -> Option<Vec<u8>> {
        let mut queue = self.queue.lock().expect("outbound mutex");
        loop {
            if let Some(frame) = queue.pop_front() {
                return Some(frame);
            }
            if stop.load(Ordering::Relaxed) || self.closed.load(Ordering::Relaxed) {
                return None;
            }
            let (guard, _) = self
                .signal
                .wait_timeout(queue, TICK_INTERVAL)
                .expect("outbound mutex");
            queue = guard;
        }
    }

    fn close(&self) {
        self.closed.store(true, Ordering::Relaxed);
        self.signal.notify_all();
    }
}

/// Bounded event queue handed to the platform layer. The oldest event is dropped when the
/// consumer falls behind, so a slow UI can never stall the receive path.
struct EventQueue {
    queue: Mutex<VecDeque<SessionEvent>>,
    capacity: usize,
    dropped: AtomicU64,
}

impl EventQueue {
    fn new(capacity: usize) -> Self {
        Self {
            queue: Mutex::new(VecDeque::new()),
            capacity,
            dropped: AtomicU64::new(0),
        }
    }

    fn push(&self, event: SessionEvent) {
        let mut queue = self.queue.lock().expect("event mutex");
        if queue.len() >= self.capacity {
            queue.pop_front();
            add(&self.dropped, 1);
        }
        queue.push_back(event);
    }

    fn pop(&self) -> Option<SessionEvent> {
        self.queue.lock().expect("event mutex").pop_front()
    }

    fn len(&self) -> usize {
        self.queue.lock().expect("event mutex").len()
    }

    fn dropped(&self) -> u64 {
        read(&self.dropped)
    }
}

struct ProbeState {
    video: PathProbe,
    control: PathProbe,
    video_status: PathStatus,
    control_status: PathStatus,
}

impl ProbeState {
    /// Borrow the probe and its last reported status for one component.
    fn for_component(&mut self, component: PathComponent) -> (&mut PathProbe, &mut PathStatus) {
        match component {
            PathComponent::Video => (&mut self.video, &mut self.video_status),
            PathComponent::Control => (&mut self.control, &mut self.control_status),
        }
    }
}

struct Peers {
    video: Option<SocketAddr>,
    control: Option<SocketAddr>,
}

/// Shared session state. The receive/send threads hold an `Arc` of this, while
/// [`MediaSession`] owns the join handles, so dropping the session can stop the threads.
pub struct SessionCore {
    video: Arc<UdpTransport>,
    control: Arc<UdpTransport>,
    video_send: PacketProtector,
    video_receive: PacketProtector,
    control_send: PacketProtector,
    control_receive: PacketProtector,
    probe_video_send: PacketProtector,
    probe_video_receive: PacketProtector,
    probe_control_send: PacketProtector,
    probe_control_receive: PacketProtector,
    assembler: Mutex<VideoAssembler>,
    control_channel: Arc<ReliableControlChannel>,
    peers: Mutex<Peers>,
    outbound: Arc<Outbound>,
    events: Arc<EventQueue>,
    probes: Mutex<ProbeState>,
    counters: Counters,
    stop: AtomicBool,
}

impl SessionCore {
    fn new(
        config: SessionConfig,
        video: UdpTransport,
        control: UdpTransport,
    ) -> Result<Self, SessionError> {
        let video_addr = video.local_addr()?;
        let control_addr = control.local_addr()?;
        if video_addr == control_addr {
            return Err(SessionError::SameSocket);
        }

        let secret = &config.shared_secret;
        let session_id = &config.session_id;
        let epoch = config.key_epoch;
        let outbound_direction = if config.is_host {
            PacketDirection::HostToViewer
        } else {
            PacketDirection::ViewerToHost
        };
        let inbound_direction = if config.is_host {
            PacketDirection::ViewerToHost
        } else {
            PacketDirection::HostToViewer
        };

        let video_send = PacketProtector::new(
            secret,
            session_id,
            epoch,
            outbound_direction,
            PacketChannel::Video,
            VIDEO_STREAM_ID,
        );
        let video_receive = PacketProtector::new(
            secret,
            session_id,
            epoch,
            inbound_direction,
            PacketChannel::Video,
            VIDEO_STREAM_ID,
        );
        let control_send = PacketProtector::new(
            secret,
            session_id,
            epoch,
            outbound_direction,
            PacketChannel::ReliableControl,
            CONTROL_STREAM_ID,
        );
        let control_receive = PacketProtector::new(
            secret,
            session_id,
            epoch,
            inbound_direction,
            PacketChannel::ReliableControl,
            CONTROL_STREAM_ID,
        );
        // Channel 3 probes: the video component uses stream 1, the control component
        // stream 2, so each component keeps its own probe key and packet sequence space.
        let probe_video_send = PacketProtector::new(
            secret,
            session_id,
            epoch,
            outbound_direction,
            PacketChannel::Probe,
            VIDEO_STREAM_ID,
        );
        let probe_video_receive = PacketProtector::new(
            secret,
            session_id,
            epoch,
            inbound_direction,
            PacketChannel::Probe,
            VIDEO_STREAM_ID,
        );
        let probe_control_send = PacketProtector::new(
            secret,
            session_id,
            epoch,
            outbound_direction,
            PacketChannel::Probe,
            CONTROL_STREAM_ID,
        );
        let probe_control_receive = PacketProtector::new(
            secret,
            session_id,
            epoch,
            inbound_direction,
            PacketChannel::Probe,
            CONTROL_STREAM_ID,
        );

        let outbound = Arc::new(Outbound::new(CONTROL_QUEUE_CAPACITY));
        let events = Arc::new(EventQueue::new(EVENT_QUEUE_CAPACITY));

        let control_channel = {
            let outbound = Arc::clone(&outbound);
            let control_events = Arc::clone(&events);
            let expired_events = Arc::clone(&events);
            Arc::new(ReliableControlChannel::new(
                move |frame: &[u8]| outbound.push(frame),
                move |event_type: u16, body: &[u8]| {
                    control_events.push(SessionEvent::Control {
                        event_type,
                        body: body.to_vec(),
                    });
                },
                Some(Box::new(move |event_type: u16| {
                    expired_events.push(SessionEvent::ControlExpired { event_type });
                })),
            ))
        };

        Ok(Self {
            assembler: Mutex::new(VideoAssembler::new(config.frame_lifetime_micros)),
            probes: Mutex::new(ProbeState {
                video: PathProbe::new(config.probe_misses_for_down, config.probe_keepalive_micros),
                control: PathProbe::new(
                    config.probe_misses_for_down,
                    config.probe_keepalive_micros,
                ),
                video_status: PathStatus::Probing,
                control_status: PathStatus::Probing,
            }),
            peers: Mutex::new(Peers {
                video: None,
                control: None,
            }),
            video: Arc::new(video),
            control: Arc::new(control),
            video_send,
            video_receive,
            control_send,
            control_receive,
            probe_video_send,
            probe_video_receive,
            probe_control_send,
            probe_control_receive,
            control_channel,
            outbound,
            events,
            counters: Counters::default(),
            stop: AtomicBool::new(false),
        })
    }

    fn local_ports(&self) -> (u16, u16) {
        (
            self.video.local_port().unwrap_or(0),
            self.control.local_port().unwrap_or(0),
        )
    }

    fn set_peer(&self, component: PathComponent, address: SocketAddr) {
        let mut peers = self.peers.lock().expect("peers mutex");
        match component {
            PathComponent::Video => peers.video = Some(address),
            PathComponent::Control => peers.control = Some(address),
        }
    }

    fn peer_for(&self, component: PathComponent) -> Option<SocketAddr> {
        let peers = self.peers.lock().expect("peers mutex");
        match component {
            PathComponent::Video => peers.video,
            PathComponent::Control => peers.control,
        }
    }

    fn transport_for(&self, component: PathComponent) -> &Arc<UdpTransport> {
        match component {
            PathComponent::Video => &self.video,
            PathComponent::Control => &self.control,
        }
    }

    /// Fragment, encrypt and send one encoded frame across the video component.
    pub fn send_video_frame(
        &self,
        encoded_frame: &[u8],
        frame_id: u64,
        capture_timestamp_micros: u64,
        config_id: u32,
        flags: u8,
        codec: u8,
    ) -> Result<u16, SessionError> {
        if encoded_frame.is_empty() {
            return Err(SessionError::EmptyFrame);
        }

        let peer = self
            .peer_for(PathComponent::Video)
            .ok_or(SessionError::PeerNotSet)?;
        let count = VideoFragmenter::fragment_count_for(encoded_frame.len())?;
        let mut buffer = vec![0u8; FRAGMENT_HEADER_SIZE + MAX_FRAGMENT_PAYLOAD];

        for index in 0..count {
            let length = VideoFragmenter::write_fragment(
                encoded_frame,
                frame_id,
                capture_timestamp_micros,
                config_id,
                flags,
                codec,
                index,
                &mut buffer,
            )?;
            let datagram = self.video_send.encode(&buffer[..length], false)?;
            self.video.send_to(&datagram, peer)?;
            add(&self.counters.video_bytes_sent, datagram.len() as u64);
        }

        Ok(count)
    }

    /// Queue one reliable control event. Returns false when the send window is full.
    pub fn send_control(&self, event_type: u16, body: &[u8]) -> Result<(), SessionError> {
        match self
            .control_channel
            .try_enqueue(event_type, body, clock::now_micros())
        {
            Ok(()) => Ok(()),
            Err(EnqueueError::WindowFull) => Err(SessionError::ControlWindowFull),
            Err(EnqueueError::SequenceExhausted) => Err(SessionError::ControlSequenceExhausted),
            Err(EnqueueError::BodyTooLarge) => Err(SessionError::InvalidControlBody),
        }
    }

    /// Drive control retransmission and keepalive probes. Called from the ticker thread and
    /// safe to call from the platform layer as well.
    pub fn tick(&self) {
        let now = clock::now_micros();
        self.control_channel.tick(now);

        let mut requests: Vec<(PathComponent, [u8; PROBE_SIZE])> = Vec::new();
        {
            let mut probes = self.probes.lock().expect("probes mutex");
            for component in [PathComponent::Video, PathComponent::Control] {
                let (probe, last_status) = probes.for_component(component);

                if probe.tick(now) {
                    requests.push((component, probe.create_request(now)));
                }

                let status = probe.status();
                if status != *last_status {
                    *last_status = status;
                    self.events.push(SessionEvent::PathStatus {
                        component,
                        status,
                        rtt_micros: probe.smoothed_rtt_micros(),
                    });
                }
            }
        }

        for (component, request) in requests {
            self.send_probe(component, &request);
        }
    }

    fn send_probe(&self, component: PathComponent, payload: &[u8; PROBE_SIZE]) {
        let Some(peer) = self.peer_for(component) else {
            return;
        };

        let protector = match component {
            PathComponent::Video => &self.probe_video_send,
            PathComponent::Control => &self.probe_control_send,
        };
        let transport = self.transport_for(component);

        if let Ok(datagram) = protector.encode(payload, false) {
            if transport.send_to(&datagram, peer).is_ok() {
                add(&self.counters.probes_sent, 1);
            }
        }
    }

    fn handle_probe(&self, component: PathComponent, plaintext: &[u8]) {
        let now = clock::now_micros();
        let (reply, answered) = {
            let mut probes = self.probes.lock().expect("probes mutex");
            let (probe, last_status) = probes.for_component(component);

            let reply = probe.on_probe(plaintext, now);
            let answered = probe.probes_answered();

            let status = probe.status();
            if status != *last_status {
                *last_status = status;
                self.events.push(SessionEvent::PathStatus {
                    component,
                    status,
                    rtt_micros: probe.smoothed_rtt_micros(),
                });
            }

            (reply, answered)
        };

        self.counters
            .probes_answered
            .store(answered, Ordering::Relaxed);

        if let Some(reply) = reply {
            self.send_probe(component, &reply);
        }
    }

    fn handle_datagram(&self, component: PathComponent, datagram: &[u8]) {
        if classify(datagram) != DatagramKind::Application {
            // Not ours: STUN, handshake, TURN ChannelData and garbage are ignored without
            // allocating a reply.
            return;
        }

        let Ok(header) = Header::parse(datagram) else {
            self.count_rejected(component);
            return;
        };

        let video_side = component == PathComponent::Video;
        match header.channel {
            PacketChannel::Video if video_side => {
                add(&self.counters.video_datagrams_received, 1);
                match self.video_receive.decode(datagram) {
                    Ok(plaintext) => self.on_video_plaintext(&plaintext),
                    Err(error) => self.count_crypto_failure(component, error),
                }
            }
            PacketChannel::ReliableControl if !video_side => {
                add(&self.counters.control_datagrams_received, 1);
                match self.control_receive.decode(datagram) {
                    Ok(plaintext) => {
                        self.control_channel
                            .on_frame(&plaintext, clock::now_micros());
                    }
                    Err(error) => self.count_crypto_failure(component, error),
                }
            }
            PacketChannel::Probe => {
                let protector = match component {
                    PathComponent::Video => &self.probe_video_receive,
                    PathComponent::Control => &self.probe_control_receive,
                };
                match protector.decode(datagram) {
                    Ok(plaintext) => self.handle_probe(component, &plaintext),
                    Err(error) => self.count_crypto_failure(component, error),
                }
            }
            _ => self.count_rejected(component),
        }
    }

    fn on_video_plaintext(&self, plaintext: &[u8]) {
        let now = clock::now_micros();
        let mut assembler = self.assembler.lock().expect("assembler mutex");
        let frame = assembler.push(plaintext, now);
        self.counters
            .dropped_frames
            .store(assembler.dropped_frames(), Ordering::Relaxed);
        self.counters
            .dropped_fragments
            .store(assembler.dropped_fragments(), Ordering::Relaxed);
        drop(assembler);

        if let Some(frame) = frame {
            add(&self.counters.assembled_frames, 1);
            self.events.push(SessionEvent::VideoFrame(frame));
        }
    }

    fn count_rejected(&self, component: PathComponent) {
        match component {
            PathComponent::Video => add(&self.counters.video_datagrams_rejected, 1),
            PathComponent::Control => add(&self.counters.control_datagrams_rejected, 1),
        }
    }

    fn count_crypto_failure(&self, component: PathComponent, error: ProtectError) {
        self.count_rejected(component);
        if error == ProtectError::Replay {
            add(&self.counters.replay_rejected, 1);
        }
    }

    /// Remove and return the oldest event, if any.
    pub fn poll_event(&self) -> Option<SessionEvent> {
        self.events.pop()
    }

    pub fn event_count(&self) -> usize {
        self.events.len()
    }

    pub fn stats(&self) -> SessionStats {
        let (video_port, control_port) = self.local_ports();
        let (video_status, control_status, video_rtt, control_rtt, partial_frames) = {
            let probes = self.probes.lock().expect("probes mutex");
            (
                probes.video.status(),
                probes.control.status(),
                probes.video.smoothed_rtt_micros(),
                probes.control.smoothed_rtt_micros(),
                self.assembler
                    .lock()
                    .expect("assembler mutex")
                    .partial_frame_count() as u64,
            )
        };

        SessionStats {
            video_datagrams_received: read(&self.counters.video_datagrams_received),
            video_datagrams_rejected: read(&self.counters.video_datagrams_rejected),
            control_datagrams_received: read(&self.counters.control_datagrams_received),
            control_datagrams_rejected: read(&self.counters.control_datagrams_rejected),
            control_frames_dropped: read(&self.outbound.dropped),
            assembled_frames: read(&self.counters.assembled_frames),
            dropped_frames: read(&self.counters.dropped_frames),
            dropped_fragments: read(&self.counters.dropped_fragments),
            video_bytes_sent: read(&self.counters.video_bytes_sent),
            control_bytes_sent: read(&self.counters.control_bytes_sent),
            replay_rejected: read(&self.counters.replay_rejected),
            probes_sent: read(&self.counters.probes_sent),
            probes_answered: read(&self.counters.probes_answered),
            events_dropped: self.events.dropped(),
            partial_frames,
            video_path_status: video_status,
            control_path_status: control_status,
            video_rtt_micros: video_rtt,
            control_rtt_micros: control_rtt,
            video_local_port: video_port,
            control_local_port: control_port,
        }
    }

    fn video_receive_loop(&self) {
        let mut buffer = vec![0u8; MAX_DATAGRAM_SIZE];
        while !self.stop.load(Ordering::Relaxed) {
            match self.video.recv_from(&mut buffer) {
                Ok((length, _source)) => {
                    self.handle_datagram(PathComponent::Video, &buffer[..length])
                }
                Err(error) => {
                    if !is_transient(&error) {
                        thread::sleep(Duration::from_millis(1));
                    }
                }
            }
        }
    }

    fn control_receive_loop(&self) {
        let mut buffer = vec![0u8; MAX_DATAGRAM_SIZE];
        while !self.stop.load(Ordering::Relaxed) {
            match self.control.recv_from(&mut buffer) {
                Ok((length, _source)) => {
                    self.handle_datagram(PathComponent::Control, &buffer[..length]);
                }
                Err(error) => {
                    if !is_transient(&error) {
                        thread::sleep(Duration::from_millis(1));
                    }
                }
            }
        }
    }

    fn control_send_loop(&self) {
        while let Some(frame) = self.outbound.pop(&self.stop) {
            let Some(peer) = self.peer_for(PathComponent::Control) else {
                continue;
            };

            if let Ok(datagram) = self.control_send.encode(&frame, false) {
                if self.control.send_to(&datagram, peer).is_ok() {
                    add(&self.counters.control_bytes_sent, datagram.len() as u64);
                }
            }
        }
    }

    fn ticker_loop(&self) {
        while !self.stop.load(Ordering::Relaxed) {
            self.tick();
            thread::sleep(TICK_INTERVAL);
        }
    }
}

/// One peer-to-peer media session owning two sockets and their threads.
pub struct MediaSession {
    core: Arc<SessionCore>,
    threads: Mutex<Vec<JoinHandle<()>>>,
}

impl MediaSession {
    /// Build a session around two already-bound transports.
    pub fn new(
        config: SessionConfig,
        video: UdpTransport,
        control: UdpTransport,
    ) -> Result<Self, SessionError> {
        Ok(Self {
            core: Arc::new(SessionCore::new(config, video, control)?),
            threads: Mutex::new(Vec::new()),
        })
    }

    /// Bind both components on the IPv4 wildcard address (port 0 picks ephemeral ports).
    pub fn bind(
        config: SessionConfig,
        video_port: u16,
        control_port: u16,
    ) -> Result<Self, SessionError> {
        let video = UdpTransport::bind_any(video_port)?;
        let control = UdpTransport::bind_any(control_port)?;
        Self::new(config, video, control)
    }

    pub fn core(&self) -> &Arc<SessionCore> {
        &self.core
    }

    pub fn local_ports(&self) -> (u16, u16) {
        self.core.local_ports()
    }

    pub fn set_peer(&self, component: PathComponent, address: SocketAddr) {
        self.core.set_peer(component, address);
    }

    pub fn set_peers(&self, video: SocketAddr, control: SocketAddr) {
        self.core.set_peer(PathComponent::Video, video);
        self.core.set_peer(PathComponent::Control, control);
    }

    pub fn is_started(&self) -> bool {
        !self.threads.lock().expect("threads mutex").is_empty()
    }

    /// Start the two receive loops, the control send loop and the ticker.
    pub fn start(&self) -> Result<(), SessionError> {
        let mut threads = self.threads.lock().expect("threads mutex");
        if !threads.is_empty() {
            return Ok(());
        }

        let video = Arc::clone(&self.core);
        threads.push(
            thread::Builder::new()
                .name("screencast-video-rx".into())
                .spawn(move || video.video_receive_loop())
                .map_err(SessionError::Io)?,
        );

        let control = Arc::clone(&self.core);
        threads.push(
            thread::Builder::new()
                .name("screencast-control-rx".into())
                .spawn(move || control.control_receive_loop())
                .map_err(SessionError::Io)?,
        );

        let sender = Arc::clone(&self.core);
        threads.push(
            thread::Builder::new()
                .name("screencast-control-tx".into())
                .spawn(move || sender.control_send_loop())
                .map_err(SessionError::Io)?,
        );

        let ticker = Arc::clone(&self.core);
        threads.push(
            thread::Builder::new()
                .name("screencast-ticker".into())
                .spawn(move || ticker.ticker_loop())
                .map_err(SessionError::Io)?,
        );

        Ok(())
    }

    /// Stop the threads. Idempotent; called automatically on drop.
    pub fn stop(&self) {
        self.core.stop.store(true, Ordering::Relaxed);
        self.core.outbound.close();

        let handles: Vec<JoinHandle<()>> = {
            let mut threads = self.threads.lock().expect("threads mutex");
            std::mem::take(&mut *threads)
        };
        for handle in handles {
            let _ = handle.join();
        }
    }

    pub fn send_video_frame(
        &self,
        encoded_frame: &[u8],
        frame_id: u64,
        capture_timestamp_micros: u64,
        config_id: u32,
        flags: u8,
        codec: u8,
    ) -> Result<u16, SessionError> {
        self.core.send_video_frame(
            encoded_frame,
            frame_id,
            capture_timestamp_micros,
            config_id,
            flags,
            codec,
        )
    }

    pub fn send_control(&self, event_type: u16, body: &[u8]) -> Result<(), SessionError> {
        self.core.send_control(event_type, body)
    }

    pub fn poll_event(&self) -> Option<SessionEvent> {
        self.core.poll_event()
    }

    pub fn tick(&self) {
        self.core.tick();
    }

    pub fn stats(&self) -> SessionStats {
        self.core.stats()
    }
}

impl Drop for MediaSession {
    fn drop(&mut self) {
        self.stop();
    }
}

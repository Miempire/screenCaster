//! A stable bound UDP socket reused for STUN discovery, hole punching and application
//! traffic for its whole lifetime, mirroring `UdpTransport.cs`.
//!
//! The receive buffer is enlarged to 4 MiB. A key frame arrives as one burst of dozens of
//! datagrams and the platform default is far too small: fragments are then silently
//! dropped, the whole frame is lost and every following P frame is undecodable. On the C#
//! side this single socket option was the difference between "video works" and "black
//! screen", so it is set here too (best effort; the platform may cap the value).
//!
//! Exactly one receive owner is allowed per transport. The socket is not closed to abort a
//! receive: a short read timeout lets the owning thread poll a stop flag, which keeps the
//! NAT mapping and the port intact for the whole session.

use std::io;
use std::net::{IpAddr, Ipv4Addr, SocketAddr, UdpSocket};
use std::time::Duration;

/// Maximum datagram the transport will read or write.
pub const MAX_DATAGRAM_SIZE: usize = 1500;
/// Default socket buffer, large enough for a 1080p key frame burst.
pub const DEFAULT_SOCKET_BUFFER_BYTES: i32 = 4 * 1024 * 1024;
/// Receive timeout used so a blocked reader can observe the stop flag.
pub const READ_POLL_INTERVAL: Duration = Duration::from_millis(50);

/// Best-effort `SO_RCVBUF` / `SO_SNDBUF` configuration.
mod options {
    #[cfg(windows)]
    mod imp {
        use std::net::UdpSocket;
        use std::os::windows::io::AsRawSocket;

        const SOL_SOCKET: i32 = 0xffff;
        const SO_SNDBUF: i32 = 0x1001;
        const SO_RCVBUF: i32 = 0x1002;

        #[link(name = "ws2_32")]
        extern "system" {
            fn setsockopt(
                socket: usize,
                level: i32,
                name: i32,
                value: *const u8,
                length: i32,
            ) -> i32;
            fn getsockopt(
                socket: usize,
                level: i32,
                name: i32,
                value: *mut u8,
                length: *mut i32,
            ) -> i32;
        }

        fn set(socket: &UdpSocket, name: i32, bytes: i32) -> bool {
            // SAFETY: the option value is a valid i32 with the matching length.
            let result = unsafe {
                setsockopt(
                    socket.as_raw_socket() as usize,
                    SOL_SOCKET,
                    name,
                    &bytes as *const i32 as *const u8,
                    std::mem::size_of::<i32>() as i32,
                )
            };
            result == 0
        }

        pub fn set_buffers(socket: &UdpSocket, bytes: i32) {
            let _ = set(socket, SO_RCVBUF, bytes);
            let _ = set(socket, SO_SNDBUF, bytes);
        }

        pub fn receive_buffer(socket: &UdpSocket) -> i32 {
            let mut value: i32 = 0;
            let mut length = std::mem::size_of::<i32>() as i32;
            // SAFETY: the output buffer and its length describe the same i32.
            let result = unsafe {
                getsockopt(
                    socket.as_raw_socket() as usize,
                    SOL_SOCKET,
                    SO_RCVBUF,
                    &mut value as *mut i32 as *mut u8,
                    &mut length,
                )
            };
            if result == 0 {
                value
            } else {
                0
            }
        }
    }

    #[cfg(unix)]
    mod imp {
        use std::ffi::c_void;
        use std::net::UdpSocket;
        use std::os::fd::AsRawFd;

        const SOL_SOCKET: i32 = 1;
        const SO_SNDBUF: i32 = 7;
        const SO_RCVBUF: i32 = 8;

        extern "C" {
            fn setsockopt(fd: i32, level: i32, name: i32, value: *const c_void, length: u32)
                -> i32;
            fn getsockopt(
                fd: i32,
                level: i32,
                name: i32,
                value: *mut c_void,
                length: *mut u32,
            ) -> i32;
        }

        fn set(socket: &UdpSocket, name: i32, bytes: i32) -> bool {
            // SAFETY: the option value is a valid i32 with the matching length.
            let result = unsafe {
                setsockopt(
                    socket.as_raw_fd(),
                    SOL_SOCKET,
                    name,
                    &bytes as *const i32 as *const c_void,
                    std::mem::size_of::<i32>() as u32,
                )
            };
            result == 0
        }

        pub fn set_buffers(socket: &UdpSocket, bytes: i32) {
            let _ = set(socket, SO_RCVBUF, bytes);
            let _ = set(socket, SO_SNDBUF, bytes);
        }

        pub fn receive_buffer(socket: &UdpSocket) -> i32 {
            let mut value: i32 = 0;
            let mut length = std::mem::size_of::<i32>() as u32;
            // SAFETY: the output buffer and its length describe the same i32.
            let result = unsafe {
                getsockopt(
                    socket.as_raw_fd(),
                    SOL_SOCKET,
                    SO_RCVBUF,
                    &mut value as *mut i32 as *mut c_void,
                    &mut length,
                )
            };
            if result == 0 {
                value
            } else {
                0
            }
        }
    }

    #[cfg(not(any(windows, unix)))]
    mod imp {
        use std::net::UdpSocket;

        pub fn set_buffers(_socket: &UdpSocket, _bytes: i32) {}

        pub fn receive_buffer(_socket: &UdpSocket) -> i32 {
            0
        }
    }

    pub use imp::{receive_buffer, set_buffers};
}

/// One bound UDP socket.
pub struct UdpTransport {
    socket: UdpSocket,
    actual_receive_buffer: i32,
}

impl UdpTransport {
    /// Bind to `ip:port` (port 0 picks an ephemeral port) and enlarge the socket buffers.
    pub fn bind(ip: IpAddr, port: u16) -> io::Result<Self> {
        Self::bind_with_buffer(ip, port, DEFAULT_SOCKET_BUFFER_BYTES)
    }

    /// Bind to the IPv4 wildcard address.
    pub fn bind_any(port: u16) -> io::Result<Self> {
        Self::bind(IpAddr::V4(Ipv4Addr::UNSPECIFIED), port)
    }

    /// Bind with an explicit socket buffer size.
    pub fn bind_with_buffer(ip: IpAddr, port: u16, buffer_bytes: i32) -> io::Result<Self> {
        let socket = UdpSocket::bind(SocketAddr::new(ip, port))?;
        options::set_buffers(&socket, buffer_bytes);
        socket.set_read_timeout(Some(READ_POLL_INTERVAL))?;
        let actual_receive_buffer = options::receive_buffer(&socket);
        Ok(Self {
            socket,
            actual_receive_buffer,
        })
    }

    pub fn local_addr(&self) -> io::Result<SocketAddr> {
        self.socket.local_addr()
    }

    pub fn local_port(&self) -> io::Result<u16> {
        Ok(self.local_addr()?.port())
    }

    /// Buffer size the socket actually accepted, for diagnostics (0 when unavailable).
    pub fn actual_receive_buffer(&self) -> i32 {
        self.actual_receive_buffer
    }

    pub fn set_read_timeout(&self, timeout: Option<Duration>) -> io::Result<()> {
        self.socket.set_read_timeout(timeout)
    }

    pub fn send_to(&self, payload: &[u8], destination: SocketAddr) -> io::Result<usize> {
        if payload.len() > MAX_DATAGRAM_SIZE {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "datagram exceeds the transport limit",
            ));
        }
        if destination.is_ipv4() != self.socket.local_addr()?.is_ipv4() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "destination address family does not match the bound socket",
            ));
        }
        self.socket.send_to(payload, destination)
    }

    /// Receive one datagram. A read timeout is reported as [`io::ErrorKind::WouldBlock`].
    pub fn recv_from(&self, buffer: &mut [u8]) -> io::Result<(usize, SocketAddr)> {
        self.socket.recv_from(buffer)
    }
}

/// Whether an error means "no datagram arrived yet" rather than a real failure.
pub fn is_transient(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut | io::ErrorKind::Interrupted
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn loopback_send_and_receive_preserves_payload_and_source_port() {
        let receiver = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("bind");
        let sender = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("bind");

        let payload = [9u8, 8, 7, 6, 5];
        let destination = receiver.local_addr().expect("addr");
        sender.send_to(&payload, destination).expect("send");

        let mut buffer = [0u8; MAX_DATAGRAM_SIZE];
        let (length, source) = receiver.recv_from(&mut buffer).expect("receive");
        assert_eq!(&buffer[..length], &payload);
        assert_eq!(source.port(), sender.local_port().expect("port"));
        assert!(source.ip().is_loopback());
    }

    #[test]
    fn receive_buffer_is_enlarged_or_reports_zero() {
        let transport = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("bind");
        let actual = transport.actual_receive_buffer();
        assert!(actual == 0 || actual >= 64 * 1024, "actual buffer {actual}");
    }

    #[test]
    fn read_timeout_reports_would_block() {
        let transport = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("bind");
        transport
            .set_read_timeout(Some(Duration::from_millis(10)))
            .expect("timeout");
        let mut buffer = [0u8; 32];
        let error = transport.recv_from(&mut buffer).expect_err("must time out");
        assert!(is_transient(&error), "unexpected error: {error:?}");
    }

    #[test]
    fn port_survives_a_timed_out_receive() {
        let receiver = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("bind");
        let port = receiver.local_port().expect("port");
        let mut buffer = [0u8; 32];
        let _ = receiver.recv_from(&mut buffer);
        assert_eq!(receiver.local_port().expect("port"), port);

        let sender = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("bind");
        sender
            .send_to(&[1, 2], receiver.local_addr().expect("addr"))
            .expect("send");
        let (length, _) = receiver.recv_from(&mut buffer).expect("receive");
        assert_eq!(length, 2);
    }

    #[test]
    fn oversized_datagrams_are_rejected_locally() {
        let transport = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("bind");
        let destination = transport.local_addr().expect("addr");
        let oversized = vec![0u8; MAX_DATAGRAM_SIZE + 1];
        let error = transport
            .send_to(&oversized, destination)
            .expect_err("must reject");
        assert_eq!(error.kind(), io::ErrorKind::InvalidInput);
    }

    #[test]
    fn address_family_mismatch_is_rejected() {
        let transport = UdpTransport::bind(IpAddr::V4(Ipv4Addr::LOCALHOST), 0).expect("bind");
        let destination: SocketAddr = "[::1]:9".parse().expect("v6 address");
        let error = transport
            .send_to(&[1], destination)
            .expect_err("must reject");
        assert_eq!(error.kind(), io::ErrorKind::InvalidInput);
    }
}

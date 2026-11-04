//! Native AWG 3.1 packet engine. Wire format follows amneziawg-go v3.1.20260828
//! (MIT); Noise/session/replay machinery is the isolated BoringTun 0.7.1 fork
//! (BSD-3-Clause). No keys or packet contents are logged.
use crate::ipc_contract::{WireNetwork, WireProfile};
use base64::Engine as _;
use chacha20::cipher::{KeyIvInit, StreamCipher};
use ior_awg_noise::{
    noise::{Tunn, TunnResult},
    x25519,
};
use std::{
    collections::BTreeMap,
    io,
    net::{Ipv4Addr, Ipv6Addr, SocketAddr},
    time::Duration,
};
fn invalid() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, "AWG_RUST_PARAMETER_INVALID")
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Range {
    lo: u32,
    hi: u32,
}
impl Range {
    fn parse(s: &str) -> io::Result<Self> {
        let mut p = s.split('-');
        let lo = p
            .next()
            .ok_or_else(invalid)?
            .parse()
            .map_err(|_| invalid())?;
        let hi = p
            .next()
            .map(|s| s.parse().map_err(|_| invalid()))
            .transpose()?
            .unwrap_or(lo);
        if p.next().is_some() || lo > hi {
            return Err(invalid());
        }
        Ok(Self { lo, hi })
    }
    fn pick(self) -> u32 {
        if self.lo == self.hi {
            return self.lo;
        }
        let mut b = [0; 4];
        getrandom::getrandom(&mut b).expect("OS random source");
        let n = u32::from_le_bytes(b);
        self.lo + ((n as u64 * (self.hi as u64 - self.lo as u64 + 1)) >> 32) as u32
    }
    fn has(self, n: u32) -> bool {
        self.lo <= n && n <= self.hi
    }
}
#[derive(Clone)]
enum Part {
    Bytes(Vec<u8>),
    Random(usize, u8),
    Timestamp,
    Size(usize),
    Data,
    DataString,
}
#[derive(Clone)]
struct Config {
    headers: [Range; 4],
    padding: [usize; 4],
    key: Option<[u8; 32]>,
    jc: usize,
    jmin: usize,
    jmax: usize,
    patterns: Vec<Vec<Part>>,
    addition: Range,
    timings: [Range; 5],
    random_trailers: bool,
    disable_cookies: bool,
    mtu: usize,
}
fn rand_fill(bytes: &mut [u8]) -> io::Result<()> {
    getrandom::getrandom(bytes).map_err(|_| io::Error::other("AWG_RNG_FAILED"))
}
fn parse_hex(s: &str) -> io::Result<Vec<u8>> {
    let s = s.strip_prefix("0x").unwrap_or(s);
    if s.is_empty() || s.len() % 2 != 0 {
        return Err(invalid());
    }
    (0..s.len())
        .step_by(2)
        .map(|i| {
            u8::from_str_radix(s.get(i..i + 2).ok_or_else(invalid)?, 16).map_err(|_| invalid())
        })
        .collect()
}
fn key(s: &str) -> io::Result<[u8; 32]> {
    if s.len() == 64 && s.bytes().all(|b| b.is_ascii_hexdigit()) {
        parse_hex(s)?.try_into().map_err(|_| invalid())
    } else {
        crate::tunnel_outbound::key32(s)
    }
}
fn pattern(s: &str) -> io::Result<Vec<Part>> {
    let mut parts = Vec::new();
    let mut rest = s;
    while let Some(start) = rest.find('<') {
        let end = rest[start..].find('>').ok_or_else(invalid)? + start;
        let mut fields = rest[start + 1..end].split_whitespace();
        let op = fields.next().ok_or_else(invalid)?;
        let value = fields.next().unwrap_or("");
        let length = || {
            value.parse::<usize>().map_err(|_| invalid()).and_then(|n| {
                if n <= 65507 {
                    Ok(n)
                } else {
                    Err(invalid())
                }
            })
        };
        parts.push(match op {
            "b" => Part::Bytes(parse_hex(value)?),
            "r" => Part::Random(length()?, 0),
            "rc" => Part::Random(length()?, 1),
            "rd" => Part::Random(length()?, 2),
            "t" => Part::Timestamp,
            "dz" => Part::Size(length()?),
            "d" => Part::Data,
            "ds" => Part::DataString,
            _ => return Err(invalid()),
        });
        rest = &rest[end + 1..];
    }
    Ok(parts)
}
fn pattern_bytes(parts: &[Part], src: &[u8]) -> io::Result<Vec<u8>> {
    let mut out = Vec::new();
    for part in parts {
        match part {
            Part::Bytes(bytes) => out.extend(bytes),
            Part::Random(n, kind) => {
                let start = out.len();
                out.resize(start + n, 0);
                rand_fill(&mut out[start..])?;
                for b in &mut out[start..] {
                    if *kind == 1 {
                        *b = b"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"
                            [*b as usize % 52];
                    } else if *kind == 2 {
                        *b = b'0' + *b % 10;
                    }
                }
            }
            Part::Timestamp => out.extend(
                (std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map_err(|_| invalid())?
                    .as_secs() as u32)
                    .to_be_bytes(),
            ),
            Part::Size(n) => {
                let start = out.len();
                out.resize(start + n, 0);
                let size = src.len().to_be_bytes();
                let count = (*n).min(size.len());
                if count > 0 {
                    out[start + n - count..].copy_from_slice(&size[size.len() - count..]);
                }
            }
            Part::Data => out.extend(src),
            Part::DataString => out.extend(
                base64::engine::general_purpose::STANDARD_NO_PAD
                    .encode(src)
                    .bytes(),
            ),
        }
        if out.len() > 65507 {
            return Err(invalid());
        }
    }
    Ok(out)
}
impl Config {
    fn from(profile: &WireProfile) -> io::Result<Self> {
        let empty = BTreeMap::new();
        let p = profile.options.awg.as_ref().unwrap_or(&empty);
        let number = |name: &str, default: u32| -> io::Result<u32> {
            p.get(name)
                .map(|v| v.parse().map_err(|_| invalid()))
                .unwrap_or(Ok(default))
        };
        let range = |name: &str, default: u32| -> io::Result<Range> {
            let v = p
                .get(name)
                .map(|s| Range::parse(s))
                .transpose()?
                .unwrap_or(Range {
                    lo: default,
                    hi: default,
                });
            Ok(if v.hi == 0 && default > 0 {
                Range {
                    lo: default,
                    hi: default,
                }
            } else {
                v
            })
        };
        let boolean = |name: &str| -> io::Result<bool> {
            match p.get(name).map(String::as_str).unwrap_or("false") {
                "true" | "1" | "on" | "yes" => Ok(true),
                "false" | "0" | "off" | "no" => Ok(false),
                _ => Err(invalid()),
            }
        };
        let headers = [
            range("h1", 1)?,
            range("h2", 2)?,
            range("h3", 3)?,
            range("h4", 4)?,
        ];
        for i in 0..4 {
            for j in i + 1..4 {
                if headers[i].lo <= headers[j].hi && headers[j].lo <= headers[i].hi {
                    return Err(invalid());
                }
            }
        }
        let padding = [
            number("s1", 0)? as usize,
            number("s2", 0)? as usize,
            number("s3", 0)? as usize,
            number("s4", 0)? as usize,
        ];
        let key = p
            .get("header_protection_key")
            .map(|s| key(s))
            .transpose()?
            .filter(|k| *k != [0; 32]);
        if key.is_some() && padding.iter().any(|s| *s < 12) {
            return Err(invalid());
        }
        if padding.iter().any(|s| *s > 65507 - 1500 - 148) {
            return Err(invalid());
        }
        let jc = number("jc", 0)? as usize;
        let jmin = number("jmin", 0)? as usize;
        let jmax = number("jmax", 0)? as usize;
        if jmin > jmax || jmax > 65507 || jc > 1024 {
            return Err(invalid());
        }
        let mut patterns = Vec::new();
        for name in ["i1", "i2", "i3", "i4", "i5"] {
            if let Some(s) = p.get(name) {
                patterns.push(pattern(s)?);
            }
        }
        let mut size = jc.checked_mul(jmax).ok_or_else(invalid)?;
        for parts in &patterns {
            size = size
                .checked_add(pattern_bytes(parts, &[])?.len())
                .ok_or_else(invalid)?;
        }
        if size > 1024 * 1024 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "AWG_OBFUSCATION_BUDGET_EXCEEDED",
            ));
        }
        for name in p.keys() {
            if !matches!(
                name.as_str(),
                "jc" | "jmin"
                    | "jmax"
                    | "s1"
                    | "s2"
                    | "s3"
                    | "s4"
                    | "h1"
                    | "h2"
                    | "h3"
                    | "h4"
                    | "i1"
                    | "i2"
                    | "i3"
                    | "i4"
                    | "i5"
                    | "header_protection_key"
                    | "content_padding_addition"
                    | "rekey_after_time"
                    | "reject_after_time"
                    | "rekey_timeout"
                    | "keepalive_timeout"
                    | "max_handshake_attempts"
                    | "random_trailers"
                    | "disable_cookies"
            ) {
                return Err(invalid());
            }
        }
        Ok(Self {
            headers,
            padding,
            key,
            jc,
            jmin,
            jmax,
            patterns,
            addition: range("content_padding_addition", 0)?,
            timings: [
                range("rekey_after_time", 120)?,
                range("reject_after_time", 180)?,
                range("rekey_timeout", 5)?,
                range("keepalive_timeout", 10)?,
                range("max_handshake_attempts", 18)?,
            ],
            random_trailers: boolean("random_trailers")?,
            disable_cookies: boolean("disable_cookies")?,
            mtu: profile.options.mtu.unwrap_or(1380) as usize,
        })
    }
}
pub fn validate(profile: &WireProfile) -> io::Result<()> {
    Config::from(profile).map(|_| ())
}
pub enum Event {
    Network(Vec<u8>),
    Tunnel(Vec<u8>),
}
pub struct Engine {
    inner: Tunn,
    config: Config,
    window: usize,
    work: Vec<u8>,
    peer_address: Option<std::net::IpAddr>,
}
impl Engine {
    pub fn new(profile: &WireProfile) -> io::Result<Self> {
        let config = Config::from(profile)?;
        let private =
            x25519::StaticSecret::from(crate::tunnel_outbound::key32(&profile.credential)?);
        let public = x25519::PublicKey::from(crate::tunnel_outbound::key32(
            profile
                .options
                .peer_public_key
                .as_deref()
                .ok_or_else(invalid)?,
        )?);
        let psk = profile
            .options
            .preshared_key
            .as_deref()
            .filter(|s| !s.is_empty())
            .map(crate::tunnel_outbound::key32)
            .transpose()?;
        Ok(Self {
            inner: Tunn::new(private, public, psk, profile.options.keepalive, 1, None),
            config,
            window: 500,
            work: vec![0; 65535],
            peer_address: profile.server.parse().ok(),
        })
    }
    fn configure(&mut self, len: usize) {
        let h = self.config.headers.map(|r| r.pick());
        self.window = self.window.max(self.config.padding[3] + 32 + len);
        let space = self
            .window
            .saturating_sub(self.config.padding[3] + 32 + len);
        let padding = if self.config.addition.hi > 0 {
            self.config.addition.pick() as usize
        } else if self.config.random_trailers {
            if space == 0 {
                0
            } else {
                Range {
                    lo: 0,
                    hi: (space - 1) as u32,
                }
                .pick() as usize
            }
        } else {
            let last = if len > self.config.mtu {
                len % self.config.mtu
            } else {
                len
            };
            ((last + 15) & !15).min(self.config.mtu) - last
        };
        self.inner
            .configure_awg(h, padding.min(space), self.config.disable_cookies);
        self.inner.configure_awg_timers([
            self.config.timings[0].pick() as u64,
            self.config.timings[1].hi as u64,
            self.config.timings[2].pick() as u64 * 1000 + Range { lo: 0, hi: 333 }.pick() as u64,
            self.config.timings[3].pick() as u64,
            self.config.timings[4].pick() as u64,
        ]);
    }
    // Separate fields allow encoding the borrowed crypto output without an intermediate packet copy.
    fn encode(config: &Config, window: usize, bytes: &[u8]) -> io::Result<Vec<Event>> {
        let value = u32::from_le_bytes(bytes.get(..4).ok_or_else(invalid)?.try_into().unwrap());
        let index = config
            .headers
            .iter()
            .position(|r| r.has(value))
            .ok_or_else(invalid)?;
        let padding = config.padding[index];
        let mut out = Vec::with_capacity(padding + bytes.len());
        out.resize(padding, 0);
        rand_fill(&mut out[..padding])?;
        out.extend(bytes);
        if let Some(key) = config.key {
            let nonce: [u8; 12] = out[..12].try_into().unwrap();
            let size = if index == 3 { 16 } else { bytes.len() };
            chacha20::ChaCha20::new((&key).into(), (&nonce).into())
                .apply_keystream(&mut out[padding..padding + size]);
        }
        if index != 3 && config.random_trailers {
            let space = window.saturating_sub(out.len());
            let extra = if space > 0 {
                Range {
                    lo: 0,
                    hi: (space - 1) as u32,
                }
                .pick() as usize
            } else {
                0
            };
            let start = out.len();
            out.resize(start + extra, 0);
            rand_fill(&mut out[start..])?;
        }
        if out.len() > 65507 {
            return Err(invalid());
        }
        let mut events = Vec::new();
        if index == 0 {
            for p in &config.patterns {
                let packet = pattern_bytes(p, &[])?;
                if !packet.is_empty() {
                    events.push(Event::Network(packet));
                }
            }
            for _ in 0..config.jc {
                let length = if config.jmax > config.jmin {
                    Range {
                        lo: config.jmin as u32,
                        hi: (config.jmax - 1) as u32,
                    }
                    .pick() as usize
                } else {
                    config.jmin
                };
                let mut packet = vec![0; length];
                rand_fill(&mut packet)?;
                events.push(Event::Network(packet));
            }
        }
        events.push(Event::Network(out));
        Ok(events)
    }
    pub fn send(&mut self, packet: &[u8]) -> io::Result<Vec<Event>> {
        self.configure(packet.len());
        let result = self.inner.encapsulate(packet, &mut self.work);
        match result {
            TunnResult::WriteToNetwork(bytes) => Self::encode(&self.config, self.window, bytes),
            _ => Ok(Vec::new()),
        }
    }
    pub fn tick(&mut self) -> io::Result<Vec<Event>> {
        self.configure(0);
        let result = self.inner.update_timers(&mut self.work);
        match result {
            TunnResult::WriteToNetwork(bytes) => Self::encode(&self.config, self.window, bytes),
            _ => Ok(Vec::new()),
        }
    }
    pub fn receive(&mut self, packet: &[u8]) -> io::Result<Vec<Event>> {
        let mut decoded = None;
        for index in 0..4 {
            let padding = self.config.padding[index];
            let expected = [148, 92, 64, 32][index];
            if packet.len() < padding + expected
                || (index != 3
                    && !self.config.random_trailers
                    && packet.len() != padding + expected)
            {
                continue;
            }
            let length = if index == 3 {
                packet.len() - padding
            } else {
                expected
            };
            let mut bytes = packet[padding..padding + length].to_vec();
            if let Some(key) = self.config.key {
                let Some(nonce) = packet.get(..12) else {
                    return Ok(Vec::new());
                };
                let nonce: [u8; 12] = nonce.try_into().unwrap();
                let count = if index == 3 { 16 } else { length };
                chacha20::ChaCha20::new((&key).into(), (&nonce).into())
                    .apply_keystream(&mut bytes[..count]);
            }
            let value = u32::from_le_bytes(bytes[..4].try_into().unwrap());
            if self.config.headers[index].has(value) {
                decoded = Some((index as u32 + 1, bytes));
                break;
            }
        }
        let Some((kind, bytes)) = decoded else {
            return Ok(Vec::new());
        };
        self.configure(0);
        let result = self
            .inner
            .decapsulate_awg(self.peer_address, &bytes, kind, &mut self.work);
        let mut events = match result {
            TunnResult::WriteToNetwork(p) => {
                Self::encode(&self.config, self.window, p)?
            }
            TunnResult::WriteToTunnelV4(p, _) | TunnResult::WriteToTunnelV6(p, _) => {
                self.window = self.window.max(packet.len());
                vec![Event::Tunnel(p.to_vec())]
            }
            _ => Vec::new(),
        };
        for _ in 0..256 {
            self.configure(0);
            let action = self.inner.decapsulate_awg(None, &[], 0, &mut self.work);
            if let TunnResult::WriteToNetwork(p) = action {
                events.extend(Self::encode(&self.config, self.window, p)?);
            } else {
                break;
            }
        }
        Ok(events)
    }
    pub fn authenticated(&self) -> bool {
        self.inner.stats().0.is_some()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore = "local packet CPU comparison; does not measure browser/network speed"]
    fn awg_packet_copy_cpu_comparison() {
        // Compare the previous intermediate crypto-output copy with the borrowed output path.
        for size in [32,1200] {
            let packet=crate::awg_packets::udp_packet("10.7.0.2:4444".parse().unwrap(),"10.7.0.1:443".parse().unwrap(),&vec![7;size]);
            let mut samples=[Vec::new(),Vec::new()];
            for round in 0..10 {
                for variant in if round%2==0 {[0,1]} else {[1,0]} {
                    let(mut a,mut b)=pair();let events=a.send(&packet).unwrap();assert_eq!(exchange(&mut a,&mut b,events),vec![packet.clone()]);
                    let started=std::time::Instant::now();
                    for _ in 0..4096 {
                        let events=if variant==0 {
                            a.configure(packet.len());let raw=match a.inner.encapsulate(&packet,&mut a.work){TunnResult::WriteToNetwork(p)=>p.to_vec(),_=>panic!("authenticated packet missing")};
                            Engine::encode(&a.config,a.window,&raw).unwrap()
                        }else{a.send(&packet).unwrap()};
                        std::hint::black_box(events);
                    }
                    samples[variant].push(started.elapsed().as_micros());
                }
            }
            samples.iter_mut().for_each(|values|values.sort_unstable());
            println!("AWG_COPY_CPU payloadBytes={size} packetsPerSample=4096 samples=10 legacyMedianUs={} candidateMedianUs={} legacyP95Us={} candidateP95Us={}",samples[0][5],samples[1][5],samples[0][9],samples[1][9]);
        }
    }
    #[test]
    fn protected_cookie_challenge_preserves_mac2_under_load() {
        let (mut a, mut b) = pair();
        let p = crate::awg_packets::udp_packet(
            "10.7.0.2:4".parse().unwrap(),
            "10.7.0.1:443".parse().unwrap(),
            b"data",
        );
        let hello = a
            .send(&p)
            .unwrap()
            .into_iter()
            .filter_map(|e| {
                if let Event::Network(p) = e {
                    Some(p)
                } else {
                    None
                }
            })
            .last()
            .unwrap();
        let mut challenge = None;
        for _ in 0..16 {
            for event in b.receive(&hello).unwrap() {
                if let Event::Network(bytes) = event {
                    challenge = Some(bytes);
                }
            }
        }
        let challenge = challenge.expect("cookie challenge missing");
        a.receive(&challenge).unwrap();
        a.configure(0);
        let raw = match a.inner.format_handshake_initiation(&mut a.work, true) {
            TunnResult::WriteToNetwork(p) => p.to_vec(),
            _ => panic!("retry missing"),
        };
        let events = Engine::encode(&a.config, a.window, &raw).unwrap();
        exchange(&mut a, &mut b, events);
        let events = a.send(&p).unwrap();
        assert_eq!(exchange(&mut a, &mut b, events), vec![p]);
    }
    fn profile(private: u8, peer: &[u8; 32]) -> WireProfile {
        serde_json::from_value(serde_json::json!({"protocol":"amneziawg","server":"127.0.0.1","port":1,"credential":base64::engine::general_purpose::STANDARD.encode([private;32]),"serverName":"localhost","publicKey":null,"shortId":null,"options":{"peerPublicKey":base64::engine::general_purpose::STANDARD.encode(peer),"mtu":1280,"awg":{"s1":"16","s2":"16","s3":"16","s4":"16","h1":"100-110","h2":"200-210","h3":"300-310","h4":"400-410","header_protection_key":base64::engine::general_purpose::STANDARD.encode([9;32]),"jc":"2","jmin":"12","jmax":"24","i1":"<b 0xff><t><r 4><rc 3><rd 2><dz 2><d><ds>","content_padding_addition":"8-16","random_trailers":"true","disable_cookies":"false","rekey_after_time":"2-4","rekey_timeout":"1-2","reject_after_time":"8-10","keepalive_timeout":"1-2","max_handshake_attempts":"2-4"}}})).unwrap()
    }
    fn pair() -> (Engine, Engine) {
        let a = x25519::PublicKey::from(&x25519::StaticSecret::from([7; 32]));
        let b = x25519::PublicKey::from(&x25519::StaticSecret::from([9; 32]));
        (
            Engine::new(&profile(7, b.as_bytes())).unwrap(),
            Engine::new(&profile(9, a.as_bytes())).unwrap(),
        )
    }
    fn exchange(a: &mut Engine, b: &mut Engine, events: Vec<Event>) -> Vec<Vec<u8>> {
        let mut queue = std::collections::VecDeque::new();
        let mut received = Vec::new();
        for event in events {
            if let Event::Network(p) = event {
                queue.push_back((false, p));
            }
        }
        let mut count = 0;
        while let Some((to_a, p)) = queue.pop_front() {
            count += 1;
            assert!(count < 1000);
            let events = if to_a { a.receive(&p) } else { b.receive(&p) }.unwrap();
            for e in events {
                match e {
                    Event::Network(p) => queue.push_back((!to_a, p)),
                    Event::Tunnel(p) => received.push(p),
                }
            }
        }
        received
    }
    #[test]
    fn protected_handshake_ranges_padding_replay_corruption_and_rekey() {
        let (mut a, mut b) = pair();
        let packet = crate::awg_packets::udp_packet(
            "10.7.0.2:4444".parse().unwrap(),
            "10.7.0.1:443".parse().unwrap(),
            b"data",
        );
        let events = a.send(&packet).unwrap();
        assert_eq!(exchange(&mut a, &mut b, events), vec![packet.clone()]);
        assert!(a.authenticated() && b.authenticated());
        let events = a.send(&packet).unwrap();
        let wire = events
            .into_iter()
            .find_map(|e| {
                if let Event::Network(p) = e {
                    Some(p)
                } else {
                    None
                }
            })
            .unwrap();
        let mut corrupt = wire.clone();
        let n = corrupt.len() - 1;
        corrupt[n] ^= 1;
        assert!(b.receive(&corrupt).unwrap().is_empty());
        assert!(matches!(b.receive(&wire).unwrap().as_slice(),[Event::Tunnel(p)] if *p==packet));
        assert!(b.receive(&wire).unwrap().is_empty());
        a.configure(0);
        let response = a.inner.format_handshake_initiation(&mut a.work, true);
        let raw = match response {
            TunnResult::WriteToNetwork(p) => p.to_vec(),
            _ => panic!("rekey missing"),
        };
        let events = Engine::encode(&a.config, a.window, &raw).unwrap();
        exchange(&mut a, &mut b, events);
        let events = a.send(&packet).unwrap();
        assert_eq!(exchange(&mut a, &mut b, events), vec![packet]);
    }
    #[test]
    fn invalid_mac_headers_and_bounds_are_rejected() {
        let (mut a, mut b) = pair();
        let p = crate::awg_packets::udp_packet(
            "10.7.0.2:1".parse().unwrap(),
            "10.7.0.1:443".parse().unwrap(),
            b"x",
        );
        let events = a.send(&p).unwrap();
        let mut hello = events
            .into_iter()
            .filter_map(|e| {
                if let Event::Network(p) = e {
                    Some(p)
                } else {
                    None
                }
            })
            .last()
            .unwrap();
        hello[16 + 116] ^= 1;
        assert!(b.receive(&hello).unwrap().is_empty());
        assert!(!b.authenticated());
        assert!(b.receive(&[0; 3]).unwrap().is_empty());
        let pubkey = x25519::PublicKey::from(&x25519::StaticSecret::from([9; 32]));
        let mut p = profile(7, pubkey.as_bytes());
        p.options
            .awg
            .as_mut()
            .unwrap()
            .insert("s4".into(), "4".into());
        assert!(validate(&p).is_err());
        p.options
            .awg
            .as_mut()
            .unwrap()
            .insert("s4".into(), "16".into());
        p.options
            .awg
            .as_mut()
            .unwrap()
            .insert("h1".into(), "200".into());
        assert!(validate(&p).is_err());
    }
    #[test]
    fn all_i_patterns_are_bounded_and_have_reference_encoding() {
        let p = pattern("<b ff><t><r 4><rc 3><rd 2><dz 2><d><ds>").unwrap();
        let bytes = pattern_bytes(&p, b"ABC").unwrap();
        assert_eq!(bytes[0], 255);
        assert_eq!(&bytes[14..16], &[0, 3]);
        assert_eq!(&bytes[16..19], b"ABC");
        assert_eq!(&bytes[19..], b"QUJD");
        assert!(pattern("<r 999999>").is_err());
        assert!(pattern("<unknown 1>").is_err());
    }
}
pub async fn start(
    profile: &WireProfile,
    bootstrap: SocketAddr,
    network: Option<WireNetwork>,
    mut input: tokio::sync::mpsc::Receiver<crate::packet_budget::Packet>,
    flows: crate::tunnel_outbound::Flows,
    local: Ipv4Addr,
    local6: Option<Ipv6Addr>,
    budget: std::sync::Arc<crate::packet_budget::Budget>,
) -> io::Result<tokio::task::JoinHandle<io::Result<()>>> {
    let mut engine = Engine::new(profile)?;
    let endpoint = SocketAddr::new(
        crate::dns_engine::resolve_server_ip_bound(&profile.server, bootstrap, &network).await?,
        profile.port,
    );
    engine.peer_address = Some(endpoint.ip());
    let physical = crate::native_router::bound_socket_family(true, &network, endpoint.is_ipv6())?;
    physical.set_recv_buffer_size(2 * 1024 * 1024)?;
    physical.set_send_buffer_size(2 * 1024 * 1024)?;
    let socket = tokio::net::UdpSocket::from_std(physical.into())?;
    socket.connect(endpoint).await?;
    let guard = crate::egress_registry::register(socket.local_addr()?, endpoint, true);
    crate::trace::code("transport", "AWG_RUST_ENGINE_READY");
    Ok(tokio::spawn(async move {
        struct End(crate::tunnel_outbound::Flows);
        impl Drop for End {
            fn drop(&mut self) {
                if let Ok(mut flows) = self.0.lock() {
                    flows.clear();
                }
            }
        }
        let _end = End(flows.clone());
        let _guard = guard;
        let mut tick = tokio::time::interval(Duration::from_millis(100));
        let mut buffer = vec![0; 65535];
        let mut reassembly = crate::tunnel_fragments::Reassembly::default();
        let mut authenticated = false;
        let mut outgoing = std::collections::VecDeque::<crate::packet_budget::Packet>::new();
        loop {
            // A blocked UDP write must not suspend inbound packets or the timer.
            // Keep FIFO order and backpressure application input until it drains.
            let events = tokio::select! {
                packet=input.recv(), if outgoing.is_empty()=>match packet{Some(packet)=>engine.send(&packet)?,None=>return Ok(())},
                result=network_step(socket.recv(&mut buffer), async {
                    match outgoing.front() {
                        Some(packet) => socket.send(packet).await,
                        None => std::future::pending().await,
                    }
                })=>match result? {
                    Some(size)=>engine.receive(&buffer[..size])?,
                    None=>{outgoing.pop_front();Vec::new()}
                },
                _=tick.tick()=>engine.tick()?
            };
            for event in events {
                match event {
                    Event::Network(p) => {
                        // The normal path stays allocation-free beyond encoding.
                        // Queue only when the physical socket reports backpressure.
                        if outgoing.is_empty() {
                            match socket.try_send(&p) {
                                Ok(size) if size == p.len()=>continue,
                                Ok(_)=>return Err(io::ErrorKind::WriteZero.into()),
                                Err(error) if error.kind()==io::ErrorKind::WouldBlock=>(),
                                Err(error)=>return Err(error),
                            }
                        }
                        // Only timer/handshake replies can grow this queue while
                        // application input is paused. Charge retained ciphertext
                        // to the same 64 MiB session budget as raw packets.
                        if outgoing.len() >= 1024 {
                            budget.queue_drop();
                            crate::trace::code("transport", "AWG_NETWORK_QUEUE_FULL");
                        } else {
                            match budget.retain(p) {
                                Ok(packet)=>outgoing.push_back(packet),
                                Err(_)=>crate::trace::code("transport", "AWG_NETWORK_BUDGET_EXHAUSTED"),
                            }
                        }
                    }
                    Event::Tunnel(p) => match reassembly.accept(&p) {
                        Ok(Some(p)) => {
                            crate::tunnel_outbound::dispatch_native(&flows, local, local6, &p)
                                .await?
                        }
                        Ok(None) => (),
                        Err(_) => crate::trace::code("tunnel", "TUNNEL_FRAGMENT_REJECTED"),
                    },
                }
            }
            if !authenticated && engine.authenticated() {
                authenticated = true;
                crate::trace::code("handshake", "AWG_HANDSHAKE_AUTHENTICATED");
            }
        }
    }))
}

async fn network_step<R, S>(receive: R, send: S) -> io::Result<Option<usize>>
where
    R: std::future::Future<Output = io::Result<usize>>,
    S: std::future::Future<Output = io::Result<usize>>,
{
    tokio::select! {
        size=receive=>size.map(Some),
        result=send=>result.map(|_|None),
    }
}

#[cfg(test)]
mod network_regression {
    use super::*;
    #[tokio::test]
    async fn blocked_udp_send_does_not_block_receive() {
        // The previous loop cannot reach recv while a send is pending.
        let legacy = async {
            std::future::pending::<io::Result<usize>>().await?;
            Ok::<_, io::Error>(1280usize)
        };
        assert!(tokio::time::timeout(Duration::from_millis(20), legacy).await.is_err());
        let received = tokio::time::timeout(Duration::from_millis(20), network_step(
            async { Ok(1280) }, std::future::pending::<io::Result<usize>>()
        )).await.unwrap().unwrap();
        assert_eq!(received, Some(1280));
    }

    #[tokio::test]
    async fn send_preserves_fifo_and_cancellation_releases_budget() {
        let budget = std::sync::Arc::new(crate::packet_budget::Budget::default());
        let mut queue = std::collections::VecDeque::new();
        for i in 0..128u8 { queue.push_back(budget.retain(vec![i;1280]).unwrap()); }
        for i in 0..64u8 {
            let packet = queue.front().unwrap();
            assert_eq!(packet.as_slice(), vec![i;1280]);
            assert_eq!(network_step(std::future::pending::<io::Result<usize>>(),async {Ok(packet.len())}).await.unwrap(),None);
            queue.pop_front();
        }
        assert!(budget.used()>0);
        drop(queue);
        assert_eq!(budget.used(),0);
        assert!(network_step(async {Err(io::ErrorKind::ConnectionReset.into())},std::future::pending::<io::Result<usize>>()).await.is_err());
    }
}

//! Streaming playback of 24 kHz mono speech through cpal.
//!
//! Samples are resampled to the device rate as they arrive and played from a queue, so speech
//! starts on the first block while the rest is still being synthesized. Each utterance is a
//! *session*: stopping bumps the session, which drops anything still queued at once. The output
//! stream lives on its own thread (cpal streams are not `Send` everywhere) and is closed after a
//! few idle seconds so the device can sleep.

use std::collections::VecDeque;
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{FromSample, SizedSample};

use crate::eq::Eq;
use crate::log;

pub const SOURCE_RATE: u32 = 24_000;
const IDLE_CLOSE: Duration = Duration::from_secs(8);

struct State {
    session: u64,
    queue: VecDeque<f32>,
    finished: bool,
    played: u64,
    device_rate: u32,
    /// Resampler: position of the next output sample, in input samples after `prev`.
    pos: f64,
    prev: f32,
    last_active: Instant,
    broken: bool,
    /// Loudness of the last block the device played (RMS, 0-1), for the status pill's bars.
    level: f32,
    /// The tone control, run on the 24 kHz speech before resampling.
    eq: Eq,
    /// Reused for the filtered copy of each block, so pushing never allocates once warm.
    scratch: Vec<f32>,
}

impl State {
    fn reset(&mut self) {
        self.session += 1;
        self.queue.clear();
        self.finished = false;
        self.played = 0;
        self.pos = 1.0;
        self.prev = 0.0;
        self.last_active = Instant::now();
        self.eq.reset();
    }
}

enum Command {
    Open { device: String, reply: Sender<Result<u32, String>> },
}

pub struct Player {
    state: Arc<Mutex<State>>,
    commands: Sender<Command>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Session(u64);

impl Default for Player {
    fn default() -> Self {
        Self::new()
    }
}

impl Player {
    pub fn new() -> Player {
        let state = Arc::new(Mutex::new(State {
            session: 0,
            queue: VecDeque::new(),
            finished: true,
            played: 0,
            device_rate: SOURCE_RATE,
            pos: 1.0,
            prev: 0.0,
            last_active: Instant::now(),
            broken: false,
            level: 0.0,
            eq: Eq::flat(SOURCE_RATE),
            scratch: Vec::new(),
        }));
        let (tx, rx) = mpsc::channel();
        let thread_state = state.clone();
        std::thread::Builder::new()
            .name("audio-output".into())
            .spawn(move || output_thread(rx, thread_state))
            .expect("spawn audio thread");
        Player { state, commands: tx }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Starts a new utterance on `device` (empty = system default), cutting off any other.
    pub fn begin(&self, device: &str) -> Result<Session, String> {
        let session = {
            let mut s = self.lock();
            s.reset();
            Session(s.session)
        };
        let (reply, answer) = mpsc::channel();
        self.commands
            .send(Command::Open { device: device.to_string(), reply })
            .map_err(|_| "The audio thread has stopped.".to_string())?;
        let rate = answer.recv().map_err(|_| "The audio thread has stopped.".to_string())??;
        let mut s = self.lock();
        if s.session == session.0 {
            s.device_rate = rate;
        }
        Ok(session)
    }

    /// Queues 24 kHz mono samples. False when the session has been stopped or replaced.
    pub fn push(&self, session: Session, samples: &[f32]) -> bool {
        let mut s = self.lock();
        if s.session != session.0 {
            return false;
        }
        let mut filtered = std::mem::take(&mut s.scratch);
        filtered.clear();
        filtered.extend_from_slice(samples);
        s.eq.process(&mut filtered);
        let samples = &filtered[..];
        let step = SOURCE_RATE as f64 / s.device_rate as f64;
        let (mut pos, mut prev) = (s.pos, s.prev);
        // Linear interpolation; `prev` is input index 0, samples[i] is index i + 1.
        let at = |i: usize, prev: f32| if i == 0 { prev } else { samples[i - 1] };
        let last = samples.len() as f64;
        while pos <= last {
            let i = pos.floor() as usize;
            let frac = (pos - i as f64) as f32;
            let a = at(i, prev);
            let b = if i < samples.len() { samples[i] } else { a };
            s.queue.push_back((a + (b - a) * frac).clamp(-1.0, 1.0));
            pos += step;
        }
        if let Some(&l) = samples.last() {
            prev = l;
        }
        s.pos = pos - last;
        s.prev = prev;
        s.last_active = Instant::now();
        s.scratch = filtered;
        true
    }

    /// Sets the tone control (gains in dB). Takes effect from the next block pushed.
    pub fn set_eq(&self, low_db: f32, mid_db: f32, high_db: f32) {
        self.lock().eq = Eq::new(SOURCE_RATE, low_db, mid_db, high_db);
    }

    /// No more audio is coming for `session`; it drains, then reports `drained`.
    pub fn finish(&self, session: Session) {
        let mut s = self.lock();
        if s.session == session.0 {
            s.finished = true;
        }
    }

    pub fn stop(&self) {
        let mut s = self.lock();
        s.reset();
        s.finished = true;
    }

    /// Seconds of audio still queued for `session` (0 when it is stale).
    pub fn queued_seconds(&self, session: Session) -> f64 {
        let s = self.lock();
        if s.session != session.0 {
            return 0.0;
        }
        s.queue.len() as f64 / s.device_rate as f64
    }

    /// (audio has started playing, everything was played) for `session`.
    pub fn progress(&self, session: Session) -> (bool, bool) {
        let s = self.lock();
        if s.session != session.0 {
            return (false, true);
        }
        (s.played > 0, s.finished && s.queue.is_empty())
    }

    /// How loud playback is right now (RMS of the last block, 0-1).
    pub fn level(&self) -> f32 {
        let s = self.lock();
        if s.queue.is_empty() && s.finished { 0.0 } else { s.level }
    }

    pub fn is_current(&self, session: Session) -> bool {
        self.lock().session == session.0
    }

    /// A short falling 880 Hz blip that confirms a stop.
    pub fn chime(&self, device: &str) {
        let Ok(session) = self.begin(device) else { return };
        let n = (SOURCE_RATE as f64 * 0.16) as usize;
        let samples: Vec<f32> = (0..n)
            .map(|i| {
                let t = i as f64 / SOURCE_RATE as f64;
                ((2.0 * std::f64::consts::PI * 880.0 * t).sin() * (-t * 18.0).exp() * 0.35) as f32
            })
            .collect();
        self.push(session, &samples);
        self.finish(session);
    }
}

/// Output device names for Settings.
pub fn output_devices() -> Vec<String> {
    let host = cpal::default_host();
    let mut names: Vec<String> =
        host.output_devices().map(|devices| devices.filter_map(|d| d.name().ok()).collect()).unwrap_or_default();
    names.dedup();
    names
}

fn find_device(name: &str) -> Option<cpal::Device> {
    let host = cpal::default_host();
    if !name.is_empty() {
        if let Ok(mut devices) = host.output_devices() {
            if let Some(d) = devices.find(|d| d.name().is_ok_and(|n| n == name)) {
                return Some(d);
            }
        }
        log!("audio: device '{name}' not found; using the default output");
    }
    host.default_output_device()
}

fn output_thread(commands: Receiver<Command>, state: Arc<Mutex<State>>) {
    let mut current: Option<(cpal::Stream, String, u32)> = None;
    loop {
        match commands.recv_timeout(Duration::from_secs(1)) {
            Ok(Command::Open { device, reply }) => {
                let broken = std::mem::take(&mut state.lock().unwrap_or_else(|e| e.into_inner()).broken);
                let reusable = !broken && current.as_ref().is_some_and(|(_, d, _)| *d == device);
                if !reusable {
                    current = None;
                    match open_stream(&device, state.clone()) {
                        Ok((stream, rate)) => current = Some((stream, device.clone(), rate)),
                        Err(e) => {
                            let _ = reply.send(Err(e));
                            continue;
                        }
                    }
                }
                let _ = reply.send(Ok(current.as_ref().map_or(SOURCE_RATE, |(_, _, r)| *r)));
            }
            Err(RecvTimeoutError::Timeout) => {
                let s = state.lock().unwrap_or_else(|e| e.into_inner());
                let idle = s.queue.is_empty() && s.last_active.elapsed() > IDLE_CLOSE;
                drop(s);
                if idle && current.is_some() {
                    current = None;
                }
            }
            Err(RecvTimeoutError::Disconnected) => return,
        }
    }
}

fn open_stream(device_name: &str, state: Arc<Mutex<State>>) -> Result<(cpal::Stream, u32), String> {
    let device = find_device(device_name).ok_or("No audio output device was found.")?;
    let supported = device.default_output_config().map_err(|e| format!("The audio device is unavailable: {e}"))?;
    let format = supported.sample_format();
    let config: cpal::StreamConfig = supported.into();
    let rate = config.sample_rate.0;
    let stream = match format {
        cpal::SampleFormat::F32 => build::<f32>(&device, &config, state),
        cpal::SampleFormat::I16 => build::<i16>(&device, &config, state),
        cpal::SampleFormat::U16 => build::<u16>(&device, &config, state),
        cpal::SampleFormat::I32 => build::<i32>(&device, &config, state),
        cpal::SampleFormat::F64 => build::<f64>(&device, &config, state),
        other => return Err(format!("Unsupported audio sample format {other:?}.")),
    }?;
    stream.play().map_err(|e| format!("Could not start audio playback: {e}"))?;
    log!(
        "audio: opened {} at {rate} Hz, {} channel(s), {format:?}",
        device.name().unwrap_or_else(|_| "?".into()),
        config.channels
    );
    Ok((stream, rate))
}

fn build<T>(
    device: &cpal::Device,
    config: &cpal::StreamConfig,
    state: Arc<Mutex<State>>,
) -> Result<cpal::Stream, String>
where
    T: SizedSample + FromSample<f32>,
{
    let channels = config.channels.max(1) as usize;
    let error_state = state.clone();
    device
        .build_output_stream(
            config,
            move |data: &mut [T], _| {
                let mut s = state.lock().unwrap_or_else(|e| e.into_inner());
                let mut played = 0u64;
                let mut energy = 0.0f32;
                for frame in data.chunks_mut(channels) {
                    let v = match s.queue.pop_front() {
                        Some(v) => {
                            played += 1;
                            energy += v * v;
                            v
                        }
                        None => 0.0,
                    };
                    let sample = T::from_sample(v);
                    frame.iter_mut().for_each(|o| *o = sample);
                }
                s.played += played;
                s.level = if played == 0 { 0.0 } else { (energy / played as f32).sqrt() };
            },
            move |e| {
                log!("audio: stream error: {e}");
                error_state.lock().unwrap_or_else(|e| e.into_inner()).broken = true;
            },
            None,
        )
        .map_err(|e| format!("Could not open the audio device: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn player_at(rate: u32) -> (Player, Session) {
        let p = Player::new();
        let session = {
            let mut s = p.lock();
            s.reset();
            s.device_rate = rate;
            Session(s.session)
        };
        (p, session)
    }

    #[test]
    fn resamples_24k_to_48k_without_gaps() {
        let (p, s) = player_at(48_000);
        let input: Vec<f32> = (0..2400).map(|i| i as f32 / 2400.0).collect();
        for block in input.chunks(333) {
            assert!(p.push(s, block));
        }
        let q = p.lock().queue.clone();
        assert!((q.len() as i64 - 4800).abs() <= 2, "{}", q.len());
        // Monotonic input stays monotonic across block boundaries.
        assert!(q.iter().zip(q.iter().skip(1)).all(|(a, b)| b >= a));
    }

    #[test]
    fn same_rate_passes_samples_through() {
        let (p, s) = player_at(24_000);
        p.push(s, &[0.1, 0.2, 0.3]);
        p.push(s, &[0.4]);
        let q: Vec<f32> = p.lock().queue.iter().copied().collect();
        assert_eq!(q, vec![0.1, 0.2, 0.3, 0.4]);
    }

    #[test]
    fn stopping_drops_the_queue_and_rejects_the_old_session() {
        let (p, s) = player_at(24_000);
        p.push(s, &[0.5; 100]);
        assert!(p.queued_seconds(s) > 0.0);
        p.stop();
        assert!(!p.push(s, &[0.5]));
        assert_eq!(p.queued_seconds(s), 0.0);
        assert_eq!(p.progress(s), (false, true), "a stale session counts as drained");
    }

    #[test]
    fn a_finished_session_drains_once_its_queue_is_empty() {
        let (p, s) = player_at(24_000);
        p.push(s, &[0.0; 10]);
        p.finish(s);
        assert_eq!(p.progress(s), (false, false));
        p.lock().queue.clear();
        assert!(p.progress(s).1);
    }
}

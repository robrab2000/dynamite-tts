//! A three-band tone control for speech: a low shelf (body), a peaking mid (presence) and a
//! high shelf (air).
//!
//! Each band is one biquad (RBJ audio-EQ cookbook) in transposed direct form II, run on the
//! 24 kHz speech before it is resampled for the device. That is 15 multiply-adds per sample
//! and no added latency: every output sample depends only on the current and past inputs. A
//! flat setting skips the filters entirely.

use std::f64::consts::PI;

/// Corner and centre frequencies, chosen for speech.
const LOW_HZ: f64 = 220.0;
const MID_HZ: f64 = 1_800.0;
const MID_Q: f64 = 0.9;
const HIGH_HZ: f64 = 5_000.0;
/// The range each slider offers, in dB.
pub const MAX_DB: f32 = 12.0;

#[derive(Clone, Copy, Debug, Default)]
struct Biquad {
    b0: f32,
    b1: f32,
    b2: f32,
    a1: f32,
    a2: f32,
    z1: f32,
    z2: f32,
}

impl Biquad {
    fn from(b: [f64; 3], a: [f64; 3]) -> Biquad {
        let n = |v: f64| (v / a[0]) as f32;
        Biquad { b0: n(b[0]), b1: n(b[1]), b2: n(b[2]), a1: n(a[1]), a2: n(a[2]), z1: 0.0, z2: 0.0 }
    }

    /// Shelf with slope S = 1, `high` selecting a high shelf.
    fn shelf(rate: f64, hz: f64, db: f64, high: bool) -> Biquad {
        let a = 10f64.powf(db / 40.0);
        let w = 2.0 * PI * hz / rate;
        let (sin, cos) = w.sin_cos();
        let alpha = sin / 2.0 * 2f64.sqrt();
        let k = 2.0 * a.sqrt() * alpha;
        let (am1, ap1) = (a - 1.0, a + 1.0);
        if high {
            Biquad::from(
                [a * (ap1 + am1 * cos + k), -2.0 * a * (am1 + ap1 * cos), a * (ap1 + am1 * cos - k)],
                [ap1 - am1 * cos + k, 2.0 * (am1 - ap1 * cos), ap1 - am1 * cos - k],
            )
        } else {
            Biquad::from(
                [a * (ap1 - am1 * cos + k), 2.0 * a * (am1 - ap1 * cos), a * (ap1 - am1 * cos - k)],
                [ap1 + am1 * cos + k, -2.0 * (am1 + ap1 * cos), ap1 + am1 * cos - k],
            )
        }
    }

    fn peak(rate: f64, hz: f64, q: f64, db: f64) -> Biquad {
        let a = 10f64.powf(db / 40.0);
        let w = 2.0 * PI * hz / rate;
        let (sin, cos) = w.sin_cos();
        let alpha = sin / (2.0 * q);
        Biquad::from([1.0 + alpha * a, -2.0 * cos, 1.0 - alpha * a], [1.0 + alpha / a, -2.0 * cos, 1.0 - alpha / a])
    }

    #[inline]
    fn process(&mut self, x: f32) -> f32 {
        let y = self.b0 * x + self.z1;
        self.z1 = self.b1 * x - self.a1 * y + self.z2;
        self.z2 = self.b2 * x - self.a2 * y;
        y
    }
}

/// The three bands at one sample rate.
#[derive(Clone, Debug)]
pub struct Eq {
    bands: [Biquad; 3],
    /// Headroom so boosted bands don't clip: half the largest boost, taken off the whole signal.
    gain: f32,
    flat: bool,
}

impl Eq {
    /// Gains in dB, clamped to ±[`MAX_DB`].
    pub fn new(rate: u32, low_db: f32, mid_db: f32, high_db: f32) -> Eq {
        let [low, mid, high] = [low_db, mid_db, high_db].map(|db| db.clamp(-MAX_DB, MAX_DB) as f64);
        let rate = rate as f64;
        let boost = low.max(mid).max(high).max(0.0);
        Eq {
            bands: [
                Biquad::shelf(rate, LOW_HZ, low, false),
                Biquad::peak(rate, MID_HZ, MID_Q, mid),
                Biquad::shelf(rate, HIGH_HZ, high, true),
            ],
            gain: 10f64.powf(-boost * 0.5 / 20.0) as f32,
            flat: low.abs() < 0.05 && mid.abs() < 0.05 && high.abs() < 0.05,
        }
    }

    pub fn flat(rate: u32) -> Eq {
        Eq::new(rate, 0.0, 0.0, 0.0)
    }

    /// Clears the filters' memory (a new utterance should not inherit the last one's tail).
    pub fn reset(&mut self) {
        for b in &mut self.bands {
            b.z1 = 0.0;
            b.z2 = 0.0;
        }
    }

    /// Filters `samples` in place.
    pub fn process(&mut self, samples: &mut [f32]) {
        if self.flat {
            return;
        }
        let [low, mid, high] = &mut self.bands;
        for s in samples {
            *s = high.process(mid.process(low.process(*s * self.gain)));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const RATE: u32 = 24_000;

    /// Steady-state gain (dB) of `eq` for a sine at `hz`.
    fn gain_at(eq: &mut Eq, hz: f64) -> f64 {
        let n = RATE as usize;
        let mut s: Vec<f32> = (0..n).map(|i| (2.0 * PI * hz * i as f64 / RATE as f64).sin() as f32 * 0.25).collect();
        eq.process(&mut s);
        let rms = |v: &[f32]| (v.iter().map(|x| (*x as f64).powi(2)).sum::<f64>() / v.len() as f64).sqrt();
        let tail = &s[n / 2..];
        20.0 * (rms(tail) / (0.25 / 2f64.sqrt())).log10()
    }

    #[test]
    fn flat_is_bit_exact() {
        let mut eq = Eq::flat(RATE);
        let mut s = vec![0.1, -0.5, 0.9, 0.0];
        eq.process(&mut s);
        assert_eq!(s, vec![0.1, -0.5, 0.9, 0.0]);
    }

    #[test]
    fn each_band_moves_its_own_frequencies() {
        // Low shelf +6 dB: lows up ~6 (minus the 3 dB headroom), highs untouched (minus headroom).
        let mut eq = Eq::new(RATE, 6.0, 0.0, 0.0);
        let (lo, hi) = (gain_at(&mut eq.clone(), 60.0), gain_at(&mut eq, 8_000.0));
        assert!((lo - 3.0).abs() < 0.5, "low {lo}");
        assert!((hi + 3.0).abs() < 0.5, "high {hi}");
        // High shelf -6 dB cuts the top and leaves the bottom.
        let mut eq = Eq::new(RATE, 0.0, 0.0, -6.0);
        assert!((gain_at(&mut eq.clone(), 10_000.0) + 6.0).abs() < 0.7);
        assert!(gain_at(&mut eq, 100.0).abs() < 0.3);
        // Mid peak +6 dB at its centre.
        let mut eq = Eq::new(RATE, 0.0, 6.0, 0.0);
        assert!((gain_at(&mut eq, MID_HZ) - 3.0).abs() < 0.5);
    }

    #[test]
    fn adds_no_latency_and_stays_stable() {
        let mut eq = Eq::new(RATE, 12.0, -12.0, 12.0);
        let mut impulse = vec![0.0f32; 4800];
        impulse[0] = 1.0;
        eq.process(&mut impulse);
        assert!(impulse[0].abs() > 0.1, "the response starts on the same sample");
        assert!(impulse[4000..].iter().all(|x| x.abs() < 1e-4), "and dies away");
    }

    /// Prints what the EQ costs: `cargo test --release eq_cost -- --ignored --nocapture`.
    #[test]
    #[ignore]
    fn eq_cost() {
        let mut eq = Eq::new(RATE, 3.0, -2.0, 4.0);
        let source: Vec<f32> = (0..2048).map(|i| (i as f32 * 0.07).sin() * 0.3).collect();
        let mut block = source.clone();
        let seconds = 600.0;
        let blocks = (RATE as f64 * seconds / block.len() as f64) as usize;
        let start = std::time::Instant::now();
        for _ in 0..blocks {
            block.copy_from_slice(&source);
            eq.process(&mut block);
        }
        let took = start.elapsed().as_secs_f64();
        println!(
            "{seconds} s of 24 kHz speech in {:.1} ms: {:.2} ns per sample, {:.0}x realtime",
            took * 1e3,
            took * 1e9 / (RATE as f64 * seconds),
            seconds / took
        );
        assert!(block[0].is_finite());
    }

    #[test]
    fn gains_are_clamped() {
        let mut eq = Eq::new(RATE, 40.0, 0.0, 0.0);
        let mut reference = Eq::new(RATE, MAX_DB, 0.0, 0.0);
        assert!((gain_at(&mut eq, 60.0) - gain_at(&mut reference, 60.0)).abs() < 1e-6);
    }
}

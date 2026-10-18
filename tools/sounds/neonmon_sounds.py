"""Offline generator for NeonMon interaction sounds. Renders 48 kHz 16-bit mono WAVs.

Not part of the app build. Needs Python 3 and numpy.

Tonal sounds come in four timbres that share one note set, so any choice stays in one key:
  string  Karplus-Strong pluck
  muted   the same pluck, darker excitation, shorter, two-pole lowpass
  block   wood-block knock: three inharmonic modes plus a tiny noise click
  glass   bell partials with a short transient
Hover ticks and peek hums do not depend on the timbre.

Usage:
  python -I tools/sounds/neonmon_sounds.py <out_dir>             every candidate, for auditioning
  python -I tools/sounds/neonmon_sounds.py --assets Assets/Sounds  the approved set the app embeds
"""
import json
import math
import sys
import wave
from pathlib import Path

import numpy as np

RATE = 48000
SEED = 7

NOTE = {"A4": 440.0, "D5": 587.33, "E5": 659.26, "A5": 880.0, "B5": 987.77, "C6": 1046.50,
        "C#6": 1108.73, "E6": 1318.51, "A6": 1760.0}

# name: (events [(start_ms, note)], decay_ms, peak_dbfs, brightness 0..1)
TONAL = {
    "click":          ([(0, "E6")], 45, -19, 0.35),
    "open":           ([(0, "A5"), (45, "E6")], 120, -18, 0.5),
    "hide":           ([(0, "E6"), (45, "A5")], 100, -21, 0.4),
    "success_rise":   ([(0, "A5"), (55, "C#6"), (110, "E6")], 140, -17, 0.55),
    "success_fourth": ([(0, "E6"), (70, "A6")], 130, -17, 0.5),
    "success_run":    ([(0, "A5"), (35, "B5"), (70, "C#6"), (105, "E6")], 110, -18, 0.5),
    "success_chime":  ([(0, "A5"), (0, "E6"), (60, "A6")], 160, -18, 0.55),
    "failure_drop":   ([(0, "E5"), (70, "D5")], 130, -17, 0.25),
    "failure_double": ([(0, "A4"), (90, "A4")], 90, -16, 0.25),
    "failure_minor":  ([(0, "C6"), (80, "A5")], 120, -18, 0.3),
    "failure_thud":   ([(0, "A4")], 90, -15, 0.15),
}

TIMBRES = ("string", "muted", "block", "glass")


def rng():
    return np.random.default_rng(SEED)


def envelope(n, decay_ms, attack_ms=2.0):
    t = np.arange(n) / RATE
    env = np.exp(-t / (decay_ms / 1000 / 4.6))  # about -40 dB at decay_ms
    attack = max(1, int(attack_ms / 1000 * RATE))
    env[:attack] *= np.linspace(0, 1, attack)
    return env


def one_pole(x, cutoff_hz, passes=1):
    a = 1 - math.exp(-2 * math.pi * cutoff_hz / RATE)
    for _ in range(passes):
        y = np.empty_like(x)
        s = 0.0
        for i, v in enumerate(x):
            s += a * (v - s)
            y[i] = s
        x = y
    return x


def highpass(x, cutoff_hz, passes=2):
    for _ in range(passes):
        x = x - one_pole(x, cutoff_hz)
    return x


def karplus(freq, n, smoothing, noise):
    # The averaging filter adds half a sample of delay, so the loop delay is shortened to match.
    delay = RATE / freq - 0.5
    p = int(delay)
    frac = delay - p
    out = np.zeros(n)
    k = max(1, smoothing)
    out[:p + 2] = np.convolve(noise[:p + 2], np.ones(k) / k, mode="same")
    for i in range(p + 2, n):
        a = (1 - frac) * out[i - p] + frac * out[i - p - 1]
        b = (1 - frac) * out[i - p - 1] + frac * out[i - p - 2]
        out[i] = 0.998 * 0.5 * (a + b)
    return out


def voice(timbre, freq, decay_ms, brightness, r):
    n = int(RATE * decay_ms / 1000 * 1.6)
    t = np.arange(n) / RATE
    if timbre == "string":
        smoothing = int(round((1 - brightness) * 6))
        return karplus(freq, n, smoothing, r.uniform(-1, 1, n)) * envelope(n, decay_ms * 1.4)
    if timbre == "muted":
        smoothing = int(round((1 - brightness) * 6)) + 3
        tone = karplus(freq, n, smoothing, r.uniform(-1, 1, n)) * envelope(n, decay_ms * 0.9)
        return one_pole(tone, 3200, passes=2)
    if timbre == "block":
        out = np.zeros(n)
        for ratio, amp, life in ((1.0, 1.0, 0.6), (2.32, 0.45 * (0.5 + brightness), 0.3), (4.25, 0.2 * (0.5 + brightness), 0.15)):
            out += amp * np.sin(2 * np.pi * freq * ratio * t) * envelope(n, decay_ms * life, attack_ms=0.5)
        click = one_pole(r.uniform(-1, 1, n), 5000) * envelope(n, 4, attack_ms=0.2)
        return out + 0.6 * click
    out = np.zeros(n)
    for ratio, amp, life in ((1.0, 1.0, 1.0), (2.76, 0.6 * brightness + 0.15, 0.55), (5.40, 0.35 * brightness + 0.05, 0.3)):
        out += amp * np.sin(2 * np.pi * freq * ratio * t) * envelope(n, decay_ms * life, attack_ms=0.8)
    tick = highpass(r.uniform(-1, 1, n), 4000) * envelope(n, 3, attack_ms=0.2)
    return out + 0.25 * tick


def finish(mix, peak_dbfs, floor_db=-50):
    # Trim the inaudible tail and fade the last 5 ms so playback never ends on a click.
    mix = mix / np.max(np.abs(mix))
    above = np.nonzero(np.abs(mix) > 10 ** (floor_db / 20))[0]
    mix = mix[:above[-1] + 1].copy()
    fade = min(len(mix), int(0.005 * RATE))
    mix[-fade:] *= np.linspace(1, 0, fade)
    return mix * 10 ** (peak_dbfs / 20)


def tonal(spec, timbre):
    events, decay_ms, peak_dbfs, brightness = spec
    r = rng()
    parts = [(int(RATE * start / 1000), voice(timbre, NOTE[name], decay_ms, brightness, r)) for start, name in events]
    last_start = max(start for start, _ in parts)
    mix = np.zeros(max(start + len(part) for start, part in parts))
    for start, part in parts:
        mix[start:start + len(part)] += (1.0 if start == last_start else 0.8) * part
    return finish(mix, peak_dbfs)


# Closed-hat style ticks: highpassed noise or the six detuned squares of an analog hat.
def hat(cutoff_hz, decay_ms, peak_dbfs, metallic=False):
    n = int(RATE * decay_ms / 1000 * 1.6)
    t = np.arange(n) / RATE
    if metallic:
        src = sum(np.sign(np.sin(2 * np.pi * f * 1.7 * t)) for f in (205.3, 304.4, 369.6, 522.7, 540.0, 800.0))
    else:
        src = rng().uniform(-1, 1, n)
    return finish(highpass(src, cutoff_hz) * envelope(n, decay_ms, attack_ms=0.4), peak_dbfs)


# Soft whoosh: noise through a swept state-variable bandpass under a sine-squared swell.
def whoosh(start_hz, end_hz, duration_ms, peak_dbfs, damping=1.4):
    n = int(RATE * duration_ms / 1000)
    noise = rng().uniform(-1, 1, n)
    low = band = 0.0
    out = np.empty(n)
    for i in range(n):
        fc = start_hz * (end_hz / start_hz) ** (i / n)
        f = 2 * math.sin(math.pi * fc / RATE)
        high = noise[i] - low - damping * band
        band += f * high
        low += f * band
        out[i] = band
    swell = np.sin(np.pi * np.arange(n) / n) ** 2
    return finish(out * swell, peak_dbfs, floor_db=-70)


# Soft hum: sines under a sine-squared swell, optionally gliding up into pitch, with a little breath noise.
def hum(freqs, duration_ms, peak_dbfs, breath=0.15, glide_from=None):
    n = int(RATE * duration_ms / 1000)
    progress = np.arange(n) / n
    swell = np.sin(np.pi * progress) ** 2
    out = np.zeros(n)
    for i, freq in enumerate(freqs):
        start = freq * glide_from / freqs[0] if glide_from else freq
        inst = start * (freq / start) ** np.minimum(1, progress * 2)
        phase = 2 * np.pi * np.cumsum(inst) / RATE
        out += (1.0 if i == 0 else 0.35) * np.sin(phase)
    if breath:
        air = whoosh(700, 1100, duration_ms, 0, damping=1.6)
        out[:len(air)] += breath * air
    return finish(out * swell, peak_dbfs, floor_db=-70)


COMMON = {
    "hover_air":   lambda: hat(7000, 20, -31),
    "hover_tick":  lambda: hat(10000, 11, -33),
    "hover_metal": lambda: hat(8000, 16, -33, metallic=True),
    "peek_warm":   lambda: whoosh(500, 2000, 160, -30),
    "peek_air":    lambda: whoosh(1500, 4500, 120, -32),
    "peek_breath": lambda: whoosh(900, 1300, 200, -31, damping=1.9),
    "peek_breath_soft":   lambda: whoosh(900, 1300, 200, -37, damping=1.9),
    "peek_breath_hum":    lambda: whoosh(820, 900, 190, -37, damping=0.35),
    "peek_hum":           lambda: hum([440.0, 880.0], 200, -41),
    "peek_hum_glide":     lambda: hum([440.0, 880.0], 180, -41, glide_from=392.0),
    "peek_hum_fifth":     lambda: hum([440.0, 659.26], 220, -42, breath=0.1),
    "unpeek_fall":        lambda: hum([392.0, 587.33], 220, -42, breath=0.1, glide_from=440.0),
    "unpeek_below":       lambda: hum([329.63, 440.0], 220, -42, breath=0.1),
    "unpeek_fall_below":  lambda: hum([293.66, 440.0], 220, -42, breath=0.1, glide_from=329.63),
}


def write_wav(path, samples):
    data = np.clip(np.round(samples * 32767), -32768, 32767).astype("<i2")
    with wave.open(str(path), "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(RATE)
        f.writeframes(data.tobytes())


def describe(group, name, samples, path):
    return {"group": group, "sound": name, "ms": round(len(samples) / RATE * 1000),
            "peak_dbfs": round(float(20 * np.log10(np.max(np.abs(samples)))), 1),
            "rms_dbfs": round(float(20 * np.log10(np.sqrt(np.mean(samples ** 2)))), 1),
            "bytes": path.stat().st_size}


# The owner-approved set (2026-10-18), named after the app's SoundCue values.
SHIPPED = {
    "hover":   lambda: COMMON["hover_tick"](),
    "click":   lambda: tonal(TONAL["click"], "block"),
    "peek":    lambda: COMMON["peek_hum_fifth"](),
    "unpeek":  lambda: COMMON["unpeek_fall_below"](),
    "open":    lambda: tonal(TONAL["open"], "block"),
    "hide":    lambda: tonal(TONAL["hide"], "block"),
    "success": lambda: tonal(TONAL["success_rise"], "block"),
    "failure": lambda: tonal(TONAL["failure_minor"], "block"),
}


def write_assets(folder):
    folder.mkdir(parents=True, exist_ok=True)
    for name, make in SHIPPED.items():
        samples = make()
        path = folder / f"{name}.wav"
        write_wav(path, samples)
        row = describe("shipped", name, samples, path)
        print(f"{name:8} {row['ms']:4} ms  peak {row['peak_dbfs']:6}  rms {row['rms_dbfs']:6}")


def main():
    if sys.argv[1] == "--assets":
        write_assets(Path(sys.argv[2]))
        return

    out = Path(sys.argv[1])
    report = []
    (out / "common").mkdir(parents=True, exist_ok=True)
    for name, make in COMMON.items():
        samples = make()
        path = out / "common" / f"{name}.wav"
        write_wav(path, samples)
        report.append(describe("common", name, samples, path))
    for timbre in TIMBRES:
        (out / timbre).mkdir(parents=True, exist_ok=True)
        for name, spec in TONAL.items():
            samples = tonal(spec, timbre)
            path = out / timbre / f"{name}.wav"
            write_wav(path, samples)
            report.append(describe(timbre, name, samples, path))
    (out / "report.json").write_text(json.dumps(report, indent=1))
    for row in report:
        print(f"{row['group']:7} {row['sound']:15} {row['ms']:4} ms  peak {row['peak_dbfs']:6}  rms {row['rms_dbfs']:6}")


if __name__ == "__main__":
    main()

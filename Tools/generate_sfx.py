#!/usr/bin/env python3
"""Synthesises the game's sound effects into Assets/Resources/Audio/*.wav.

Everything is generated from simple oscillators, envelopes and noise (no samples, no downloads), so the
sounds are original and licence-free, tiny, and reproducible: edit the recipes below and re-run
    python Tools/generate_sfx.py
Mono, 22.05 kHz, 16-bit PCM. Unity imports the .wav files as-is (GameAudio loads them via Resources).
"""
import math
import os
import random
import struct
import wave

RATE = 22050
OUT = os.path.join(os.path.dirname(__file__), "..", "Assets", "Resources", "Audio")
rng = random.Random(7)  # fixed seed: regenerating gives identical files


def env_exp(t, dur, attack=0.004, decay=6.0):
    """Fast attack, exponential decay - the basic 'blip' amplitude shape."""
    a = min(1.0, t / attack) if attack > 0 else 1.0
    return a * math.exp(-decay * t / dur)


def render(dur, fn):
    n = int(RATE * dur)
    return [fn(i / RATE, i, dur) for i in range(n)]


def sweep(f0, f1, t, dur):
    """Phase for a frequency glide f0 -> f1 (exponential) evaluated at time t."""
    k = math.log(f1 / f0) / dur
    return 2 * math.pi * f0 * (math.exp(k * t) - 1) / k


def lowpass(samples, cutoff_hz):
    rc = 1.0 / (2 * math.pi * cutoff_hz)
    dt = 1.0 / RATE
    alpha = dt / (rc + dt)
    out, y = [], 0.0
    for s in samples:
        y += alpha * (s - y)
        out.append(y)
    return out


def normalise(samples, peak=0.8):
    m = max(1e-9, max(abs(s) for s in samples))
    return [s * peak / m for s in samples]


def fade_out(samples, ms=8):
    n = int(RATE * ms / 1000)
    for i in range(min(n, len(samples))):
        samples[-1 - i] *= i / n
    return samples


def write(name, samples):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name + ".wav")
    samples = fade_out(normalise(samples))
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, s)) * 32767)) for s in samples))
    print(f"{name}.wav  {len(samples) / RATE * 1000:.0f} ms  {os.path.getsize(path) // 1024} KB")


# eat: a tiny rising blip - heard constantly, so short and soft
dur = 0.09
write("eat", render(dur, lambda t, i, d: math.sin(sweep(620, 940, t, d)) * env_exp(t, d, decay=5)))

# split / pop: a low 'whoomp' with a puff of filtered noise
dur = 0.28
noise = lowpass([rng.uniform(-1, 1) for _ in range(int(RATE * dur))], 1400)
write("split", render(dur, lambda t, i, d: (math.sin(sweep(340, 110, t, d)) * 0.9 + noise[i] * 0.5) * env_exp(t, d, decay=5)))

# merge: cells fusing - a soft rising sweep
dur = 0.34
write("merge", render(dur, lambda t, i, d: math.sin(sweep(220, 660, t, d)) * math.sin(math.pi * min(1.0, t / d)) ** 0.7))

# eject: a small thump
dur = 0.13
write("eject", render(dur, lambda t, i, d: math.sin(sweep(210, 80, t, d)) * env_exp(t, d, decay=7)))

# death: descending wobble that fades out
dur = 0.85
write("death", render(dur, lambda t, i, d: math.sin(sweep(520, 70, t, d) + 3.0 * math.sin(2 * math.pi * 7 * t)) * env_exp(t, d, attack=0.01, decay=3.2)))

# powerup: a bright four-note arpeggio (C5 E5 G5 C6)
notes = [523.25, 659.25, 783.99, 1046.5]
dur = 0.5


def arp(t, i, d):
    step = 0.09
    idx = min(len(notes) - 1, int(t / step))
    local = t - idx * step
    f = notes[idx]
    return (math.sin(2 * math.pi * f * t) + 0.3 * math.sin(2 * math.pi * 2 * f * t)) * env_exp(local, 0.25, decay=4)


write("powerup", render(dur, arp))

# shield: airy two-tone chime, deliberately different from the powerup arpeggio
dur = 0.55
write("shield", render(dur, lambda t, i, d: (math.sin(2 * math.pi * 392 * t) + math.sin(2 * math.pi * 587.3 * t)) * 0.5 * env_exp(t, d, attack=0.02, decay=3.5)))

# click: UI tap
dur = 0.045
write("click", render(dur, lambda t, i, d: math.sin(2 * math.pi * 1300 * t) * env_exp(t, d, attack=0.001, decay=8)))

# warning: connection lost - two low beeps
dur = 0.4
write("warning", render(dur, lambda t, i, d: math.sin(2 * math.pi * 300 * t) * (1.0 if (t % 0.2) < 0.12 else 0.0) * env_exp(t % 0.2, 0.2, decay=2)))

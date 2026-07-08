# Lossless Wi-Fi Loss Resilience — Mechanism Comparison (Decision Record)

Date: 2026-06-30
Status: design (exploratory — not scheduled; a sibling/alternative to the Opus proposal)

## Purpose

The [Opus compression design](2026-06-28-opus-compression-design.md) targets the **Wi-Fi** use
case, where packet loss is real and bandwidth can be tight. Opus addresses that with a **lossy**
codec plus its built-in PLC/FEC. This document explores the opposite trade: keep the wire payload
**bit-exact PCM** and add loss resilience as a separate, codec-independent layer — so we get Wi-Fi
robustness without giving up losslessness, the format subset, or taking on a managed codec
dependency.

This is a **general comparison and decision record only** — no wire format, header layout, or
implementation detail yet. It exists to pick a mechanism before anyone designs one.

## What "lossless resilience" means here

Recovery must reproduce the **original bytes** of a lost datagram, not a plausible interpolation.
A scheme is "lossless" when, within its latency budget, a recovered packet is identical to what was
sent. The only non-lossless moment is the **last-resort fallback** (below), used when the network
genuinely fails to deliver a packet in time — the lossless analogue of Opus PLC.

## The constraints that decide this (from the current code)

- **Per-datagram timing:** at 48 kHz/16-bit/stereo, `BlockAlign` = 4 B and the sender's `maxChunk`
  is `(1440/4)*4 = 1440` B = **360 frames = 7.5 ms of audio per full datagram**. Note the PCM path has
  no accumulator — each WASAPI callback is sliced independently, emitting a partial trailing datagram
  per callback, so the *measured* rate is **~200 pkts/s** (confirmed via the app's `pkts/s` diagnostic),
  not the 133/s a fully-packed stream would imply. Either way a "group of N packets" or "one RTT"
  translates to small numbers of milliseconds.
- **Playout deadline:** the receiver's drift cap (`ReceiverMaxLatencyMilliseconds`, default
  **150 ms**) is the time any recovery has to land before the gap is played. 150 ms is *huge*
  relative to a Wi-Fi RTT or a few packet-times — so **latency is not the binding constraint;
  bandwidth and architectural surface area are.**
- **Existing machinery to reuse:** `ReorderBuffer` already restores sequence order and feeds the
  audio buffer in order; `SequenceLossTracker` already detects and classifies gaps. Any recovered
  datagram just needs to enter `ReorderBuffer` in sequence — no new playout path.
- **The app is proudly one-directional:** the sender only `Send()`s; the receiver only receives.
  Anything that adds a back-channel is a real architectural change, not a tweak.

**Key asymmetry:** redundancy is cheap for Opus and expensive for PCM. An Opus frame is ~200 B, so
duplicating it is trivial — which is *why* Opus pairs naturally with FEC/RED. A PCM datagram is
~1440 B, so any *proactive* duplication pushes the already-heavy ~1.5 Mbps stream toward 3 Mbps.
That asymmetry is the whole reason a lossless/PCM path wants a different resilience strategy than a
compressed path would.

## The three mechanisms

### A. Parity FEC (XOR, or Reed–Solomon for multi-loss) — **recommended**

Group every N data datagrams and emit M parity datagrams; the receiver reconstructs up to M losses
per group by XOR/RS.

- **Lossless:** yes — reconstructed bytes are bit-exact.
- **Bandwidth:** a *constant* **+M/N tax whether or not loss occurs** (e.g. RS(8,1) ≈ +12.5%), paid
  forever even on a clean wire.
- **Latency:** in-order packets play immediately; only a *lost* one waits for its group to complete
  (~N × 7.5 ms; a group of 8 ≈ 60 ms — comfortably under the 150 ms cap).
- **Wi-Fi reality:** Wi-Fi loss is **bursty** (consecutive drops during interference). Single-parity
  XOR fails on a burst; spanning one needs RS(N, M ≥ 2) **plus interleaving** — more math and a small
  GF(256) routine.
- **Architecture:** **stays fully unidirectional** — a pure sender-side addition plus receiver-side
  recovery feeding the existing `ReorderBuffer`. No listening socket on the sender, no new attack
  surface, no bidirectional protocol.

### B. NACK / selective retransmission (ARQ)

The receiver detects a gap (it already does), asks the sender to resend the missing sequence, and
the sender replays it from a small ring buffer of recent datagrams.

- **Lossless:** yes — the retransmit is the original datagram.
- **Bandwidth:** **~0 % on a clean wire; scales with actual loss.** Best-in-class for a stream that
  is already bandwidth-heavy on Wi-Fi.
- **Latency:** +1 RTT, and only on a loss (Wi-Fi ~2–10 ms — trivially under 150 ms).
- **Wi-Fi reality:** handles bursts natively (request every missing sequence). A NACK or a
  retransmit can itself be lost, so it needs deadline-bounded retry; under sustained heavy loss it
  must cap retransmits to avoid a storm.
- **Architecture:** the **one real cost** — it breaks the one-way model. The sender must now *listen*
  (validate the source address, rate-limit to avoid a NACK-flood DoS) and keep a send-side ring
  buffer; the receiver must learn the sender's endpoint (available from `ReceiveFrom`). A genuine new
  bidirectional protocol surface.

### C. Redundant / RED-style transmission (piggyback previous frame, or send-twice) — rejected

- **Lossless:** yes.
- **Bandwidth:** **worst for PCM** — proactive duplication roughly doubles the stream (~3 Mbps).
  This is the disqualifier for a raw-PCM path.
- **Latency:** piggyback adds +1 packet (7.5 ms) always; send-twice adds none.
- **Wi-Fi reality:** without spacing/interleaving, a burst takes out both copies.
- **Verdict:** rejected for the PCM path on bandwidth grounds. Only worth revisiting if a *compressed*
  path ever exists, where small frames make duplication cheap.

### Last-resort fallback (all mechanisms)

When recovery misses the playout deadline, **conceal** rather than click — repeat the last good frame
with a short fade (the lossless analogue of Opus PLC). This is the only non-lossless moment, and only
when the network genuinely fails to deliver in time. It can reuse the same additive `onLost` hook the
Opus design proposes for `ReorderBuffer`.

## Recommendation

Lead with **A — interleaved parity FEC (RS-capable)**, and document **B (NACK)** as the
"more bandwidth-efficient, handles bad Wi-Fi better, but adds a back-channel" upgrade.

Why A fits *this* app:

- It **preserves the app's defining simplicity** — one-directional UDP, no listening socket on the
  sender, no NACK-flood security surface, no bidirectional protocol. That matches the codebase's
  whole personality.
- It **drops into the existing pipeline** — recovered datagrams just feed `ReorderBuffer` in order;
  the latency cap and diagnostics are untouched.
- Its ~12 % constant tax is real but predictable, with no latency spikes or retransmit storms under
  bad Wi-Fi — an acceptable price for a *bit-exact* guarantee on a LAN/Wi-Fi.

Why B is credited rather than dismissed:

- On congested Wi-Fi, **bandwidth is the thing that actually matters**, and B pays nothing on a clean
  wire while scaling cleanly with real loss; it also absorbs bursts without RS math. If a
  bidirectional protocol is acceptable, B is the more elegant *engineering* answer; A is the more
  elegant *fit-this-app* answer.
- A realistic v2 is the **hybrid**: a small proactive parity to cover the common isolated loss with
  zero added latency and no round-trip, plus NACK for the rest.

## Out of scope (for this record)

- Wire format / header layout / packet-type signalling.
- Parity grouping/interleaving parameters, GF(256) details, ring-buffer sizing.
- Lossless *compression* for bandwidth (a separate, orthogonal question).
- Any implementation plan — this record only selects the mechanism.

## Relationship to the Opus proposal

These are **alternatives, not complements** for the same goal (Wi-Fi robustness): Opus trades
losslessness for built-in resilience + bandwidth savings; this path keeps PCM bit-exact and adds
resilience as a codec-independent layer. They could in principle coexist (Opus + parity), but that is
explicitly not proposed here. Pick one direction first.

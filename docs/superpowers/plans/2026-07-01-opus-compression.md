# Optional Opus Compression Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add optional Opus compression (via OpusSharp / native libopus) as an alternative to the raw-PCM UDP payload, selectable per session, defaulting to PCM.

**Architecture:** A codec seam (`IAudioEncoder`/`IAudioDecoder`) sits between capture and the socket on the sender, and between the socket and the buffered-wave-provider on the receiver. PCM keeps today's MTU-slice behaviour; Opus accumulates fixed frames and encodes. The codec is announced in a new 1-byte wire header field; the receiver adapts. Selecting Opus fixes the session to 48 kHz/16-bit/stereo (derived locally, without clobbering stored PCM config).

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), WPF + WinForms, NAudio.Wasapi, OpusSharp (native libopus 1.6.1), System.Text.Json, xUnit (new test project).

## Global Constraints

- Single main project `AudioStreamer.csproj` at repo root; flat `AudioStreamer` namespace regardless of folder; SDK-style `**/*.cs` globbing.
- **No per-callback allocation** on the capture (`DataAvailable`) or receive threads — a GC pause there is an audible dropout. Reuse pre-allocated buffers.
- Opus is fixed to **48 kHz / 16-bit / stereo, `OPUS_APPLICATION_RESTRICTED_LOWDELAY`, 192 000 bps VBR, complexity 10**.
- OpusSharp: reference **`OpusSharp.Core` + the win-x64 native only** (not the all-platforms meta-package).
- PCM stays the default codec; the wire change is **breaking** (both ends must run the same build — already required).
- `CodecId` is a **public top-level enum** (a `public` `Config.Codec` property cannot expose an `internal` type).
- Commit after every task. Build the main app with `dotnet build`; run tests with `dotnet test AudioStreamer.Tests`.
- Spec: `docs/superpowers/specs/2026-06-28-opus-compression-design.md`. This plan refines two spec details discovered during planning, noted where they occur: (a) `CodecId` is public top-level, (b) the Opus reorder window is a **fixed bounded packet count**, not `ceil(targetMs/frameMs)` (the receiver can't know the sender's frame size before decoding; a fixed count keeps give-up time bounded under the drift cap across all frame sizes).

---

### Task 1: Test project + `InternalsVisibleTo`

Creates the xUnit project the TDD tasks write into, and makes the main project's `internal` types visible to it. The main csproj globs `**/*.cs`, so the test folder must be excluded from the main compile or the app won't build.

**Files:**
- Create: `AudioStreamer.Tests/AudioStreamer.Tests.csproj`
- Create: `AudioStreamer.Tests/SmokeTests.cs`
- Modify: `AudioStreamer.csproj` (exclude the test folder; add `InternalsVisibleTo`)

**Interfaces:**
- Produces: a runnable `dotnet test AudioStreamer.Tests`; `internal` types in `AudioStreamer` visible to `AudioStreamer.Tests`.

- [ ] **Step 1: Create the test project file**

Create `AudioStreamer.Tests/AudioStreamer.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- net10.0-windows to match the main project (it targets net10.0-windows for WPF). -->
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <!-- Match the app's GC profile so any alloc-sensitive assertions behave like the app. -->
    <ServerGarbageCollection>false</ServerGarbageCollection>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\AudioStreamer.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Exclude the test folder from the main project and expose internals**

In `AudioStreamer.csproj`, inside the existing `<Project>`, add a new `<ItemGroup>` (after the `EmbeddedResource` group):

```xml
  <ItemGroup>
    <!-- The SDK default glob (**/*.cs) would otherwise pull the test project's sources into the app. -->
    <Compile Remove="AudioStreamer.Tests\**" />
    <EmbeddedResource Remove="AudioStreamer.Tests\**" />
    <None Remove="AudioStreamer.Tests\**" />
    <!-- Let the test project see internal types (WireProtocol, codec seam). -->
    <InternalsVisibleTo Include="AudioStreamer.Tests" />
  </ItemGroup>
```

- [ ] **Step 3: Write a trivial smoke test**

Create `AudioStreamer.Tests/SmokeTests.cs`:

```csharp
using Xunit;

namespace AudioStreamer.Tests
{
    public class SmokeTests
    {
        [Fact]
        public void TestHarnessRuns() => Assert.True(true);
    }
}
```

- [ ] **Step 4: Verify the app still builds and the test runs**

Run: `dotnet build`
Expected: main app builds, 0 errors (the test folder is excluded).

Run: `dotnet test AudioStreamer.Tests`
Expected: 1 test, PASS.

- [ ] **Step 5: Commit**

```bash
git add AudioStreamer.Tests/AudioStreamer.Tests.csproj AudioStreamer.Tests/SmokeTests.cs AudioStreamer.csproj
git commit -m "test: add xUnit test project with InternalsVisibleTo"
```

---

### Task 2: WireProtocol — codec header byte

Adds the `CodecId` enum and a codec byte between the format code and the sequence number, shifting the sequence offset and total header size.

**Files:**
- Modify: `Streaming/WireProtocol.cs`
- Test: `AudioStreamer.Tests/WireProtocolTests.cs`

**Interfaces:**
- Produces: `public enum CodecId : byte { Pcm = 0, Opus = 1 }`; `WireProtocol.CodecByteOffset = 1`, `SequenceByteOffset = 2`, `HeaderBytes = 3`; `WireProtocol.WriteCodec(byte[], CodecId)`, `WireProtocol.ReadCodec(byte[]) : CodecId`.

- [ ] **Step 1: Write the failing test**

Create `AudioStreamer.Tests/WireProtocolTests.cs`:

```csharp
using Xunit;

namespace AudioStreamer.Tests
{
    public class WireProtocolTests
    {
        [Fact]
        public void Offsets_AreDistinctAndHeaderIsThree()
        {
            Assert.Equal(0, WireProtocol.FormatHeaderBytes - 1); // format code at 0
            Assert.Equal(1, WireProtocol.CodecByteOffset);
            Assert.Equal(2, WireProtocol.SequenceByteOffset);
            Assert.Equal(3, WireProtocol.HeaderBytes);
        }

        [Fact]
        public void FormatCode_Codec_Sequence_RoundTripIndependently()
        {
            var buf = new byte[WireProtocol.HeaderBytes + 4];
            WireProtocol.WriteFormatCode(buf, 42);
            WireProtocol.WriteCodec(buf, CodecId.Opus);
            buf[WireProtocol.SequenceByteOffset] = 200;

            Assert.Equal(42, WireProtocol.ReadFormatCode(buf));
            Assert.Equal(CodecId.Opus, WireProtocol.ReadCodec(buf));
            Assert.Equal(200, buf[WireProtocol.SequenceByteOffset]);
        }

        [Fact]
        public void ReadCodec_DefaultsToPcmForZero()
        {
            var buf = new byte[WireProtocol.HeaderBytes];
            Assert.Equal(CodecId.Pcm, WireProtocol.ReadCodec(buf));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AudioStreamer.Tests --filter WireProtocolTests`
Expected: FAIL — `CodecId`, `CodecByteOffset`, `WriteCodec`/`ReadCodec` don't exist; `SequenceByteOffset`/`HeaderBytes` are 1/2.

- [ ] **Step 3: Implement the wire changes**

Replace the body of `Streaming/WireProtocol.cs` with:

```csharp
namespace AudioStreamer
{
    /// <summary>Codec of a datagram's payload. Public + top-level so the persisted Config.Codec can expose it.</summary>
    public enum CodecId : byte { Pcm = 0, Opus = 1 }

    /// <summary>
    /// On-the-wire UDP framing shared by sender and receiver. Every datagram is a 3-byte header
    /// (byte 0: format code — an index into AudioFormats.Formats; byte 1: codec id; byte 2: wrapping
    /// per-datagram sequence number) followed by the payload (raw PCM, or one Opus frame).
    /// </summary>
    internal static class WireProtocol
    {
        public const int HeaderBytes = 3;          // total header (payload starts here on both sides)
        public const int FormatHeaderBytes = 1;    // byte 0: format code (index into AudioFormats.Formats)
        public const int CodecByteOffset = 1;      // byte 1: codec id
        public const int SequenceByteOffset = 2;   // byte 2: wrapping per-datagram sequence number

        // Keep each datagram inside a 1500-byte Ethernet MTU (minus IP/UDP/header, with VPN/PPPoE headroom)
        // so IP never fragments the audio. PCM is sliced on whole-frame boundaries by the sender.
        public const int MaxUdpAudioBytes = 1440;
        public const int SocketBufferBytes = 1 << 20; // 1 MiB
        public const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

        public static void WriteFormatCode(byte[] dest, byte code) => dest[0] = code;
        public static byte ReadFormatCode(byte[] src) => src[0];

        public static void WriteCodec(byte[] dest, CodecId c) => dest[CodecByteOffset] = (byte)c;
        public static CodecId ReadCodec(byte[] src) => (CodecId)src[CodecByteOffset];
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test AudioStreamer.Tests --filter WireProtocolTests`
Expected: PASS (3 tests).

Note: the app **still builds** after this task. Every caller uses the named constants (`HeaderBytes`, `SequenceByteOffset`, `MaxUdpAudioBytes`, `WriteFormatCode`, …) and this rewrite removes none of them — changing a `const` value is not a compile break. Only the wire *behaviour* is incomplete until Tasks 7–8 wire in the codec byte (the codec byte is a zero-initialised `Pcm` in the interim). So `dotnet build` and `dotnet test` pass at every task; do not touch `SenderSession`/`ReceiverSession` here.

- [ ] **Step 5: Commit**

```bash
git add Streaming/WireProtocol.cs AudioStreamer.Tests/WireProtocolTests.cs
git commit -m "feat: add codec header byte to wire protocol"
```

---

### Task 3: ReorderBuffer — additive `onLost` hook (per skipped sequence)

Adds an optional delegate fired **once per genuinely-missing sequence** so the Opus decoder can conceal exactly one frame per lost frame (stateful-decode correctness). Additive: existing call sites and the PCM path are unchanged.

**Files:**
- Modify: `Streaming/ReorderBuffer.cs`
- Test: `AudioStreamer.Tests/ReorderBufferTests.cs`

**Interfaces:**
- Produces: `new ReorderBuffer(int maxDepth, Action<byte[],int,int> emit, Action? onLost = null)`. `onLost` fires once per skipped sequence when the window overflows.

- [ ] **Step 1: Write the failing test**

Create `AudioStreamer.Tests/ReorderBufferTests.cs`:

```csharp
using System.Collections.Generic;
using Xunit;

namespace AudioStreamer.Tests
{
    public class ReorderBufferTests
    {
        private static byte[] One(byte v) => new[] { v };

        [Fact]
        public void InOrder_EmitsStraightThrough_NoLoss()
        {
            var emitted = new List<byte>();
            int lost = 0;
            var rb = new ReorderBuffer(4, (b, o, c) => emitted.Add(b[o]), () => lost++);
            for (byte s = 0; s < 5; s++) rb.Add(s, One(s), 0, 1);
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, emitted);
            Assert.Equal(0, lost);
        }

        [Fact]
        public void SingleGap_FiresOnLostOncePerMissingSequence()
        {
            var emitted = new List<byte>();
            int lost = 0;
            var rb = new ReorderBuffer(2, (b, o, c) => emitted.Add(b[o]), () => lost++);
            rb.Add(0, One(0), 0, 1);          // in order
            rb.Add(2, One(2), 0, 1);          // 1 missing, held: {2}
            rb.Add(3, One(3), 0, 1);          // held {2,3} >= maxDepth(2) -> give up on 1
            Assert.Equal(1, lost);            // exactly one skip
            Assert.Equal(new byte[] { 0, 2, 3 }, emitted);
        }

        [Fact]
        public void MultiGap_FiresOnLostPerSkippedSequence()
        {
            var emitted = new List<byte>();
            int lost = 0;
            var rb = new ReorderBuffer(2, (b, o, c) => emitted.Add(b[o]), () => lost++);
            rb.Add(0, One(0), 0, 1);          // in order
            rb.Add(3, One(3), 0, 1);          // 1,2 missing; held {3}
            rb.Add(4, One(4), 0, 1);          // held {3,4} >= 2 -> skip 1, then skip 2, emit 3,4
            Assert.Equal(2, lost);            // two skipped sequences -> two onLost calls
            Assert.Equal(new byte[] { 0, 3, 4 }, emitted);
        }

        [Fact]
        public void NullOnLost_IsAllowed_PcmCallSiteUnchanged()
        {
            var emitted = new List<byte>();
            var rb = new ReorderBuffer(2, (b, o, c) => emitted.Add(b[o]));  // no onLost
            rb.Add(0, One(0), 0, 1);
            rb.Add(2, One(2), 0, 1);
            rb.Add(3, One(3), 0, 1);          // skip 1 with null onLost must not throw
            Assert.Equal(new byte[] { 0, 2, 3 }, emitted);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AudioStreamer.Tests --filter ReorderBufferTests`
Expected: FAIL — the 3-arg constructor doesn't exist; `onLost` isn't fired.

- [ ] **Step 3: Implement the additive hook**

In `Streaming/ReorderBuffer.cs`: add the field and constructor param, and fire `onLost` at each skip.

Change the fields/constructor:

```csharp
        private readonly int maxDepth;
        private readonly Action<byte[], int, int> emit;   // (buffer, offset, count) — feeds the audio buffer in order
        private readonly Action? onLost;                  // fired once per genuinely-missing sequence (optional)
        private long nextSeq = -1;                         // unwrapped sequence we still owe the audio buffer
        private readonly SortedDictionary<long, byte[]> held = new();

        public ReorderBuffer(int maxDepth, Action<byte[], int, int> emit, Action? onLost = null)
        {
            this.maxDepth = maxDepth;
            this.emit = emit;
            this.onLost = onLost;
        }
```

Change the give-up loop in `Add` so each skip fires `onLost` before advancing:

```csharp
            EmitContiguous();
            // Too many datagrams piled up behind a missing one: give up on it and skip ahead so audio keeps
            // flowing. Fire onLost once per skipped sequence (Opus decode is stateful — the decoder must
            // advance by exactly the number of lost frames), then emit anything now contiguous.
            while (held.Count >= maxDepth)
            {
                onLost?.Invoke();
                nextSeq++;
                EmitContiguous();
            }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test AudioStreamer.Tests --filter ReorderBufferTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Streaming/ReorderBuffer.cs AudioStreamer.Tests/ReorderBufferTests.cs
git commit -m "feat: add per-skip onLost hook to ReorderBuffer"
```

---

### Task 4: Codec seam — interfaces, PCM codec, factory (PCM only)

Introduces the encoder/decoder seam and the identity PCM implementation. Opus is added in Task 5. This keeps the PCM path mechanically identical to today's behaviour.

**Files:**
- Create: `Streaming/IAudioCodec.cs`
- Create: `Streaming/PcmCodec.cs`
- Create: `Streaming/CodecFactory.cs`
- Test: `AudioStreamer.Tests/PcmCodecTests.cs`

**Interfaces:**
- Produces:
  - `internal interface IAudioEncoder : IDisposable { void Push(byte[] pcm, int offset, int count, Action<byte[], int, int> emit); }` — `emit(payload, offset, length)`.
  - `internal interface IAudioDecoder : IDisposable { int Decode(byte[] packet, int offset, int count, byte[] pcmOut); int Conceal(byte[] pcmOut); }`
  - `internal static class CodecFactory { IAudioEncoder CreateEncoder(CodecId, WaveFormat, int opusFrameSamples, Action<string> log); IAudioDecoder CreateDecoder(CodecId, WaveFormat, Action<string> log); }`
- Consumes: `NAudio.Wave.WaveFormat`, `WireProtocol.MaxUdpAudioBytes`.

- [ ] **Step 1: Write the failing test**

Create `AudioStreamer.Tests/PcmCodecTests.cs`:

```csharp
using System.Collections.Generic;
using NAudio.Wave;
using Xunit;

namespace AudioStreamer.Tests
{
    public class PcmCodecTests
    {
        private static readonly WaveFormat Fmt = AudioFormats.ToWaveFormat(48000, 16, 2); // BlockAlign = 4

        [Fact]
        public void Encoder_SlicesOnWholeFrames_UnderMtu()
        {
            using var enc = CodecFactory.CreateEncoder(CodecId.Pcm, Fmt, 0, _ => { });
            var pcm = new byte[WireProtocol.MaxUdpAudioBytes + 400]; // one full slice + a remainder
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)i;
            var slices = new List<int>();
            enc.Push(pcm, 0, pcm.Length, (payload, off, len) => slices.Add(len));

            int maxChunk = (WireProtocol.MaxUdpAudioBytes / Fmt.BlockAlign) * Fmt.BlockAlign; // 1440
            Assert.Equal(new[] { maxChunk, pcm.Length - maxChunk }, slices);
            Assert.All(slices, len => Assert.Equal(0, len % Fmt.BlockAlign)); // frame-aligned
        }

        [Fact]
        public void Decoder_IsBitExactIdentity()
        {
            using var dec = CodecFactory.CreateDecoder(CodecId.Pcm, Fmt, _ => { });
            var packet = new byte[3 + 8];
            for (int i = 0; i < packet.Length; i++) packet[i] = (byte)(i * 7);
            var outBuf = new byte[16];
            int n = dec.Decode(packet, 3, 8, outBuf);
            Assert.Equal(8, n);
            for (int i = 0; i < 8; i++) Assert.Equal(packet[3 + i], outBuf[i]);
        }

        [Fact]
        public void Decoder_ConcealEmitsNothing()
        {
            using var dec = CodecFactory.CreateDecoder(CodecId.Pcm, Fmt, _ => { });
            Assert.Equal(0, dec.Conceal(new byte[16]));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AudioStreamer.Tests --filter PcmCodecTests`
Expected: FAIL — `IAudioEncoder`/`CodecFactory`/`PcmCodec` don't exist.

- [ ] **Step 3: Create the interfaces**

Create `Streaming/IAudioCodec.cs`:

```csharp
using System;

namespace AudioStreamer
{
    /// <summary>Turns captured PCM into wire payloads. PCM: MTU slicing. Opus: accumulate a frame, encode.</summary>
    internal interface IAudioEncoder : IDisposable
    {
        // Append captured PCM; invoke emit(payload, offset, length) once per complete output frame (zero or
        // more per call). The offset lets PCM emit straight from the capture buffer with no copy; Opus passes 0.
        void Push(byte[] pcm, int offset, int count, Action<byte[], int, int> emit);
    }

    /// <summary>Turns a wire payload back into PCM. Conceal() fills a dropped frame (Opus PLC; PCM emits nothing).</summary>
    internal interface IAudioDecoder : IDisposable
    {
        int Decode(byte[] packet, int offset, int count, byte[] pcmOut);  // returns PCM bytes written
        int Conceal(byte[] pcmOut);                                       // returns PCM bytes written (0 for PCM)
    }
}
```

- [ ] **Step 4: Create the PCM codec**

The encoder emits each whole-frame slice **directly from the capture buffer** via `emit(pcm, offset, len)`
— no copy (the sender's emit callback copies straight into `sendBuffer`, exactly as today). The decoder is
a memcpy identity; `Conceal` emits nothing (today's skip-the-gap behaviour). The PCM path is therefore
allocation-free and copy-count-identical to the current code.

Create `Streaming/PcmCodec.cs`:

```csharp
using System;

namespace AudioStreamer
{
    internal sealed class PcmEncoderAdapter : IAudioEncoder
    {
        private readonly int maxChunk;

        public PcmEncoderAdapter(int blockAlign)
        {
            maxChunk = Math.Max(blockAlign, (WireProtocol.MaxUdpAudioBytes / blockAlign) * blockAlign);
        }

        public void Push(byte[] pcm, int offset, int count, Action<byte[], int, int> emit)
        {
            for (int pos = 0; pos < count; pos += maxChunk)
            {
                int chunk = Math.Min(maxChunk, count - pos);
                emit(pcm, offset + pos, chunk);   // straight from the capture buffer — no copy
            }
        }

        public void Dispose() { }
    }

    internal sealed class PcmDecoderAdapter : IAudioDecoder
    {
        public int Decode(byte[] packet, int offset, int count, byte[] pcmOut)
        {
            Buffer.BlockCopy(packet, offset, pcmOut, 0, count);
            return count;
        }

        public int Conceal(byte[] pcmOut) => 0;   // today's "skip the gap" behaviour

        public void Dispose() { }
    }
}
```

- [ ] **Step 5: Create the factory (PCM branch only)**

Create `Streaming/CodecFactory.cs`:

```csharp
using System;
using NAudio.Wave;

namespace AudioStreamer
{
    /// <summary>Maps a CodecId + PCM format to an encoder/decoder. Opus branch added in Task 5.</summary>
    internal static class CodecFactory
    {
        public static IAudioEncoder CreateEncoder(CodecId codec, WaveFormat format, int opusFrameSamples, Action<string> log)
        {
            return new PcmEncoderAdapter(format.BlockAlign);
        }

        public static IAudioDecoder CreateDecoder(CodecId codec, WaveFormat format, Action<string> log)
        {
            return new PcmDecoderAdapter();
        }
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test AudioStreamer.Tests --filter PcmCodecTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add Streaming/IAudioCodec.cs Streaming/PcmCodec.cs Streaming/CodecFactory.cs AudioStreamer.Tests/PcmCodecTests.cs
git commit -m "feat: add codec seam and identity PCM codec"
```

---

### Task 5: OpusSharp package + Opus adapters

Adds the OpusSharp dependency and the Opus encoder/decoder adapters, and wires them into the factory.

**Files:**
- Modify: `AudioStreamer.csproj` (add `OpusSharp.Core` + win-x64 native)
- Create: `Streaming/OpusCodec.cs`
- Modify: `Streaming/CodecFactory.cs`
- Test: `AudioStreamer.Tests/OpusCodecTests.cs`

**Interfaces:**
- Produces: `OpusEncoderAdapter`, `OpusDecoderAdapter` (both `internal`, implement the Task 4 interfaces). `OpusDecoderAdapter` tracks the last decoded frame length so `Conceal` synthesises the right number of samples.
- Consumes: `OpusSharp.Core.OpusEncoder/OpusDecoder`, `OpusSharp.Core.Extensions` setters.

- [ ] **Step 1: Add the package references**

In `AudioStreamer.csproj`, in the `<ItemGroup>` with `NAudio.Wasapi`, add:

```xml
    <!-- Native libopus binding. Core = managed wrapper; the win-x64 native package ships opus.dll.
         Windows-x64-only app, so we skip the all-platforms meta-package. -->
    <PackageReference Include="OpusSharp.Core" Version="1.6.6" />
    <PackageReference Include="OpusSharp.Natives" Version="1.6.1.3" />
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
```

Run: `dotnet build`
Expected: restore succeeds and the app still builds (the new adapter files in Step 4 compile against the just-added package; the Opus path isn't wired into the sessions until Tasks 7–8). If `OpusSharp.Natives` pulls all platforms, that is acceptable for now and trimmed at publish via the win-x64 RID.

- [ ] **Step 2: Write the failing test**

Create `AudioStreamer.Tests/OpusCodecTests.cs`:

```csharp
using System;
using NAudio.Wave;
using Xunit;

namespace AudioStreamer.Tests
{
    public class OpusCodecTests
    {
        private const int Rate = 48000, Channels = 2, FrameSamples = 480; // 10 ms
        private static readonly WaveFormat Fmt = AudioFormats.ToWaveFormat(Rate, 16, Channels);
        private const int FrameBytes = FrameSamples * Channels * 2;

        private static byte[] SineFrame(double freq)
        {
            var buf = new byte[FrameBytes];
            for (int i = 0; i < FrameSamples; i++)
            {
                short v = (short)(Math.Sin(2 * Math.PI * freq * i / Rate) * 20000);
                for (int ch = 0; ch < Channels; ch++)
                {
                    int idx = (i * Channels + ch) * 2;
                    buf[idx] = (byte)(v & 0xFF);
                    buf[idx + 1] = (byte)((v >> 8) & 0xFF);
                }
            }
            return buf;
        }

        private static double Rms(byte[] b, int count)
        {
            double sum = 0; int n = count / 2;
            for (int i = 0; i < n; i++) { short s = (short)(b[i * 2] | (b[i * 2 + 1] << 8)); sum += (double)s * s; }
            return Math.Sqrt(sum / n);
        }

        [Fact]
        public void RoundTrip_PreservesFrameLengthAndRoughLevel()
        {
            using var enc = CodecFactory.CreateEncoder(CodecId.Opus, Fmt, FrameSamples, _ => { });
            using var dec = CodecFactory.CreateDecoder(CodecId.Opus, Fmt, _ => { });
            var input = SineFrame(440);
            var pcmOut = new byte[FrameBytes * 2];

            byte[]? packet = null; int packetLen = 0;
            enc.Push(input, 0, input.Length, (p, off, len) => { packet = new byte[len]; Buffer.BlockCopy(p, off, packet, 0, len); packetLen = len; });
            Assert.NotNull(packet);
            Assert.True(packetLen > 0 && packetLen < FrameBytes); // actually compressed

            int decodedBytes = dec.Decode(packet!, 0, packetLen, pcmOut);
            Assert.Equal(FrameBytes, decodedBytes);               // same sample count out
            double inRms = Rms(input, FrameBytes), outRms = Rms(pcmOut, decodedBytes);
            Assert.InRange(outRms, inRms * 0.5, inRms * 1.5);     // roughly the same level (lossy)
        }

        [Fact]
        public void Push_AccumulatesPartialBuffersIntoWholeFrames()
        {
            using var enc = CodecFactory.CreateEncoder(CodecId.Opus, Fmt, FrameSamples, _ => { });
            var half = new byte[FrameBytes / 2];
            int emitted = 0;
            enc.Push(half, 0, half.Length, (p, off, len) => emitted++);
            Assert.Equal(0, emitted);                              // half a frame -> nothing yet
            enc.Push(half, 0, half.Length, (p, off, len) => emitted++);
            Assert.Equal(1, emitted);                              // second half completes one frame
        }

        [Fact]
        public void Conceal_UsesLearnedFrameLength()
        {
            using var enc = CodecFactory.CreateEncoder(CodecId.Opus, Fmt, FrameSamples, _ => { });
            using var dec = CodecFactory.CreateDecoder(CodecId.Opus, Fmt, _ => { });
            var pcmOut = new byte[FrameBytes * 2];
            byte[]? packet = null; int packetLen = 0;
            enc.Push(SineFrame(440), 0, FrameBytes, (p, off, len) => { packet = new byte[len]; Buffer.BlockCopy(p, off, packet, 0, len); packetLen = len; });
            dec.Decode(packet!, 0, packetLen, pcmOut);            // decoder learns 480-sample frames
            int concealed = dec.Conceal(pcmOut);
            Assert.Equal(FrameBytes, concealed);                  // PLC synthesises one full frame
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test AudioStreamer.Tests --filter OpusCodecTests`
Expected: FAIL — the Opus adapters and factory branch don't exist.

- [ ] **Step 4: Implement the Opus adapters**

Create `Streaming/OpusCodec.cs`:

```csharp
using System;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;

namespace AudioStreamer
{
    /// <summary>
    /// Opus encoder adapter: accumulates arbitrary WASAPI buffers into whole frames of frameSamples/channel,
    /// then encodes one datagram per frame. All buffers are allocated once — no per-callback allocation.
    /// </summary>
    internal sealed class OpusEncoderAdapter : IAudioEncoder
    {
        private const int Bitrate = 192_000;
        private const int Complexity = 10;

        private readonly OpusEncoder encoder;
        private readonly int frameSamples;
        private readonly byte[] accumulator;   // frameSamples * channels * 2 bytes
        private readonly byte[] encodeBuffer;  // encoded output scratch
        private int accumFill;                 // bytes currently in the accumulator

        public OpusEncoderAdapter(int sampleRate, int channels, int frameSamples)
        {
            this.frameSamples = frameSamples;
            encoder = new OpusEncoder(sampleRate, channels, OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
            encoder.SetVbr(true);
            encoder.SetBitRate(Bitrate);          // NOTE capital R
            encoder.SetComplexity(Complexity);
            accumulator = new byte[frameSamples * channels * 2];
            encodeBuffer = new byte[WireProtocol.MaxUdpAudioBytes]; // one Opus frame is far smaller
        }

        public void Push(byte[] pcm, int offset, int count, Action<byte[], int, int> emit)
        {
            int pos = offset, end = offset + count;
            while (pos < end)
            {
                int take = Math.Min(accumulator.Length - accumFill, end - pos);
                Buffer.BlockCopy(pcm, pos, accumulator, accumFill, take);
                accumFill += take;
                pos += take;
                if (accumFill == accumulator.Length)
                {
                    int len = encoder.Encode(accumulator, frameSamples, encodeBuffer, encodeBuffer.Length);
                    emit(encodeBuffer, 0, len);   // Opus payload starts at 0 of its own encode buffer
                    accumFill = 0;
                }
            }
        }

        public void Dispose() => encoder.Dispose();
    }

    /// <summary>
    /// Opus decoder adapter. Learns the stream's frame length from the first successful decode and reuses it
    /// for concealment (PLC needs a sample count, and the sender's frame size is not signalled but is constant).
    /// </summary>
    internal sealed class OpusDecoderAdapter : IAudioDecoder
    {
        private readonly OpusDecoder decoder;
        private readonly int channels;
        private readonly int maxFrameSamples;   // decode-output capacity, samples/channel (20 ms)
        private readonly byte[] inScratch;      // payload copied to offset 0 (OpusSharp Decode has no offset arg)
        private int lastFrameSamples;

        public OpusDecoderAdapter(int sampleRate, int channels)
        {
            this.channels = channels;
            decoder = new OpusDecoder(sampleRate, channels);
            maxFrameSamples = sampleRate / 50;              // 20 ms = largest supported frame
            lastFrameSamples = sampleRate / 100;            // 10 ms default until the first decode
            inScratch = new byte[WireProtocol.MaxUdpAudioBytes];
        }

        public int Decode(byte[] packet, int offset, int count, byte[] pcmOut)
        {
            Buffer.BlockCopy(packet, offset, inScratch, 0, count);
            int samples = decoder.Decode(inScratch, count, pcmOut, maxFrameSamples, false);
            lastFrameSamples = samples;
            return samples * channels * 2;
        }

        public int Conceal(byte[] pcmOut)
        {
            // Null input packet => Opus PLC; frame size must match the stream (learned above).
            int samples = decoder.Decode(null, 0, pcmOut, lastFrameSamples, false);
            return samples * channels * 2;
        }

        public void Dispose() => decoder.Dispose();
    }
}
```

- [ ] **Step 5: Wire the Opus branch into the factory**

In `Streaming/CodecFactory.cs`, replace the two method bodies:

```csharp
        public static IAudioEncoder CreateEncoder(CodecId codec, WaveFormat format, int opusFrameSamples, Action<string> log)
        {
            if (codec == CodecId.Opus)
            {
                log($"Opus encoder: {format.SampleRate}Hz {format.Channels}ch, {opusFrameSamples} samples/frame");
                return new OpusEncoderAdapter(format.SampleRate, format.Channels, opusFrameSamples);
            }
            return new PcmEncoderAdapter(format.BlockAlign);
        }

        public static IAudioDecoder CreateDecoder(CodecId codec, WaveFormat format, Action<string> log)
        {
            if (codec == CodecId.Opus)
            {
                log($"Opus decoder: {format.SampleRate}Hz {format.Channels}ch");
                return new OpusDecoderAdapter(format.SampleRate, format.Channels);
            }
            return new PcmDecoderAdapter();
        }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test AudioStreamer.Tests --filter OpusCodecTests`
Expected: PASS (3 tests). If `Conceal` returns 0 the first time, confirm `Decode(null, ...)` is the PLC call for your OpusSharp version; the `Decode` null-packet overload is the documented PLC path.

- [ ] **Step 7: Commit**

```bash
git add AudioStreamer.csproj Streaming/OpusCodec.cs Streaming/CodecFactory.cs AudioStreamer.Tests/OpusCodecTests.cs
git commit -m "feat: add OpusSharp-backed Opus encoder/decoder adapters"
```

---

### Task 6: Config — `Codec` + `OpusFrameSamples` (+ clamp helper)

Adds the two persisted settings and a pure clamp helper for a hand-edited frame-size value.

**Files:**
- Modify: `Core/AudioStreamerLogic.cs`
- Test: `AudioStreamer.Tests/OpusFrameSamplesTests.cs`

**Interfaces:**
- Produces: `Config.Codec` (`CodecId`, default `Pcm`), `Config.OpusFrameSamples` (`int`, default `480`); `AudioStreamerLogic.ClampOpusFrameSamples(int) : int` returning the nearest of {120,240,480,960}.

- [ ] **Step 1: Write the failing test**

Create `AudioStreamer.Tests/OpusFrameSamplesTests.cs`:

```csharp
using Xunit;

namespace AudioStreamer.Tests
{
    public class OpusFrameSamplesTests
    {
        [Theory]
        [InlineData(120, 120)]
        [InlineData(240, 240)]
        [InlineData(480, 480)]
        [InlineData(960, 960)]
        [InlineData(0, 120)]      // below range -> smallest
        [InlineData(300, 240)]    // nearest
        [InlineData(700, 480)]    // nearest (700-480=220 < 960-700=260)
        [InlineData(5000, 960)]   // above range -> largest
        public void Clamp_SnapsToNearestValidOption(int input, int expected)
        {
            Assert.Equal(expected, AudioStreamerLogic.ClampOpusFrameSamples(input));
        }

        [Fact]
        public void Config_DefaultsAreOpusFriendly()
        {
            var c = new AudioStreamerLogic.Config();
            Assert.Equal(CodecId.Pcm, c.Codec);
            Assert.Equal(480, c.OpusFrameSamples);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test AudioStreamer.Tests --filter OpusFrameSamplesTests`
Expected: FAIL — `Config.Codec`/`OpusFrameSamples`/`ClampOpusFrameSamples` don't exist.

- [ ] **Step 3: Implement the config fields and clamp**

In `Core/AudioStreamerLogic.cs`, add to the `Config` class (after `Channels`):

```csharp
            public CodecId Codec { get; set; } = CodecId.Pcm;
            // Opus frame size in samples/channel at 48 kHz (480 = 10 ms). Valid: 120/240/480/960 (2.5/5/10/20 ms).
            // Stored as samples (libopus's frame_size arg) to avoid a fractional-ms field for 2.5 ms.
            public int OpusFrameSamples { get; set; } = 480;
```

Add the static helper to `AudioStreamerLogic` (after `SaveConfig`):

```csharp
        public static readonly int[] OpusFrameSizes = { 120, 240, 480, 960 };   // 2.5/5/10/20 ms @ 48 kHz

        /// <summary>Snaps a (possibly hand-edited) frame size to the nearest valid option.</summary>
        public static int ClampOpusFrameSamples(int value)
        {
            int best = OpusFrameSizes[0];
            foreach (int v in OpusFrameSizes)
                if (Math.Abs(v - value) < Math.Abs(best - value))
                    best = v;
            return best;
        }
```

Add `using System;` at the top of the file if not present (it uses `Math`). The file already has `using System.IO;` / `System.Text.Json;`; add `using System;`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test AudioStreamer.Tests --filter OpusFrameSamplesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/AudioStreamerLogic.cs AudioStreamer.Tests/OpusFrameSamplesTests.cs
git commit -m "feat: add Codec and OpusFrameSamples config fields"
```

---

### Task 7: Sender integration

Routes capture through the codec seam, writes the codec byte, and derives an effective 48 kHz/16-bit/stereo format for Opus without clobbering stored config. Restores the app to a buildable state.

**Files:**
- Modify: `Streaming/SenderSession.cs`
- Verify: build + manual (no unit test — WASAPI capture).

**Interfaces:**
- Consumes: `CodecFactory.CreateEncoder`, `WireProtocol.WriteCodec/HeaderBytes/SequenceByteOffset`, `AudioStreamerLogic.ClampOpusFrameSamples`, `AudioFormats.ToWaveFormat/ToCode/ResolveCaptureChannels`.

- [ ] **Step 1: Derive the effective format and build the encoder inside `senderLock`**

In `StartCapture()` (`Streaming/SenderSession.cs`), replace the format/channel resolution and buffer setup. The current code computes `channels`, builds `capture.WaveFormat`, `blockAlign`, `maxChunk`, `sendBuffer`, writes the format code, then subscribes `DataAvailable`.

Replace the region from `int channels = ...` through the `WriteFormatCode` line with:

```csharp
            // Effective capture format. Opus is fixed to 48 kHz/16-bit/stereo; PCM uses the configured/Auto format.
            // Do NOT write these back into config — switching to PCM must restore the user's stored values.
            bool opus = config.Codec == CodecId.Opus;
            int effRate = opus ? 48000 : config.SampleRate;
            int effBits = opus ? 16 : config.BitsPerSample;
            int effChannels = opus ? 2 : AudioFormats.ResolveCaptureChannels(config.Channels, nativeChannels);
            int frameSamples = AudioStreamerLogic.ClampOpusFrameSamples(config.OpusFrameSamples);
```

Then, inside the existing `lock (senderLock)` block, set the capture format from the effective values and build the encoder there:

```csharp
                capture = new TweakedWasapiLoopbackCapture(config.SenderAudioBufferMillisecondsLength)
                {
                    WaveFormat = AudioFormats.ToWaveFormat(effRate, effBits, effChannels)
                };

                var format = capture.WaveFormat;
                // `encoder` is a field (declared in Step 3) so StopCapture can dispose it. Dispose any prior
                // encoder first so a device-flip rebuild (RestartCapture) doesn't leak the previous one.
                encoder?.Dispose();
                encoder = CodecFactory.CreateEncoder(config.Codec, format, frameSamples, logLine);

                // Header: format code + codec are constant for the session — write once. Sequence is per datagram.
                byte[] sendBuffer = new byte[WireProtocol.HeaderBytes + WireProtocol.MaxUdpAudioBytes];
                WireProtocol.WriteFormatCode(sendBuffer, AudioFormats.ToCode(effRate, effBits, effChannels));
                WireProtocol.WriteCodec(sendBuffer, config.Codec);
```

(Delete the old `blockAlign`/`maxChunk` locals — slicing now lives in `PcmEncoderAdapter`. Keep the `sendLogTimer`/`sentPackets`/`sentBytes`/`sequence` locals.)

- [ ] **Step 2: Route `DataAvailable` through the encoder**

Replace the `capture.DataAvailable += (sender, e) => { ... }` body with:

```csharp
                capture.DataAvailable += (sender, e) =>
                {
                    var socket = udpClient;   // read per callback so a Stop()/restart can't send on a closed socket
                    if (socket == null)
                        return;
                    try
                    {
                        encoder.Push(e.Buffer, 0, e.BytesRecorded, (payload, off, len) =>
                        {
                            var s = udpClient;
                            if (s == null) return;
                            sendBuffer[WireProtocol.SequenceByteOffset] = sequence++;
                            Buffer.BlockCopy(payload, off, sendBuffer, WireProtocol.HeaderBytes, len);
                            s.Send(sendBuffer, len + WireProtocol.HeaderBytes);
                            sentPackets++;
                            sentBytes += len + WireProtocol.HeaderBytes;
                        });

                        if (sendLogTimer.ElapsedMilliseconds >= 1000)
                        {
                            report(DiagnosticsSnapshot.ForSender(sentPackets, sentBytes / 1024));
                            sentPackets = 0; sentBytes = 0; sendLogTimer.Restart();
                        }
                    }
                    catch (Exception ex)
                    {
                        logLine("Error sending audio: " + ex.Message);
                    }
                };
```

- [ ] **Step 3: Declare the encoder field and dispose it on teardown**

The encoder must outlive each `DataAvailable` callback and be disposable on Stop, so it is a **field**,
not a local. Add near `waveSource`:

```csharp
        private IAudioEncoder? encoder;
```

Step 1 already assigns this field inside the `senderLock` block (and disposes any prior encoder before
reassigning, covering the device-flip rebuild). In `StopCapture()`, inside the `senderLock` block after
`capture.Dispose()`, add:

```csharp
                encoder?.Dispose();
                encoder = null;
```

(A rebuild via `RestartCapture` re-enters `StartCapture`, which disposes-then-replaces the field; the old
capture's thread has already ended, so disposing its encoder is safe — a one-frame accumulator
discontinuity at resume, per the spec.)

- [ ] **Step 4: Build and verify**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 5: Manual verification (PCM regression)**

Run the app as Sender with `Codec = Pcm` (default) against a Receiver on another machine (or localhost with two instances). Confirm audio plays and diagnostics show `pkts/s ≈ 200`, unchanged from before.

- [ ] **Step 6: Commit**

```bash
git add Streaming/SenderSession.cs
git commit -m "feat: route sender capture through the codec seam"
```

---

### Task 8: Receiver integration

Reads the codec byte, builds the matching decoder, decodes inside the reorder callback via `EmitSamples`, uses a fixed bounded reorder window for Opus, and re-checks the codec per datagram.

**Files:**
- Modify: `Streaming/ReceiverSession.cs`
- Verify: build + manual.

**Interfaces:**
- Consumes: `CodecFactory.CreateDecoder`, `WireProtocol.ReadCodec/ReadFormatCode/HeaderBytes/SequenceByteOffset`, `IAudioDecoder`, `ReorderBuffer(maxDepth, emit, onLost)`.

- [ ] **Step 1: Add codec state + the Opus reorder-window constant**

Near the other reorder constants (top of `ReceiverSession`), add:

```csharp
        // Opus datagrams arrive at the frame rate (100/s at 10 ms), not the PCM MTU cadence, so the PCM
        // byte-rate window formula mis-sizes them (8 packets = 160 ms at 20 ms frames, past the drift cap).
        // A fixed small count keeps give-up time bounded (10-80 ms across 2.5-20 ms frames), always < the cap.
        private const int OpusReorderWindowPackets = 4;
```

Add fields near `currentFormatCode`:

```csharp
        private CodecId currentCodec;
        private IAudioDecoder decoder = null!;   // built alongside the pipeline
        private byte[] pcmScratch = System.Array.Empty<byte>();   // decode/conceal output, sized for the largest frame
```

- [ ] **Step 2: Build the decoder + scratch in `BuildPipeline`, and read the codec in `InitializeReceiver`**

In `BuildPipeline(AudioFormats.Format fmt, byte code)`, it must also take/record the codec and build the decoder. Change its signature and body:

```csharp
        private void BuildPipeline(AudioFormats.Format fmt, byte code, CodecId codec)
        {
            currentFormatCode = code;
            currentCodec = codec;
            var waveFormat = AudioFormats.ToWaveFormat(fmt.SampleRate, fmt.BitDepth, fmt.Channels);
            bufferedWaveProvider = new BufferedWaveProvider(waveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(config.ReceiverAudioBufferMillisecondsLength),
                DiscardOnBufferOverflow = true
            };
            underrunMeter = new UnderrunCountingWaveProvider(bufferedWaveProvider);
            decoder?.Dispose();
            decoder = CodecFactory.CreateDecoder(codec, waveFormat, logLine);
            // Largest supported Opus frame (20 ms) worth of PCM, sized generously; PCM decode writes <= one datagram.
            int maxFrameBytes = waveFormat.SampleRate / 50 * waveFormat.Channels * (waveFormat.BitsPerSample / 8);
            pcmScratch = new byte[System.Math.Max(maxFrameBytes, WireProtocol.MaxUdpAudioBytes)];
            BuildAndPlayOutput();
        }
```

In `InitializeReceiver`, read the codec alongside the format code and pass it to `BuildPipeline`. Replace the `BuildPipeline(fmt, code);` call with:

```csharp
                        CodecId codec = WireProtocol.ReadCodec(receiveBuffer);
                        logLine($"Codec: {codec}");
                        BuildPipeline(fmt, code, codec);
```

- [ ] **Step 3: Decode inside the reorder callback and size the Opus window**

In `ReceiveAudio`, replace the reorder-window computation and `ReorderBuffer` construction:

```csharp
            int reorderWindow = currentCodec == CodecId.Opus
                ? OpusReorderWindowPackets
                : ComputeReorderWindow(bufferedWaveProvider.WaveFormat);
            logLine($"Reorder window: {reorderWindow} packets");
            var reorderBuffer = MakeReorderBuffer(reorderWindow);
```

Add a small factory method (so the initial and rebuild paths share the decode/conceal wiring). Add to the class:

```csharp
        private ReorderBuffer MakeReorderBuffer(int window) => new ReorderBuffer(
            window,
            emit: (buf, off, cnt) =>
            {
                int bytes = decoder.Decode(buf, off, cnt, pcmScratch);
                EmitSamples(pcmScratch, 0, bytes);
            },
            onLost: () =>
            {
                int bytes = decoder.Conceal(pcmScratch);   // Opus PLC; PCM returns 0
                if (bytes > 0) EmitSamples(pcmScratch, 0, bytes);
            });
```

- [ ] **Step 4: Re-check the codec per datagram on the format-change path**

In `ReceiveAudio`, the mid-stream branch currently triggers only on `code != currentFormatCode`. Extend it to also cover a codec change, and rebuild with the codec-aware window. **Replace from the existing `byte code = WireProtocol.ReadFormatCode(receiveBuffer);` line (the block below re-declares `code`, so do not leave the original declaration in place — replacing only the `if` would double-declare `code` → CS0128) through the end of that `if` block** with:

```csharp
                        byte code = WireProtocol.ReadFormatCode(receiveBuffer);
                        CodecId codec = WireProtocol.ReadCodec(receiveBuffer);
                        if (code != currentFormatCode || codec != currentCodec)
                        {
                            if (AudioFormats.FromCode(code) is not { } newFmt)
                            {
                                if (!loggedBadFormatMidStream)
                                {
                                    logLine($"Ignoring mid-stream datagram with unknown format code {code}.");
                                    loggedBadFormatMidStream = true;
                                }
                                continue;
                            }
                            logLine($"Wire format/codec changed to {newFmt.SampleRate}Hz {newFmt.BitDepth}bit {newFmt.Channels}ch {codec}; rebuilding.");
                            BuildPipeline(newFmt, code, codec);
                            reorderWindow = codec == CodecId.Opus
                                ? OpusReorderWindowPackets
                                : ComputeReorderWindow(bufferedWaveProvider.WaveFormat);
                            reorderBuffer = MakeReorderBuffer(reorderWindow);
                            logLine($"Reorder window: {reorderWindow} packets");
                        }
```

- [ ] **Step 5: Dispose the decoder in `Stop`**

In `Stop()`, after the socket teardown, add:

```csharp
            decoder?.Dispose();
            decoder = null!;
```

- [ ] **Step 6: Build and verify**

Run: `dotnet build`
Expected: 0 errors.

Run: `dotnet test AudioStreamer.Tests`
Expected: all prior tests still PASS (no receiver unit tests; this confirms nothing else regressed).

- [ ] **Step 7: Manual verification**

- PCM regression: Sender (PCM) ↔ Receiver — audio + diagnostics unchanged.
- Opus happy path: set Sender `Codec = Opus` in `config.json` (UI comes in Task 9), confirm the Receiver logs `Codec: Opus`, audio plays at correct pitch, `KB/s` drops to ~10–15% of PCM.
- Loss: on Wi-Fi (or with induced loss) confirm `lost/s` ticks and audio degrades gracefully rather than clicking.

- [ ] **Step 8: Commit**

```bash
git add Streaming/ReceiverSession.cs
git commit -m "feat: decode through the codec seam on the receiver"
```

---

### Task 9: UI — Codec + Opus frame-size dropdowns with gating

Adds the two Sender-panel selectors and the dynamic enable/disable (format dropdowns off when Opus; frame-size on only when Opus).

**Files:**
- Modify: `UI/MainWindow.xaml`
- Modify: `UI/MainWindow.xaml.cs`
- Verify: build + manual.

**Interfaces:**
- Consumes: `Config.Codec`, `Config.OpusFrameSamples`, `AudioStreamerLogic.OpusFrameSizes`, the existing `FormatOption`/`SelectValue`/`SelectedValue` plumbing.

- [ ] **Step 1: Add the ComboBoxes to XAML**

In `UI/MainWindow.xaml`, inside `<StackPanel x:Name="SenderPanel">`, before the `Sample Rate` block, add:

```xml
                <TextBlock Text="Codec" Margin="1"/>
                <ComboBox x:Name="CodecCombo" Width="200" Margin="1" HorizontalAlignment="Left"
                          SelectionChanged="CodecCombo_SelectionChanged"/>

                <TextBlock Text="Opus Frame Size" Margin="1"/>
                <ComboBox x:Name="OpusFrameCombo" Width="200" Margin="1" HorizontalAlignment="Left"/>
```

- [ ] **Step 2: Populate the new dropdowns**

In `PopulateFormatDropdowns()` (`MainWindow.xaml.cs`), add:

```csharp
            CodecCombo.ItemsSource = new[]
            {
                new FormatOption((int)CodecId.Pcm, "PCM (uncompressed)"),
                new FormatOption((int)CodecId.Opus, "Opus (compressed)")
            };
            OpusFrameCombo.ItemsSource = AudioStreamerLogic.OpusFrameSizes
                .Select(s => new FormatOption(s, $"{s * 1000.0 / 48000.0:0.#} ms")).ToList();
```

- [ ] **Step 3: Read/write the new settings**

In `UpdateConfigFromUI()`, add:

```csharp
            cfg.Codec = (CodecId)SelectedValue(CodecCombo, (int)cfg.Codec);
            cfg.OpusFrameSamples = SelectedValue(OpusFrameCombo, cfg.OpusFrameSamples);
```

In `PopulateUIFromConfig()`, add before `UpdateModePanels();`:

```csharp
            SelectValue(CodecCombo, (int)audioStreamerLogic.CurrentConfig.Codec, (int)CodecId.Pcm);
            SelectValue(OpusFrameCombo, audioStreamerLogic.CurrentConfig.OpusFrameSamples, 480);
            UpdateCodecGating();
```

- [ ] **Step 4: Add the gating handler**

Add to `MainWindow.xaml.cs`:

```csharp
        private void CodecCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCodecGating();

        // Opus fixes 48k/16/stereo, so the format dropdowns are disabled and the frame-size dropdown enabled.
        private void UpdateCodecGating()
        {
            bool opus = SelectedValue(CodecCombo, (int)CodecId.Pcm) == (int)CodecId.Opus;
            SampleRateCombo.IsEnabled = !opus;
            BitDepthCombo.IsEnabled = !opus;
            ChannelsCombo.IsEnabled = !opus;
            OpusFrameCombo.IsEnabled = opus;
        }
```

- [ ] **Step 5: Re-assert gating after Stop re-enables the panel**

`SetRunningState(false)` sets `SettingsPanel.IsEnabled = true`, which re-enables all children including the Opus-disabled dropdowns. Re-apply the per-control gating at the end of `SetRunningState`:

```csharp
            if (!running)
            {
                DiagnosticsText.Text = string.Empty;
                UpdateCodecGating();   // restore Opus/PCM per-control enablement after the blanket re-enable
            }
```

(Replace the existing `if (!running) DiagnosticsText.Text = string.Empty;` with the block above.)

- [ ] **Step 6: Build and verify**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 7: Manual verification**

- Launch, select Sender. Codec = PCM: format dropdowns enabled, frame-size disabled. Switch to Opus: format dropdowns grey out, frame-size enables.
- Pick Opus + 5 ms, Start; confirm Receiver plays and `pkts/s ≈ 200`. Stop, switch to PCM, confirm the previously-set Sample Rate/Bits/Channels are still there (not clobbered).
- Start with Opus, Stop, confirm the format dropdowns stay correctly greyed (gating survived the Stop re-enable).

- [ ] **Step 8: Commit**

```bash
git add UI/MainWindow.xaml UI/MainWindow.xaml.cs
git commit -m "feat: add Codec and Opus frame-size selectors with gating"
```

---

### Task 10: Diagnostics — codec label (optional, small)

Show the active codec in the UI diagnostics readout. `DiagnosticsSnapshot` is a positional
`readonly record struct`, so threading a codec through it would touch its parameter list and every
`ForSender`/`ForReceiver` construction — overkill for a label. Instead prepend it in `MainWindow`, which
already knows the current codec, leaving the snapshot untouched.

**Files:**
- Modify: `UI/MainWindow.xaml.cs` (`OnDiagnostics`)
- Verify: build + manual.

- [ ] **Step 1: Prepend the codec in the UI readout**

In `MainWindow.xaml.cs`, change `OnDiagnostics` to prefix the compact line with the active codec:

```csharp
        private void OnDiagnostics(DiagnosticsSnapshot snapshot)
        {
            string codec = audioStreamerLogic.CurrentConfig.Codec.ToString().ToLowerInvariant();
            Dispatcher.BeginInvoke((Action)(() =>
                DiagnosticsText.Text = $"codec={codec} {snapshot.ToCompactLine()}"));
        }
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build` → 0 errors. Run a session; confirm the diagnostics readout shows `codec=opus` / `codec=pcm`.

- [ ] **Step 3: Commit**

```bash
git add UI/MainWindow.xaml.cs
git commit -m "feat: show active codec in diagnostics readout"
```

---

### Task 11: Docs

Update the living docs to reflect the shipped feature.

**Files:**
- Modify: `CLAUDE.md`
- Modify: `docs/streaming-followups.md`

- [ ] **Step 1: Update CLAUDE.md**

Edit the wire-protocol paragraph (3-byte header: format code, codec id, sequence), the Sender/Receiver paragraphs (codec seam; Opus accumulator sized by `OpusFrameSamples`; decoder + PLC; Opus uses an effective 48 kHz/16-bit/stereo format that does not clobber stored config; frame size not signalled — receiver reads it from the packet and learns the length for PLC; fixed Opus reorder window), the `Config` list (`Codec` + `OpusFrameSamples`), and the MainWindow UI paragraph (Codec + Opus-frame-size dropdowns and their gating). Note the new `AudioStreamer.Tests` xUnit project and how to run it (`dotnet test AudioStreamer.Tests`).

- [ ] **Step 2: Update the followups doc**

In `docs/streaming-followups.md`, record the new 3-byte wire layout under a "Wire protocol (current)" note.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md docs/streaming-followups.md
git commit -m "docs: document Opus compression + test project"
```

---

## Self-Review

**Spec coverage:** Wire codec byte (T2), Config.Codec + OpusFrameSamples (T6), codec seam + PcmCodec + factory (T4), Opus adapters via OpusSharp (T5), sender effective-format + encoder routing (T7), receiver decode + per-datagram codec + Opus reorder window (T8), ReorderBuffer onLost per-skip (T3), UI dropdowns + gating (T9), diagnostics (T10), docs (T11), verification via the new test project (T1 + per-task tests). All spec sections map to a task.

**Refinements over the original spec (now synced into the spec §1/§5):** `CodecId` is a public top-level enum (accessibility); the Opus reorder window is a fixed bounded constant (`OpusReorderWindowPackets = 4`) rather than `ceil(targetMs/frameMs)` (the receiver can't know the sender's frame size before decoding — a fixed 4-packet window keeps give-up time 10–80 ms across 2.5–20 ms frames, always under the drift cap); the decoder learns the frame length from the first decode for PLC.

**Type consistency:** `IAudioEncoder.Push(byte[],int,int,Action<byte[],int>)`, `IAudioDecoder.Decode(byte[],int,int,byte[])`/`Conceal(byte[])`, `CodecFactory.CreateEncoder(CodecId,WaveFormat,int,Action<string>)`/`CreateDecoder(CodecId,WaveFormat,Action<string>)`, `ReorderBuffer(int,Action<byte[],int,int>,Action?)`, `ClampOpusFrameSamples(int)`, `OpusFrameSizes` — used consistently across tasks.

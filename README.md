# AudioStreamer

**Stream your PC's sound to another PC over your local network.** AudioStreamer captures whatever is playing on one Windows machine and plays it in real time on another, across your LAN over UDP.

One machine runs as the **Sender** (it captures its own system audio and transmits it); the other runs as the **Receiver** (it plays the incoming stream). Use it to pipe audio from a media PC to speakers on another machine, listen to one computer from another room, and the like.

---

## Install

1. Download **`AudioStreamer-Setup.exe`** from the [**Releases page**](https://github.com/DoctorKomodo/AudioStreamer/releases) (or build it yourself — see [below](#building-from-source)).
2. Run it on **both** machines. The installer is per-user — **no administrator rights and no UAC prompt** required.
3. Launch **AudioStreamer** from the Start Menu.

> **Requirements:** Windows 10 or 11, the **.NET 10 Desktop Runtime** (the setup wizard points you to the download if it's missing), and both machines on the **same local network**.

---

## Using AudioStreamer

You run one copy on each machine and give each a role with the **Mode** dropdown. The window only shows the settings relevant to the selected mode.

### On the machine you want to hear the audio (Receiver)

1. Find this machine's **local IP address** (e.g. run `ipconfig` and read the IPv4 address, like `192.168.1.50`).
2. Set **Mode → Receiver**.
3. Leave **Port** at the default (`5005`) or pick your own.
4. Click **Start**. The status line shows `Running (Receiver) on port 5005`.

### On the machine whose sound you want to send (Sender)

1. Set **Mode → Sender**.
2. Set **Host Name** to the **Receiver's IP address** from step 1 above.
3. Set **Port** to the **same** value as the Receiver.
4. Click **Start**. It begins capturing this PC's system audio and streaming it.

That's it — whatever plays on the Sender now comes out of the Receiver's speakers. Click **Stop** on either end to end the session; settings are saved automatically.

### Running in the background

Minimizing the window sends AudioStreamer to the **system tray** (double-click the tray icon, or right-click → **Show**, to bring it back; **Exit** quits). Tick **Start Minimized** to have it launch straight to the tray and begin streaming with your saved settings, and **Start with Windows** to launch it automatically at login. During installation you can also tick "Start AudioStreamer automatically when Windows starts" to set the same option.

> **Firewall:** the first time the Receiver binds its port, Windows may prompt to allow AudioStreamer through the firewall — allow it on **Private networks**. If you see no audio, check that UDP on the chosen port is permitted on the Receiver.

---

## Settings reference

The **Sender** decides the audio format; the **Receiver** learns it automatically from the stream.

**Sender:**
- **Host Name** — the Receiver's IP address.
- **Port** — UDP port to send to (must match the Receiver).
- **Sender Audio Buffer (ms)** — capture buffer size; smaller = lower latency, larger = smoother.
- **Audio Codec** — how the sound is sent over the network (**PCM** or **Opus** — see [Choosing a codec](#choosing-a-codec-pcm-or-opus) below).
- **Opus Frame Size (ms)** — only used with the Opus codec; smaller = lower latency, larger = slightly more efficient. Ignored (greyed out) when PCM is selected.
- **Sample Rate / Bits Per Sample / Channels** — the audio format to transmit (e.g. 48000 / 16 / 2). Leave **Channels** on **Auto (match source)** to follow your PC's own speaker setup (stereo, 5.1, and so on) automatically. These fields don't apply when the Opus codec is selected, which always sends CD-quality stereo.

**Receiver:**
- **Port** — UDP port to listen on (must match the Sender).
- **Receiver Audio Buffer (ms)** — jitter buffer ceiling.
- **Receiver Audio Latency (ms)** — output device latency.
- **Receiver Max Latency (ms)** — caps how far audio may fall behind before the buffer is trimmed (see below).
- **Receiver Preroll (ms)** — a small cushion of audio the Receiver keeps buffered before playback, so brief network hiccups don't cause dropouts. Raise it for a more resilient stream, lower it for tighter latency.

The Receiver reads the codec and audio format from the incoming stream, so you never have to match these by hand — just set the format on the Sender.

### About latency and lip-sync

The Sender and Receiver run on independent sound-card clocks that never tick at exactly the same rate, so over a long session the audio can gradually drift behind. **Receiver Max Latency (ms)** is the guard against this: when the playback backlog exceeds it, AudioStreamer trims the buffer back so the delay can't keep growing. Lower it for tighter sync (at the cost of an occasional brief blip when it resyncs); raise it for smoother playback with more tolerance for network jitter.

### Choosing a codec (PCM or Opus)

The **Audio Codec** setting on the Sender controls how sound travels over the network:

- **PCM** sends the audio uncompressed. It's bit-for-bit exact and lets you pick any sample rate, bit depth, and channel count, but it uses the most bandwidth — best on a fast, quiet wired network.
- **Opus** compresses the audio on the fly, using a small fraction of the bandwidth while staying transparent for everyday listening. It's the better choice on Wi-Fi or a busy network, and it recovers more gracefully from the occasional lost packet. Opus always streams high-quality stereo, so the Sample Rate / Bits / Channels fields don't apply while it's selected.

If you're not sure, start with **Opus** — it's easier on the network and sounds great. Switch to **PCM** only if you specifically need an exact, uncompressed copy or a non-stereo channel layout.

### Avoiding dropouts when a screen sleeps

Windows switches off an HDMI/DisplayPort monitor's audio output when the monitor goes into power-saving, which pauses the stream on that machine until the display wakes. AudioStreamer resumes on its own once the device comes back, but for playback that never cuts out, point the audio at a device that stays awake — a built-in or USB sound device, or a [virtual audio cable](https://vb-audio.com/Cable/) — rather than the display's own audio output.

---

## Uninstall

Uninstall from **Settings → Apps → Installed apps → AudioStreamer**, or use the **Uninstall AudioStreamer** shortcut in the Start Menu. AudioStreamer stores its settings in a `config.json` next to the installed executable, which the uninstaller removes for you.

---

## Building from source

Requires the **.NET 10 SDK** (and **Inno Setup 6** for the installer).

```powershell
dotnet build                 # Debug build
dotnet run                   # build + launch the app
.\build-installer.ps1        # produce installer\Output\AudioStreamer-Setup.exe
```

Architecture notes, the wire protocol, and the build/installer details live in **[CLAUDE.md](CLAUDE.md)**.

## A note on how this was built

AudioStreamer was developed with the help of AI tools. The code is open for anyone to read, review, and audit.

## License

Released under the [MIT License](LICENSE).

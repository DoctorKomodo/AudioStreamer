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

> **Firewall:** the first time the Receiver binds its port, Windows may prompt to allow AudioStreamer through the firewall — allow it on **Private networks**. If you see no audio, check that UDP on the chosen port is permitted on the Receiver.

---

## Settings reference

The **Sender** decides the audio format; the **Receiver** learns it automatically from the stream.

**Sender:**
- **Host Name** — the Receiver's IP address.
- **Port** — UDP port to send to (must match the Receiver).
- **Sender Audio Buffer (ms)** — capture buffer size; smaller = lower latency, larger = smoother.
- **Sample Rate / Bits Per Sample / Channels** — the audio format to transmit (e.g. 48000 / 16 / 2).

**Receiver:**
- **Port** — UDP port to listen on (must match the Sender).
- **Receiver Audio Buffer (ms)** — jitter buffer ceiling.
- **Receiver Audio Latency (ms)** — output device latency.
- **Receiver Max Latency (ms)** — caps how far audio may fall behind before the buffer is trimmed (see below).

### About latency and lip-sync

The Sender and Receiver run on independent sound-card clocks that never tick at exactly the same rate, so over a long session the audio can gradually drift behind. **Receiver Max Latency (ms)** is the guard against this: when the playback backlog exceeds it, AudioStreamer trims the buffer back so the delay can't keep growing. Lower it for tighter sync (at the cost of an occasional brief blip when it resyncs); raise it for smoother playback with more tolerance for network jitter.

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

# RTPTransmitter

A .NET Blazor Server application that receives AES67 RTP audio streams over UDP and delivers them to the browser for real-time playback using **Howler.js** with the Web Audio API.

## Architecture

```
┌─────────────────────┐     UDP/RTP      ┌────────────────────────┐
│   AES67 Source       │ ──────────────►  │  RtpListenerService    │
│   (48kHz PCM L16/L24)│                  │  (Background Service)  │
└─────────────────────┘                  └───────────┬────────────┘
                                                      │
                                          PCM → Float32 conversion
                                                      │
                                                      ▼
                                         ┌────────────────────────┐
                                         │   AudioStreamHub       │
                                         │   (SignalR Hub)        │
                                         └───────────┬────────────┘
                                                      │
                                              SignalR WebSocket
                                                      │
                                                      ▼
                                         ┌────────────────────────┐
                                         │   Browser              │
                                         │   ┌──────────────────┐ │
                                         │   │ audioStreamInterop│ │
                                         │   └────────┬─────────┘ │
                                         │            │           │
                                         │   ┌────────▼─────────┐ │
                                         │   │  HowlerStream    │ │
                                         │   │  (howler.stream)  │ │
                                         │   └────────┬─────────┘ │
                                         │            │           │
                                         │   ┌────────▼─────────┐ │
                                         │   │  Web Audio API   │ │
                                         │   │  (AudioContext)   │ │
                                         │   └──────────────────┘ │
                                         └────────────────────────┘
```

## Components

### Server-Side (.NET)

- **`Services/RtpPacket.cs`** — Parses RTP packet headers (RFC 3550) including version, sequence, timestamp, SSRC, extension headers, and payload extraction.
- **`Services/PcmConverter.cs`** — Converts L16 (16-bit big-endian) and L24 (24-bit big-endian) PCM audio payloads to Float32 samples.
- **`Services/RtpListenerOptions.cs`** — Configuration model for the UDP listener (port, multicast, sample rate, channels, etc.).
- **`Services/RtpListenerService.cs`** — Background service that listens for UDP packets, parses RTP, converts PCM, aggregates packets into chunks, and broadcasts via SignalR.
- **`Hubs/AudioStreamHub.cs`** — SignalR hub managing client connections and stream group membership.

### Client-Side (JavaScript)

- **`wwwroot/js/howler.core.js`** — Howler.js core library (Web Audio API + HTML5 Audio).
- **`wwwroot/js/howler.stream.js`** — HowlerStream plugin that adds a `SoundBuffer` for gapless playback of streamed PCM Float32 chunks via Web Audio API.
- **`wwwroot/js/audioStreamInterop.js`** — Blazor JS interop module that connects SignalR to HowlerStream.

### Blazor Pages

- **`Components/Pages/AudioStream.razor`** — Main UI page with stream configuration, playback controls (start/stop/mute/volume), and real-time stats.

## Configuration

Edit `appsettings.json`:

```json
{
  "RtpListener": {
    "Port": 5004,
    "MulticastGroup": "",
    "LocalAddress": "0.0.0.0",
    "SampleRate": 48000,
    "Channels": 1,
    "PacketsPerChunk": 10,
    "ForceBitDepth": 0
  }
}
```

| Setting | Description |
|---|---|
| `Port` | UDP port for RTP packets (AES67 default: 5004) |
| `MulticastGroup` | Multicast address to join (e.g. `239.69.1.1`). Empty = unicast only |
| `LocalAddress` | Local interface IP for binding. `0.0.0.0` = all interfaces |
| `SampleRate` | Expected audio sample rate (48000 for AES67) |
| `Channels` | 1 = mono, 2 = stereo |
| `PacketsPerChunk` | RTP packets aggregated per browser chunk. Higher = more latency, smoother playback |
| `ForceBitDepth` | 0 = auto-detect, 16 = force L16, 24 = force L24 |

## Running

```bash
cd RTPTransmitter
dotnet run
```

Then open `http://localhost:5000/audiostream` in your browser and click **Start Stream**.

## Testing Without an AES67 Source

You can send test RTP packets using tools like `ffmpeg`:

```bash
# Send a sine wave as L16 RTP to localhost:5004
ffmpeg -re -f lavfi -i "sine=frequency=440:sample_rate=48000" \
  -ar 48000 -ac 1 -acodec pcm_s16be \
  -f rtp rtp://127.0.0.1:5004
```

Or with GStreamer:

```bash
gst-launch-1.0 audiotestsrc freq=440 ! \
  audioconvert ! audio/x-raw,rate=48000,channels=1,format=S16BE ! \
  rtpL16pay ! udpsink host=127.0.0.1 port=5004
```

## Howler.js Stream Plugin

The `howler.stream.js` plugin can also be used independently of this Blazor application:

```javascript
// Create a stream connected to Howler's AudioContext
var stream = new HowlerStream({
    sampleRate: 48000,
    channels: 1,
    bufferSize: 6,
    debug: true
});

// Feed Float32 PCM chunks from any source
stream.addChunk(float32Array);

// Control playback
stream.volume(0.5);
stream.mute(true);
stream.stop();
stream.destroy();
```

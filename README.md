# Screen Recorder (WinUI 3)

A WinUI 3 screen recorder. It records your screen and system audio to MP4 (H.264 + AAC), lets you boost the volume up to 500%, and starts in dark mode.

## Features
- Record any display, or pick a single window with **Pick window**
- System audio via WASAPI loopback (whatever your speakers or headphones are playing)
- **Volume boost** from 0–500%, with an optional soft limiter so boosted audio doesn't distort, and a live level meter
- Choice of 30 or 60 fps, three quality levels (8, 16 or 35 Mbps), and resolution (Native, 1440p, 1080p or 720p)
- Pause and resume (F10), start and stop (F9)
- Option to keep the recorder window out of the video
- Dark, Light or System theme on a Mica background; your settings are remembered
- Hardware encoding through Media Foundation when your GPU supports it

## Build and run
Requirements: Windows 10 version 2004 or later (Windows 11 recommended) and the .NET 10 SDK.

```
dotnet run
```

Recordings are saved to `Videos\Screen Recordings` by default.

> **Note:** clone into a short path (e.g. `C:\dev\ScreenRecorder`). The WinUI XAML compiler fails with
> `WMC9999 Could not find a part of the path ...XamlSaveStateFile.xml` when the project path is very long.

If the app crashes, details are written to `%LOCALAPPDATA%\ScreenRecorder\crash.log`.

## License
MIT

## How it works
| File | Role |
|---|---|
| `Capture/Recorder.cs` | Captures frames with Windows.Graphics.Capture and feeds the video and audio through a MediaStreamSource into MediaTranscoder to produce the MP4 |
| `Capture/AudioLoopback.cs` | Captures loopback audio with NAudio, applies the gain and limiter, and downmixes to stereo |
| `Capture/Direct3DHelper.cs` | D3D11 and WinRT surface interop (Vortice) |
| `Capture/CaptureSources.cs` | Lists monitors and creates a capture item for each one without a picker |
| `MainWindow.xaml(.cs)` | The user interface |

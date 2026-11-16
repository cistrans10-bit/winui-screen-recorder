using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace ScreenRecorder.Capture;

internal sealed record RecorderOptions(
    int FrameRate,
    uint VideoBitrate,
    int MaxHeight,          // 0 = native resolution
    bool CaptureCursor,
    bool RecordAudio);

/// <summary>
/// Records a GraphicsCaptureItem plus (optionally) loopback audio into an H.264/AAC MP4.
/// Video frames come from Windows.Graphics.Capture, audio from <see cref="AudioLoopback"/>;
/// both are fed through a MediaStreamSource into MediaTranscoder (hardware encoder when available).
/// </summary>
internal sealed class Recorder : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly RecorderOptions _options;
    private readonly AudioLoopback? _audio;

    private ID3D11Device? _d3dDevice;
    private IDirect3DDevice? _device;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private Windows.Graphics.SizeInt32 _poolSize;
    private int _width, _height;

    private readonly BlockingCollection<(ID3D11Texture2D Tex, TimeSpan Time)> _frames = new(boundedCapacity: 8);
    private readonly BlockingCollection<(byte[] Data, TimeSpan Time, TimeSpan Duration)> _audioChunks = new();
    private ID3D11Texture2D? _lastTexture;
    private TimeSpan _lastVideoTime = TimeSpan.MinValue;
    private TimeSpan _lastAcceptedFrame = TimeSpan.MinValue;

    // Timeline: all timestamps are QPC-based and exclude paused time.
    private TimeSpan _startTime;
    private TimeSpan _pausedTotal;
    private TimeSpan _pauseStarted;
    private volatile bool _paused;
    private volatile bool _stopping;

    private readonly object _audioLock = new();
    private long _audioFramesWritten;
    private Timer? _silenceTimer;

    public Recorder(GraphicsCaptureItem item, RecorderOptions options, AudioLoopback? audio)
    {
        _item = item;
        _options = options;
        _audio = options.RecordAudio ? audio : null;
    }

    public bool IsPaused => _paused;

    /// <summary>Recording time excluding pauses.</summary>
    public TimeSpan Elapsed => _startTime == default ? TimeSpan.Zero : (_paused ? _pauseStarted : Now) - _startTime - _pausedTotal;

    public event Action? ItemClosed;

    private static TimeSpan Now => TimeSpan.FromTicks((long)(Stopwatch.GetTimestamp() * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)));

    /// <summary>Starts recording and returns when the file has been fully written (after <see cref="Stop"/>).</summary>
    public async Task RecordAsync(IRandomAccessStream stream)
    {
        _d3dDevice = Direct3DHelper.CreateD3DDevice();
        _device = Direct3DHelper.CreateDirect3DDevice(_d3dDevice);

        var size = _item.Size;
        _width = Math.Max(2, size.Width & ~1);
        _height = Math.Max(2, size.Height & ~1);

        var mss = CreateMediaStreamSource(out var profile);

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _poolSize = size;
        _framePool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;

        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _options.CaptureCursor;
        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            try { _session.IsBorderRequired = false; } catch { /* needs borderless access; harmless */ }
        }
        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.MinUpdateInterval)))
        {
            try { _session.MinUpdateInterval = TimeSpan.FromSeconds(1.0 / _options.FrameRate); } catch { }
        }

        _startTime = Now;
        if (_audio is not null)
        {
            _audio.SamplesAvailable += OnAudioSamples;
            _silenceTimer = new Timer(_ => FillSilence(), null, 50, 50);
        }
        _session.StartCapture();

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, stream, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException($"The encoder can't handle this recording ({prepared.FailureReason}). Try a lower resolution.");

        await prepared.TranscodeAsync();
    }

    public void Pause()
    {
        if (_paused || _stopping) return;
        lock (_audioLock)
        {
            _pauseStarted = Now;
            _paused = true;
        }
    }

    public void Resume()
    {
        if (!_paused) return;
        lock (_audioLock)
        {
            _pausedTotal += Now - _pauseStarted;
            _paused = false;
        }
    }

    public void Stop()
    {
        if (_stopping) return;
        if (_paused) Resume();
        _stopping = true;
        _silenceTimer?.Dispose();
        if (_audio is not null) _audio.SamplesAvailable -= OnAudioSamples;
        _session?.Dispose();
        _frames.CompleteAdding();
        _audioChunks.CompleteAdding();
    }

    // ---------------------------------------------------------------- media stream source

    private MediaStreamSource CreateMediaStreamSource(out MediaEncodingProfile profile)
    {
        var videoInput = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_width, (uint)_height);
        var videoDescriptor = new VideoStreamDescriptor(videoInput);

        int outW = _width, outH = _height;
        if (_options.MaxHeight > 0 && _height > _options.MaxHeight)
        {
            outH = _options.MaxHeight & ~1;
            outW = Math.Max(2, (int)Math.Round(_width * (double)outH / _height) & ~1);
        }

        profile = new MediaEncodingProfile
        {
            Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Mpeg4 },
            Video = VideoEncodingProperties.CreateH264(),
        };
        profile.Video.Width = (uint)outW;
        profile.Video.Height = (uint)outH;
        profile.Video.Bitrate = _options.VideoBitrate;
        profile.Video.FrameRate.Numerator = (uint)_options.FrameRate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;

        MediaStreamSource mss;
        if (_audio is not null)
        {
            var audioInput = AudioEncodingProperties.CreatePcm((uint)_audio.SampleRate, (uint)_audio.Channels, 16);
            mss = new MediaStreamSource(videoDescriptor, new AudioStreamDescriptor(audioInput));
            uint aacRate = _audio.SampleRate is 44100 or 48000 ? (uint)_audio.SampleRate : 48000;
            profile.Audio = AudioEncodingProperties.CreateAac(aacRate, (uint)_audio.Channels, 192000);
        }
        else
        {
            mss = new MediaStreamSource(videoDescriptor);
            profile.Audio = null;
        }

        mss.BufferTime = TimeSpan.Zero;
        mss.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        mss.SampleRequested += OnSampleRequested;
        return mss;
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        try
        {
            if (args.Request.StreamDescriptor is VideoStreamDescriptor)
                args.Request.Sample = NextVideoSample();
            else
                args.Request.Sample = NextAudioSample();
        }
        catch
        {
            args.Request.Sample = null;
        }
    }

    private MediaStreamSample? NextVideoSample()
    {
        while (true)
        {
            if (_frames.TryTake(out var frame, 1000))
            {
                _lastTexture?.Dispose();
                _lastTexture = frame.Tex;
                return MakeVideoSample(frame.Tex, frame.Time);
            }

            if (_frames.IsCompleted) return null;

            // Screen hasn't changed for a while: repeat the last frame so the encoder keeps pace with audio.
            if (!_paused && _lastTexture is not null && _d3dDevice is not null)
            {
                var copy = CreateTargetTexture();
                _d3dDevice.ImmediateContext.CopyResource(copy, _lastTexture);
                _lastTexture.Dispose();
                _lastTexture = copy;
                return MakeVideoSample(copy, Now - _startTime - _pausedTotal);
            }
        }
    }

    private MediaStreamSample MakeVideoSample(ID3D11Texture2D texture, TimeSpan time)
    {
        if (time <= _lastVideoTime) time = _lastVideoTime + TimeSpan.FromMilliseconds(1);
        _lastVideoTime = time;

        var surface = Direct3DHelper.CreateDirect3DSurface(texture);
        var sample = MediaStreamSample.CreateFromDirect3D11Surface(surface, time);
        sample.Processed += (_, _) => surface.Dispose();
        return sample;
    }

    private MediaStreamSample? NextAudioSample()
    {
        if (!_audioChunks.TryTake(out var chunk, Timeout.Infinite)) return null;
        var sample = MediaStreamSample.CreateFromBuffer(chunk.Data.AsBuffer(), chunk.Time);
        sample.Duration = chunk.Duration;
        return sample;
    }

    // ---------------------------------------------------------------- video capture

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null || _stopping || _d3dDevice is null) return;

        var contentSize = frame.ContentSize;
        if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
        {
            // Window was resized: recreate the pool; the output stays at the original size.
            _poolSize = contentSize;
            sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
        }

        if (_paused) return;

        var time = frame.SystemRelativeTime - _startTime - _pausedTotal;
        if (time < TimeSpan.Zero) return;

        // Respect the chosen frame rate on high-refresh displays.
        var interval = TimeSpan.FromSeconds(1.0 / _options.FrameRate);
        if (_lastAcceptedFrame != TimeSpan.MinValue && time - _lastAcceptedFrame < interval * 0.9) return;
        _lastAcceptedFrame = time;

        var target = CreateTargetTexture();
        try
        {
            using var source = Direct3DHelper.GetTexture(frame.Surface);
            var ctx = _d3dDevice.ImmediateContext;

            int w = Math.Min(contentSize.Width, _width);
            int h = Math.Min(contentSize.Height, _height);
            if (w < _width || h < _height)
            {
                using var rtv = _d3dDevice.CreateRenderTargetView(target);
                ctx.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
            }
            ctx.CopySubresourceRegion(target, 0, 0, 0, 0, source, 0, new Box(0, 0, 0, w, h, 1));
        }
        catch
        {
            target.Dispose();
            return;
        }

        // If the encoder falls behind, drop the oldest frame instead of blocking capture.
        while (!_frames.TryAdd((target, time)))
        {
            if (_frames.IsAddingCompleted) { target.Dispose(); return; }
            if (_frames.TryTake(out var old)) old.Tex.Dispose();
        }
    }

    private ID3D11Texture2D CreateTargetTexture() =>
        _d3dDevice!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => ItemClosed?.Invoke();

    // ---------------------------------------------------------------- audio

    private void OnAudioSamples(short[] samples, int frames)
    {
        lock (_audioLock)
        {
            if (_paused || _stopping) return;
            PadSilenceLocked(toleranceSeconds: 0.1);
            WriteAudioLocked(samples, frames);
        }
    }

    /// <summary>Loopback delivers nothing while the system is silent; keep the audio timeline continuous.</summary>
    private void FillSilence()
    {
        lock (_audioLock)
        {
            if (_paused || _stopping) return;
            PadSilenceLocked(toleranceSeconds: 0.15);
        }
    }

    private void PadSilenceLocked(double toleranceSeconds)
    {
        int rate = _audio!.SampleRate;
        long expected = (long)((Now - _startTime - _pausedTotal).TotalSeconds * rate);
        long missing = expected - _audioFramesWritten;
        if (missing <= rate * toleranceSeconds) return;

        // Pad in chunks of at most 100 ms.
        int chunk = rate / 10;
        while (missing > 0)
        {
            int n = (int)Math.Min(chunk, missing);
            WriteAudioLocked(new short[n * _audio.Channels], n);
            missing -= n;
        }
    }

    private void WriteAudioLocked(short[] samples, int frames)
    {
        int rate = _audio!.SampleRate;
        var bytes = new byte[frames * _audio.Channels * sizeof(short)];
        System.Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        var time = TimeSpan.FromSeconds(_audioFramesWritten / (double)rate);
        var duration = TimeSpan.FromSeconds(frames / (double)rate);
        _audioFramesWritten += frames;

        if (!_audioChunks.IsAddingCompleted)
        {
            try { _audioChunks.Add((bytes, time, duration)); } catch (InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        Stop();
        _item.Closed -= OnItemClosed;
        if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
        _framePool?.Dispose();
        while (_frames.TryTake(out var f)) f.Tex.Dispose();
        _lastTexture?.Dispose();
        _device?.Dispose();
        _d3dDevice?.Dispose();
        _frames.Dispose();
        _audioChunks.Dispose();
    }
}

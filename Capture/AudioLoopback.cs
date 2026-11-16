using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ScreenRecorder.Capture;

/// <summary>
/// Captures whatever the default playback device is playing (WASAPI loopback), applies the
/// volume boost with an optional soft limiter, and hands out 16-bit PCM (mono or stereo).
/// </summary>
#pragma warning disable CS0618 // WasapiLoopbackCapture still does system-wide loopback fine
internal sealed class AudioLoopback : IDisposable
{
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private WasapiLoopbackCapture? _capture;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifier;
    private bool _isFloat;
    private int _srcChannels;
    private int _srcBytesPerSample;

    private float _peakAccum;
    private bool _clipAccum;
    private long _lastLevelTicks;

    /// <summary>Linear gain. 1.0 = 100%, 5.0 = 500%.</summary>
    public float Gain { get; set; } = 1f;

    /// <summary>Soft-limit boosted samples instead of hard clipping them.</summary>
    public bool Limiter { get; set; } = true;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public string DeviceName { get; private set; } = "";
    public bool IsRunning => _capture is not null;

    /// <summary>Interleaved 16-bit samples, frame count. Raised on the audio thread.</summary>
    public event Action<short[], int>? SamplesAvailable;

    /// <summary>Peak level (0..1, after gain) and whether the signal exceeded full scale. ~30 Hz.</summary>
    public event Action<float, bool>? LevelChanged;

    /// <summary>The default playback device changed.</summary>
    public event Action? DefaultDeviceChanged;

    public void Start()
    {
        if (_capture is not null) return;

        _enumerator ??= new MMDeviceEnumerator();
        if (_notifier is null)
        {
            _notifier = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifier.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) DefaultDeviceChanged?.Invoke();
            };
        }

        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        DeviceName = device.FriendlyName;

        var capture = new WasapiLoopbackCapture(device);
        var wf = capture.WaveFormat;
        _isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                   (wf is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);
        _srcChannels = wf.Channels;
        _srcBytesPerSample = wf.BitsPerSample / 8;
        SampleRate = wf.SampleRate;
        Channels = Math.Min(_srcChannels, 2);

        capture.DataAvailable += OnDataAvailable;
        _capture = capture;
        capture.StartRecording();
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c is null) return;
        c.DataAvailable -= OnDataAvailable;
        try { c.StopRecording(); } catch { }
        c.Dispose();
        LevelChanged?.Invoke(0, false);
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int frameBytes = _srcBytesPerSample * _srcChannels;
        if (frameBytes == 0 || e.BytesRecorded < frameBytes) return;

        int frames = e.BytesRecorded / frameBytes;
        int outCh = Channels;
        var output = new short[frames * outCh];
        float gain = Gain;
        bool limiter = Limiter;
        float peak = 0;
        bool clipped = false;

        ReadOnlySpan<byte> src = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (int f = 0; f < frames; f++)
        {
            int baseOffset = f * frameBytes;
            for (int oc = 0; oc < outCh; oc++)
            {
                // Downmix extra channels: even channels -> left, odd -> right.
                float sum = 0;
                int count = 0;
                for (int sc = oc; sc < _srcChannels; sc += outCh)
                {
                    sum += ReadSample(src, baseOffset + sc * _srcBytesPerSample);
                    count++;
                }
                float s = (count > 0 ? sum / count : 0) * gain;

                float a = MathF.Abs(s);
                if (a > 1f) clipped = true;
                if (limiter) s = SoftLimit(s);
                s = Math.Clamp(s, -1f, 1f);

                a = MathF.Abs(s);
                if (a > peak) peak = a;
                output[f * outCh + oc] = (short)(s * 32767f);
            }
        }

        SamplesAvailable?.Invoke(output, frames);
        ReportLevel(peak, clipped);
    }

    private float ReadSample(ReadOnlySpan<byte> buf, int offset)
    {
        if (_isFloat)
        {
            return _srcBytesPerSample == 8
                ? (float)BitConverter.ToDouble(buf.Slice(offset, 8))
                : BitConverter.ToSingle(buf.Slice(offset, 4));
        }
        return _srcBytesPerSample switch
        {
            2 => BitConverter.ToInt16(buf.Slice(offset, 2)) / 32768f,
            3 => ((buf[offset] << 8 | buf[offset + 1] << 16 | buf[offset + 2] << 24) >> 8) / 8388608f,
            4 => BitConverter.ToInt32(buf.Slice(offset, 4)) / 2147483648f,
            _ => 0f,
        };
    }

    /// <summary>Transparent below -2 dBFS, smoothly saturates above so boosted audio never hard-clips.</summary>
    private static float SoftLimit(float x)
    {
        const float knee = 0.8f;
        float a = MathF.Abs(x);
        if (a <= knee) return x;
        float y = knee + (1f - knee) * MathF.Tanh((a - knee) / (1f - knee));
        return MathF.CopySign(y, x);
    }

    private void ReportLevel(float peak, bool clipped)
    {
        _peakAccum = MathF.Max(_peakAccum, peak);
        _clipAccum |= clipped;
        long now = Environment.TickCount64;
        if (now - _lastLevelTicks < 33) return;
        _lastLevelTicks = now;
        LevelChanged?.Invoke(_peakAccum, _clipAccum);
        _peakAccum = 0;
        _clipAccum = false;
    }

    public void Dispose()
    {
        Stop();
        _notifier?.Dispose();
        _notifier = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }
}

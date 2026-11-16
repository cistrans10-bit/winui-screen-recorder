using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ScreenRecorder.Capture;
using Windows.Graphics.Capture;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace ScreenRecorder;

public sealed partial class MainWindow : Window
{
    private static readonly uint[] Bitrates = [8_000_000, 16_000_000, 35_000_000];
    private static readonly int[] MaxHeights = [0, 1440, 1080, 720];

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AudioLoopback _audio = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly IntPtr _hwnd;

    private Recorder? _recorder;
    private Task? _recordTask;
    private GraphicsCaptureItem? _pickedItem;
    private string? _lastFile;
    private bool _audioDeviceChangedWhileRecording;
    private float _meterValue;

    private sealed record SourceEntry(string Label, MonitorInfo? Monitor, GraphicsCaptureItem? Item)
    {
        public override string ToString() => Label;
    }

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Scale(560), Scale(900)));
        if (AppWindow.Presenter is OverlappedPresenter p) p.PreferredMinimumWidth = Scale(460);

        LoadSettingsIntoUi();
        RefreshSources();
        ApplyTheme(_settings.Theme);

        _audio.LevelChanged += (level, clipped) => DispatcherQueue.TryEnqueue(() => UpdateMeter(level, clipped));
        _audio.DefaultDeviceChanged += () => DispatcherQueue.TryEnqueue(OnDefaultAudioDeviceChanged);
        StartAudioMonitor();

        _clock.Tick += (_, _) => UpdateTimer();
        Activated += (_, _) => { if (_recorder is null) RefreshSources(); };
        Closed += OnClosed;
    }

    private int Scale(int px) => (int)(px * GetDpiForWindow(_hwnd) / 96.0);

    // ---------------------------------------------------------------- settings

    private void LoadSettingsIntoUi()
    {
        FpsCombo.SelectedIndex = _settings.FrameRate >= 60 ? 1 : 0;
        QualityCombo.SelectedIndex = Math.Clamp(_settings.QualityIndex, 0, 2);
        ResolutionCombo.SelectedIndex = Math.Clamp(_settings.ResolutionIndex, 0, 3);
        CursorToggle.IsOn = _settings.CaptureCursor;
        HideToggle.IsOn = _settings.HideFromCapture;
        AudioToggle.IsOn = _settings.RecordAudio;
        BoostSlider.Value = _settings.BoostPercent;
        LimiterToggle.IsOn = _settings.Limiter;
        FolderText.Text = _settings.OutputFolder;
        AudioPanel.Opacity = _settings.RecordAudio ? 1 : 0.4;
        ApplyHideFromCapture();
    }

    private void SaveSettingsFromUi()
    {
        _settings.FrameRate = FpsCombo.SelectedIndex == 1 ? 60 : 30;
        _settings.QualityIndex = QualityCombo.SelectedIndex;
        _settings.ResolutionIndex = ResolutionCombo.SelectedIndex;
        _settings.CaptureCursor = CursorToggle.IsOn;
        _settings.HideFromCapture = HideToggle.IsOn;
        _settings.RecordAudio = AudioToggle.IsOn;
        _settings.BoostPercent = BoostSlider.Value;
        _settings.Limiter = LimiterToggle.IsOn;
        _settings.Save();
    }

    // ---------------------------------------------------------------- sources

    private void RefreshSources()
    {
        var previous = SourceCombo.SelectedItem as SourceEntry;
        var entries = CaptureSources.GetMonitors().Select(m => new SourceEntry(m.Label, m, null)).ToList();
        if (_pickedItem is not null)
            entries.Add(new SourceEntry("Window · " + _pickedItem.DisplayName, null, _pickedItem));

        SourceCombo.ItemsSource = entries;
        var match = previous is null ? null
            : entries.FirstOrDefault(e => (e.Item is not null && e.Item == previous.Item) ||
                                          (e.Monitor is not null && e.Monitor.Handle == previous.Monitor?.Handle));
        SourceCombo.SelectedItem = match ?? entries.FirstOrDefault();
    }

    private async void PickButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new GraphicsCapturePicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        var item = await picker.PickSingleItemAsync();
        if (item is null) return;

        _pickedItem = item;
        RefreshSources();
        SourceCombo.SelectedItem = ((IEnumerable<SourceEntry>)SourceCombo.ItemsSource).Last();
    }

    // ---------------------------------------------------------------- recording

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is null) await StartRecordingAsync();
        else await StopRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (SourceCombo.SelectedItem is not SourceEntry source)
        {
            ShowMessage(InfoBarSeverity.Warning, "No source", "Pick a display or window to record.");
            return;
        }

        SaveSettingsFromUi();
        MessageBar.IsOpen = false;

        try
        {
            var item = source.Item ?? CaptureSources.CreateForMonitor(source.Monitor!.Handle);

            bool recordAudio = AudioToggle.IsOn;
            if (recordAudio && !_audio.IsRunning) StartAudioMonitor();
            recordAudio &= _audio.IsRunning;

            Directory.CreateDirectory(_settings.OutputFolder);
            var folder = await StorageFolder.GetFolderFromPathAsync(_settings.OutputFolder);
            var file = await folder.CreateFileAsync($"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4",
                CreationCollisionOption.GenerateUniqueName);
            _lastFile = file.Path;

            var options = new RecorderOptions(
                FrameRate: _settings.FrameRate,
                VideoBitrate: Bitrates[QualityCombo.SelectedIndex],
                MaxHeight: MaxHeights[ResolutionCombo.SelectedIndex],
                CaptureCursor: CursorToggle.IsOn,
                RecordAudio: recordAudio);

            var recorder = new Recorder(item, options, _audio);
            recorder.ItemClosed += () => DispatcherQueue.TryEnqueue(async () => await StopRecordingAsync());
            _recorder = recorder;

            var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            _recordTask = RunRecorderAsync(recorder, stream, file);

            SetRecordingUi(true);
            _clock.Start();
        }
        catch (Exception ex)
        {
            _recorder?.Dispose();
            _recorder = null;
            SetRecordingUi(false);
            ShowMessage(InfoBarSeverity.Error, "Couldn't start recording", ex.Message);
        }
    }

    private async Task RunRecorderAsync(Recorder recorder, Windows.Storage.Streams.IRandomAccessStream stream, StorageFile file)
    {
        Exception? error = null;
        try
        {
            await Task.Run(() => recorder.RecordAsync(stream));
        }
        catch (Exception ex)
        {
            error = ex;
            recorder.Stop();
        }
        finally
        {
            stream.Dispose();
        }

        DispatcherQueue.TryEnqueue(async () =>
        {
            recorder.Dispose();
            if (_recorder == recorder) _recorder = null;
            _clock.Stop();
            SetRecordingUi(false);

            if (error is not null)
            {
                ShowMessage(InfoBarSeverity.Error, "Recording failed", error.Message);
                try { await file.DeleteAsync(); } catch { }
                return;
            }

            var props = await file.GetBasicPropertiesAsync();
            ShowMessage(InfoBarSeverity.Success, "Saved",
                $"{file.Name}  ·  {props.Size / 1024.0 / 1024.0:0.0} MB", showOpen: true);

            if (_audioDeviceChangedWhileRecording)
            {
                _audioDeviceChangedWhileRecording = false;
                OnDefaultAudioDeviceChanged();
            }
        });
    }

    private async Task StopRecordingAsync()
    {
        var recorder = _recorder;
        if (recorder is null) return;
        RecordButton.IsEnabled = false;
        StatusText.Text = "Finishing…";
        recorder.Stop();
        if (_recordTask is not null) await _recordTask;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder is null) return;
        if (_recorder.IsPaused) _recorder.Resume();
        else _recorder.Pause();
        UpdateRecordingStatus();
    }

    private void SetRecordingUi(bool recording)
    {
        RecordButton.IsEnabled = true;
        PauseButton.IsEnabled = recording;
        RecordLabel.Text = recording ? "Stop recording" : "Start recording";
        RecordIcon.Glyph = recording ? "" : "";
        foreach (var c in new Control[] { SourceCombo, PickButton, FpsCombo, QualityCombo, ResolutionCombo, CursorToggle, AudioToggle, ChangeFolderButton })
            c.IsEnabled = !recording;
        if (!recording) TimerText.Text = "00:00:00";
        UpdateRecordingStatus();
    }

    private void UpdateRecordingStatus()
    {
        bool recording = _recorder is not null;
        bool paused = _recorder?.IsPaused == true;
        StatusText.Text = !recording ? "Ready" : paused ? "Paused" : "Recording";
        PauseIcon.Glyph = paused ? "" : "";
        RecDot.Opacity = 1;
        RecDot.Fill = new SolidColorBrush(!recording ? Color.FromArgb(255, 46, 194, 126)
            : paused ? Color.FromArgb(255, 246, 211, 45) : Color.FromArgb(255, 232, 17, 35));
    }

    private void UpdateTimer()
    {
        if (_recorder is null) return;
        TimerText.Text = _recorder.Elapsed.ToString(@"hh\:mm\:ss");
        // Blink the dot while recording.
        if (!_recorder.IsPaused) RecDot.Opacity = DateTime.Now.Millisecond < 600 ? 1 : 0.35;
        else RecDot.Opacity = 1;
    }

    // ---------------------------------------------------------------- audio

    private void StartAudioMonitor()
    {
        if (!AudioToggle.IsOn) return;
        try
        {
            _audio.Gain = (float)(BoostSlider.Value / 100.0);
            _audio.Limiter = LimiterToggle.IsOn;
            _audio.Start();
            DeviceText.Text = $"{_audio.DeviceName}  ·  {_audio.SampleRate / 1000.0:0.#} kHz";
        }
        catch (Exception ex)
        {
            DeviceText.Text = "No playback device available";
            Debug.WriteLine(ex);
        }
    }

    private void OnDefaultAudioDeviceChanged()
    {
        // Switching devices mid-recording would change the audio format; wait until it's done.
        if (_recorder is not null)
        {
            _audioDeviceChangedWhileRecording = true;
            return;
        }
        if (!AudioToggle.IsOn) return;
        _audio.Stop();
        StartAudioMonitor();
    }

    private void AudioToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AudioPanel.Opacity = AudioToggle.IsOn ? 1 : 0.4;
        if (AudioToggle.IsOn) StartAudioMonitor();
        else
        {
            _audio.Stop();
            DeviceText.Text = "Off";
        }
    }

    private void BoostSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        BoostText.Text = $"{e.NewValue:0}%";
        _audio.Gain = (float)(e.NewValue / 100.0);
    }

    private void ResetBoostButton_Click(object sender, RoutedEventArgs e) => BoostSlider.Value = 100;

    private void LimiterToggle_Toggled(object sender, RoutedEventArgs e) => _audio.Limiter = LimiterToggle.IsOn;

    private void UpdateMeter(float peak, bool clipped)
    {
        // Map -60..0 dBFS to 0..1, with a quick attack and slower release.
        float db = peak > 0 ? 20f * MathF.Log10(peak) : -60f;
        float target = Math.Clamp((db + 60f) / 60f, 0f, 1f);
        _meterValue = target > _meterValue ? target : MathF.Max(target, _meterValue - 0.05f);
        MeterScale.ScaleX = 1 - _meterValue;
        ClipText.Opacity = clipped ? 1 : Math.Max(0, ClipText.Opacity - 0.1);
        ClipText.Text = LimiterToggle.IsOn ? "LIMITING" : "CLIPPING";
    }

    // ---------------------------------------------------------------- output folder

    private async void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _settings.OutputFolder = folder.Path;
        FolderText.Text = folder.Path;
        _settings.Save();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.OutputFolder}\"") { UseShellExecute = true });
    }

    private void OpenLastButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile is not null && File.Exists(_lastFile))
            Process.Start(new ProcessStartInfo(_lastFile) { UseShellExecute = true });
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message, bool showOpen = false)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        OpenLastButton.Visibility = showOpen ? Visibility.Visible : Visibility.Collapsed;
        MessageBar.IsOpen = true;
    }

    // ---------------------------------------------------------------- window / theme

    private void HideToggle_Toggled(object sender, RoutedEventArgs e) => ApplyHideFromCapture();

    private void ApplyHideFromCapture()
    {
        const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        SetWindowDisplayAffinity(_hwnd, HideToggle.IsOn ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        string next = _settings.Theme switch { "Dark" => "Light", "Light" => "System", _ => "Dark" };
        ApplyTheme(next);
        _settings.Theme = next;
        _settings.Save();
    }

    private void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        ThemeLabel.Text = theme;
        ThemeIcon.Glyph = theme switch { "Dark" => "", "Light" => "", _ => "" };

        bool dark = Root.ActualTheme == ElementTheme.Dark ||
                    (theme == "Dark") ||
                    (theme == "System" && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        var tb = AppWindow.TitleBar;
        var fg = dark ? Colors.White : Colors.Black;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x18, 255, 255, 255) : Color.FromArgb(0x18, 0, 0, 0);
        tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x10, 255, 255, 255) : Color.FromArgb(0x10, 0, 0, 0);
        tb.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x80, 255, 255, 255) : Color.FromArgb(0x80, 0, 0, 0);
        UpdateRecordingStatus();
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        SaveSettingsFromUi();
        if (_recorder is not null)
        {
            // Finish writing the file before the process exits.
            args.Handled = true;
            await StopRecordingAsync();
            _audio.Dispose();
            Close();
            return;
        }
        _audio.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

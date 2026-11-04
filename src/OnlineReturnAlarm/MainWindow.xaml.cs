using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OnlineReturnAlarm;

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OnlineReturnAlarm",
        "settings.json");

    private AppSettings _settings;
    private CancellationTokenSource? _monitorCts;
    private bool? _stableOnline;
    private int _successCount;
    private int _failureCount;
    private bool _alarmActive;
    private DispatcherTimer? _flashTimer;
    private bool _flashPhase;
    private WaveOutEvent? _waveOut;
    private LoopStream? _loopStream;
    private string? _savedAudioDeviceId;
    private float _savedVolume;
    private bool _savedMute;
    private bool _volumeWasChanged;

    public MainWindow()
    {
        InitializeComponent();

        _settings = LoadSettings();
        SoundCheck.IsChecked = _settings.PlaySound;
        RaiseVolumeCheck.IsChecked = _settings.RaiseWindowsVolume;
        VolumeSlider.Value = _settings.TargetVolume;
        BringToFrontCheck.IsChecked = _settings.BringToFront;
        AlwaysOnTopCheck.IsChecked = _settings.AlwaysOnTop;
        FlashCheck.IsChecked = _settings.FlashWarning;

        Topmost = _settings.AlwaysOnTop;
        AddLog("アプリを起動しました。監視はまだ開始されていません。");
        UpdateVolumeLabel();
    }

    private async void StartMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorCts is not null)
            return;

        _monitorCts = new CancellationTokenSource();
        _stableOnline = null;
        _successCount = 0;
        _failureCount = 0;

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusBadge.Text = "監視中";
        StatusDot.Fill = Brush("#39C98A");
        ConnectionTitle.Text = "接続状態を確認しています";
        ConnectionDetail.Text = "外部HTTPS接続を2回確認して初期状態を判定します。";
        AlertHeadline.Text = "監視中";
        AlertDescription.Text = "オフラインからオンラインへの復帰を待っています。";
        AddLog("ネットワーク監視を開始しました。");

        await MonitorLoopAsync(_monitorCts.Token);
    }

    private void StopMonitoring_Click(object sender, RoutedEventArgs e)
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusBadge.Text = "停止中";
        StatusDot.Fill = Brush("#8494AA");
        ConnectionTitle.Text = "監視を停止しました";
        ConnectionDetail.Text = "再開するには「監視を開始」を押してください。";
        AddLog("ネットワーク監視を停止しました。");
        StopAlarm();
    }

    private void TestAlarm_Click(object sender, RoutedEventArgs e)
    {
        AddLog("アラームのテストを開始しました。");
        StartAlarm(isTest: true);
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        try
        {
            do
            {
                bool online = await CheckInternetAsync(cancellationToken);

                if (online)
                {
                    _successCount++;
                    _failureCount = 0;
                }
                else
                {
                    _failureCount++;
                    _successCount = 0;
                }

                if (_stableOnline is null)
                {
                    if (_successCount >= 2)
                    {
                        _stableOnline = true;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            SetConnectionOnline(initial: true);
                            AddLog("初期状態をオンラインと判定しました。");
                        });
                    }
                    else if (_failureCount >= 2)
                    {
                        _stableOnline = false;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            SetConnectionOffline();
                            AddLog("オフライン状態を検出しました。復帰を待機します。");
                        });
                    }
                }
                else if (_stableOnline == true && _failureCount >= 2)
                {
                    _stableOnline = false;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        SetConnectionOffline();
                        AddLog("インターネット接続が切断されました。");
                    });
                }
                else if (_stableOnline == false && _successCount >= 2)
                {
                    _stableOnline = true;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        SetConnectionOnline(initial: false);
                        AddLog("インターネット接続の復帰を検出しました。");
                        StartAlarm(isTest: false);
                    });
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ConnectionDetail.Text = online
                            ? "通信を確認中です（復帰判定には連続2回の成功が必要）。"
                            : "通信を確認中です（切断判定には連続2回の失敗が必要）。";
                    });
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Expected when the user stops monitoring.
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                AddLog($"監視エラー: {ex.Message}");
                ConnectionDetail.Text = "監視中にエラーが発生しました。";
            });
        }
        finally
        {
            if (ReferenceEquals(_monitorCts?.Token, cancellationToken) == false)
                return;
        }
    }

    private static async Task<bool> CheckInternetAsync(CancellationToken cancellationToken)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
            return false;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://www.msftconnecttest.com/connecttest.txt");
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains("Microsoft Connect Test", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://www.google.com/generate_204");
            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            return response.StatusCode == HttpStatusCode.NoContent;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return false;
        }
    }

    private void SetConnectionOnline(bool initial)
    {
        ConnectionTitle.Text = "オンライン";
        ConnectionDetail.Text = initial
            ? "インターネット接続を確認しました。復帰イベントを待機しています。"
            : "インターネットへの接続が戻りました。";
        StatusBadge.Text = "オンライン";
        StatusDot.Fill = Brush("#39C98A");
        if (initial)
        {
            AlertHeadline.Text = "待機状態";
            AlertDescription.Text = "現在オンラインです。いったん切断され、その後復帰したときに警告します。";
        }
    }

    private void SetConnectionOffline()
    {
        ConnectionTitle.Text = "オフライン";
        ConnectionDetail.Text = "外部インターネット接続を確認できません。復帰を待っています。";
        StatusBadge.Text = "オフライン";
        StatusDot.Fill = Brush("#F0A34A");
        AlertHeadline.Text = "復帰待機中";
        AlertDescription.Text = "インターネット接続が戻るとアラームを開始します。";
    }

    private void StartAlarm(bool isTest)
    {
        if (_alarmActive)
            StopAlarm();

        _alarmActive = true;
        AlertHeadline.Text = isTest ? "アラームテスト中" : "接続復帰を検知！";
        AlertDescription.Text = isTest
            ? "テスト音と警告表示を確認できます。"
            : "インターネット接続が復帰しました。停止ボタンでアラームを止められます。";
        AlertPanel.Background = Brush("#5B1D27");
        AlertPanel.BorderBrush = Brush("#F05A68");

        if (BringToFrontCheck.IsChecked == true)
            BringWindowToFront();

        if (FlashCheck.IsChecked == true)
            StartVisualPulse();

        if (RaiseVolumeCheck.IsChecked == true)
        {
            try
            {
                SetWindowsVolume((float)(VolumeSlider.Value / 100.0));
                AddLog($"Windowsマスター音量を{VolumeSlider.Value:0}%に変更しました（停止時に復元）。");
            }
            catch (Exception ex)
            {
                AddLog($"Windows音量を変更できませんでした: {ex.Message}");
            }
        }

        if (SoundCheck.IsChecked == true)
        {
            try
            {
                StartAlarmTone();
            }
            catch (Exception ex)
            {
                AddLog($"アラーム音を再生できませんでした: {ex.Message}");
            }
        }

        AddLog(isTest ? "テストアラームを再生中です。" : "復帰アラームを再生中です。");
    }

    private void StopAlarm()
    {
        _alarmActive = false;
        _flashTimer?.Stop();
        _flashTimer = null;

        try
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _loopStream?.Dispose();
            _loopStream = null;
        }
        catch
        {
            // Best-effort audio shutdown.
        }

        RestoreWindowsVolume();

        AlertPanel.Background = Brush("#202C3D");
        AlertPanel.BorderBrush = Brush("#344A65");
        if (_stableOnline == true)
        {
            AlertHeadline.Text = "オンライン・待機中";
            AlertDescription.Text = "アラームを停止しました。次の切断と復帰を待っています。";
        }
        AddLog("アラームを停止しました。");
    }

    private void SetWindowsVolume(float target)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        if (!_volumeWasChanged)
        {
            _savedAudioDeviceId = device.ID;
            _savedVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
            _savedMute = device.AudioEndpointVolume.Mute;
            _volumeWasChanged = true;
        }

        device.AudioEndpointVolume.Mute = false;

        float start = device.AudioEndpointVolume.MasterVolumeLevelScalar;
        const int steps = 8;
        for (int i = 1; i <= steps; i++)
        {
            float value = start + (target - start) * i / steps;
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
            Thread.Sleep(35);
        }
    }

    private void RestoreWindowsVolume()
    {
        if (!_volumeWasChanged || string.IsNullOrWhiteSpace(_savedAudioDeviceId))
            return;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(_savedAudioDeviceId);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = _savedVolume;
            device.AudioEndpointVolume.Mute = _savedMute;
            AddLog("変更前のWindows音量とミュート状態を復元しました。");
        }
        catch (Exception ex)
        {
            AddLog($"音量の復元に失敗しました: {ex.Message}");
        }
        finally
        {
            _volumeWasChanged = false;
            _savedAudioDeviceId = null;
        }
    }

    private void StartAlarmTone()
    {
        const int sampleRate = 44100;
        const double durationSeconds = 1.2;
        const double amplitude = 0.085;
        int sampleCount = (int)(sampleRate * durationSeconds);
        byte[] pcm = new byte[sampleCount * 2];
        double phase = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            double time = (double)i / sampleRate;
            double within = time % 0.9;
            bool active = within < 0.19 || (within >= 0.29 && within < 0.48);
            double local = within < 0.19 ? within / 0.19 : (within - 0.29) / 0.19;
            double frequency = 1450 + 850 * local + 170 * Math.Sin(time * 19);
            double edge = active ? Math.Min(1.0, Math.Min(local * 18, (1.0 - local) * 18)) : 0.0;
            double sample = active
                ? Math.Sin(phase) * amplitude * edge
                : 0.0;

            phase += 2.0 * Math.PI * frequency / sampleRate;
            short value = (short)(Math.Clamp(sample, -1.0, 1.0) * short.MaxValue);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        var format = new WaveFormat(sampleRate, 16, 1);
        _loopStream = new LoopStream(new RawSourceWaveStream(new MemoryStream(pcm), format));
        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 120,
            Volume = 1.0f
        };
        _waveOut.Init(_loopStream);
        _waveOut.Play();
    }

    private void StartVisualPulse()
    {
        _flashTimer?.Stop();
        _flashPhase = false;
        _flashTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(850)
        };
        _flashTimer.Tick += (_, _) =>
        {
            if (!_alarmActive)
                return;
            _flashPhase = !_flashPhase;
            AlertPanel.Background = _flashPhase ? Brush("#5B1D27") : Brush("#33202A");
            AlertPanel.BorderBrush = _flashPhase ? Brush("#F05A68") : Brush("#A64A59");
        };
        _flashTimer.Start();
    }

    private void BringWindowToFront()
    {
        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Topmost = AlwaysOnTopCheck.IsChecked == true;
        Activate();

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetForegroundWindow(handle);
            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                WindowHandle = handle,
                Flags = 3,
                Count = 4,
                Timeout = 0
            };
            FlashWindowEx(ref info);
        }
    }

    private void AlwaysOnTop_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = AlwaysOnTopCheck.IsChecked == true;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateVolumeLabel();
    }

    private void UpdateVolumeLabel()
    {
        if (VolumePercentLabel is not null && VolumeSlider is not null)
            VolumePercentLabel.Text = $"{VolumeSlider.Value:0}%";
    }

    private void AddLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AddLog(message));
            return;
        }

        LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (LogList.Items.Count > 80)
            LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath));
                if (settings is not null)
                    return settings;
            }
        }
        catch
        {
            // Use safe defaults if the settings file is unreadable.
        }

        return new AppSettings();
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            _settings = new AppSettings
            {
                PlaySound = SoundCheck.IsChecked == true,
                RaiseWindowsVolume = RaiseVolumeCheck.IsChecked == true,
                TargetVolume = VolumeSlider.Value,
                BringToFront = BringToFrontCheck.IsChecked == true,
                AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true,
                FlashWarning = FlashCheck.IsChecked == true
            };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            AddLog($"設定を保存できませんでした: {ex.Message}");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        StopAlarm();
        SaveSettings();
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private sealed class AppSettings
    {
        public bool PlaySound { get; set; } = true;
        public bool RaiseWindowsVolume { get; set; } = false;
        public double TargetVolume { get; set; } = 70;
        public bool BringToFront { get; set; } = true;
        public bool AlwaysOnTop { get; set; } = true;
        public bool FlashWarning { get; set; } = true;
    }

    private sealed class LoopStream : WaveStream
    {
        private readonly WaveStream _source;

        public LoopStream(WaveStream source) => _source = source;

        public override WaveFormat WaveFormat => _source.WaveFormat;
        public override long Length => long.MaxValue;
        public override long Position
        {
            get => _source.Position;
            set => _source.Position = value % _source.Length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = _source.Read(buffer, offset + total, count - total);
                if (read == 0)
                    _source.Position = 0;
                else
                    total += read;
            }
            return total;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _source.Dispose();
            base.Dispose(disposing);
        }
    }
}

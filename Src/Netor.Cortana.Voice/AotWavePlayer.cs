using System.Runtime.InteropServices;

namespace Netor.Cortana.Voice;

/// <summary>
/// AOT 兼容的音频播放器。使用 winmm.dll waveOut* API + [LibraryImport] + struct WAVEHDR。
/// 替代 NAudio 的 WaveOutEvent，解决 class WaveHeader 在 Native AOT 下 marshalling 失败的问题。
/// </summary>
internal sealed unsafe class AotWavePlayer : IDisposable
{
    private const int CALLBACK_EVENT = 0x00050000;
    private const int WAVE_MAPPER = -1;
    private const uint WHDR_DONE = 0x00000001;

    private IntPtr _hWaveOut;
    private readonly PlayBuffer[] _buffers;
    private readonly AutoResetEvent _callbackEvent = new(false);
    private readonly IAudioDataProvider _provider;
    private Thread? _playThread;
    private volatile bool _playing;

    /// <summary>播放停止时触发，参数为异常（正常停止时 null）。</summary>
    public event Action<Exception?>? PlaybackStopped;

    public AotWavePlayer(IAudioDataProvider provider, int desiredLatency = 200, int numberOfBuffers = 2)
    {
        _provider = provider;

        var wfx = provider.GetWaveFormatEx();
        int bufferSize = (int)(wfx.nAvgBytesPerSec * desiredLatency / 1000);

        int result = WinMmNative.waveOutOpen(out _hWaveOut, WAVE_MAPPER, in wfx,
            _callbackEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
        if (result != 0)
            throw new InvalidOperationException($"waveOutOpen failed with error {result}");

        _buffers = new PlayBuffer[numberOfBuffers];
        for (int i = 0; i < numberOfBuffers; i++)
            _buffers[i] = new PlayBuffer(_hWaveOut, bufferSize);
    }

    public void Play()
    {
        _playing = true;
        _playThread = new Thread(DoPlayback) { IsBackground = true, Name = "AotWavePlayer" };
        _playThread.Start();
    }

    public void Stop()
    {
        _playing = false;
        _callbackEvent.Set();
        _playThread?.Join(TimeSpan.FromSeconds(3));
        WinMmNative.waveOutReset(_hWaveOut);
    }

    private void DoPlayback()
    {
        Exception? exception = null;
        try
        {
            // 初始填充所有缓冲区并排队
            foreach (var buf in _buffers)
            {
                if (!FillAndQueue(buf))
                {
                    // 数据不足一个缓冲区即全部填完
                    break;
                }
            }

            while (_playing)
            {
                if (!_callbackEvent.WaitOne(300))
                    continue;
                if (!_playing) break;

                bool anyQueued = false;
                foreach (var buf in _buffers)
                {
                    if (buf.IsDone)
                    {
                        if (FillAndQueue(buf))
                            anyQueued = true;
                    }
                    else
                    {
                        anyQueued = true;
                    }
                }

                // 如果所有缓冲区都 done 且无法再填充，说明播放完毕
                if (!anyQueued)
                    break;
            }
        }
        catch (Exception ex) { exception = ex; }
        finally
        {
            _playing = false;
        }
        PlaybackStopped?.Invoke(exception);
    }

    private bool FillAndQueue(PlayBuffer buf)
    {
        int bytesRead = _provider.Read(buf.Data, 0, buf.Data.Length);
        if (bytesRead <= 0) return false;
        buf.Queue(bytesRead);
        return true;
    }

    public void Dispose()
    {
        Stop();
        foreach (var buf in _buffers)
            buf.Dispose();
        if (_hWaveOut != IntPtr.Zero)
        {
            WinMmNative.waveOutClose(_hWaveOut);
            _hWaveOut = IntPtr.Zero;
        }
        _callbackEvent.Dispose();
    }

    // ────────── 播放缓冲区 ──────────

    private sealed class PlayBuffer : IDisposable
    {
        private readonly IntPtr _hWaveOut;
        private readonly byte[] _data;
        private readonly GCHandle _dataPin;
        private readonly WAVEHDR* _pHeader;

        public PlayBuffer(IntPtr hWaveOut, int bufferSize)
        {
            _hWaveOut = hWaveOut;
            _data = new byte[bufferSize];
            _dataPin = GCHandle.Alloc(_data, GCHandleType.Pinned);

            _pHeader = (WAVEHDR*)NativeMemory.AllocZeroed((nuint)sizeof(WAVEHDR));
            _pHeader->lpData = _dataPin.AddrOfPinnedObject();
            _pHeader->dwBufferLength = (uint)bufferSize;
        }

        public byte[] Data => _data;
        public bool IsDone => (_pHeader->dwFlags & WHDR_DONE) != 0 || _pHeader->dwFlags == 0;

        public void Queue(int bytesWritten)
        {
            Unprepare();
            _pHeader->dwBufferLength = (uint)bytesWritten;
            _pHeader->dwBytesRecorded = 0;
            _pHeader->dwFlags = 0;
            Prepare();

            int r = WinMmNative.waveOutWrite(_hWaveOut, (IntPtr)_pHeader, sizeof(WAVEHDR));
            if (r != 0) throw new InvalidOperationException($"waveOutWrite failed: {r}");
        }

        private void Prepare()
        {
            int r = WinMmNative.waveOutPrepareHeader(_hWaveOut, (IntPtr)_pHeader, sizeof(WAVEHDR));
            if (r != 0) throw new InvalidOperationException($"waveOutPrepareHeader failed: {r}");
        }

        private void Unprepare()
        {
            WinMmNative.waveOutUnprepareHeader(_hWaveOut, (IntPtr)_pHeader, sizeof(WAVEHDR));
        }

        public void Dispose()
        {
            Unprepare();
            NativeMemory.Free(_pHeader);
            if (_dataPin.IsAllocated) _dataPin.Free();
        }
    }
}

/// <summary>
/// 音频数据提供接口，替代 NAudio 的 IWaveProvider。
/// </summary>
internal interface IAudioDataProvider
{
    WAVEFORMATEX GetWaveFormatEx();
    int Read(byte[] buffer, int offset, int count);
}

// ────────── waveOut P/Invoke ──────────

internal static partial class WinMmNative
{
    [LibraryImport("winmm.dll")]
    public static partial int waveOutOpen(out IntPtr phwo, int uDeviceID,
        in WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);

    [LibraryImport("winmm.dll")]
    public static partial int waveOutPrepareHeader(IntPtr hwo, IntPtr lpWaveOutHdr, int cbWaveOutHdr);

    [LibraryImport("winmm.dll")]
    public static partial int waveOutUnprepareHeader(IntPtr hwo, IntPtr lpWaveOutHdr, int cbWaveOutHdr);

    [LibraryImport("winmm.dll")]
    public static partial int waveOutWrite(IntPtr hwo, IntPtr lpWaveOutHdr, int cbWaveOutHdr);

    [LibraryImport("winmm.dll")]
    public static partial int waveOutReset(IntPtr hwo);

    [LibraryImport("winmm.dll")]
    public static partial int waveOutClose(IntPtr hwo);
}

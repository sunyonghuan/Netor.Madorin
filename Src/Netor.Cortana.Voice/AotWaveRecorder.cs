using System.Runtime.InteropServices;

namespace Netor.Cortana.Voice;

/// <summary>
/// AOT 兼容的音频录制器。使用 winmm.dll + [LibraryImport] + struct WAVEHDR 替代 NAudio 的 WaveInEvent。
/// NAudio 的 WaveHeader 是 class 类型，在 Native AOT 下 P/Invoke marshalling 无法正确回写 WHDR_PREPARED 标志，
/// 导致 waveInAddBuffer 返回 WaveHeaderUnprepared 错误。
/// </summary>
internal sealed unsafe class AotWaveRecorder : IDisposable
{
    private const int CALLBACK_EVENT = 0x00050000;
    private const int WAVE_MAPPER = -1;
    private const uint WHDR_DONE = 0x00000001;

    private IntPtr _hWaveIn;
    private readonly RecordBuffer[] _buffers;
    private readonly AutoResetEvent _callbackEvent = new(false);
    private Thread? _recordThread;
    private volatile bool _recording;

    /// <summary>
    /// 音频数据就绪回调。参数：(byte[] buffer, int bytesRecorded)。
    /// 在录制线程上触发，调用方应尽快处理或入队。
    /// </summary>
    public event Action<byte[], int>? DataAvailable;

    /// <summary>
    /// 录制停止回调。参数：异常（正常停止时为 null）。
    /// </summary>
    public event Action<Exception?>? RecordingStopped;

    public AotWaveRecorder(int sampleRate = 16000, int bitsPerSample = 16, int channels = 1,
        int bufferMilliseconds = 100, int numberOfBuffers = 3)
    {
        int bufferSize = bufferMilliseconds * sampleRate * channels * (bitsPerSample / 8) / 1000;

        var wfx = new WAVEFORMATEX
        {
            wFormatTag = 1, // WAVE_FORMAT_PCM
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = (uint)(sampleRate * channels * bitsPerSample / 8),
            nBlockAlign = (ushort)(channels * bitsPerSample / 8),
            wBitsPerSample = (ushort)bitsPerSample,
            cbSize = 0
        };

        int result = WinMmNative.waveInOpen(out _hWaveIn, WAVE_MAPPER, in wfx,
            _callbackEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
        if (result != 0)
            throw new InvalidOperationException($"waveInOpen failed with error {result}");

        _buffers = new RecordBuffer[numberOfBuffers];
        for (int i = 0; i < numberOfBuffers; i++)
            _buffers[i] = new RecordBuffer(_hWaveIn, bufferSize);
    }

    /// <summary>可用音频输入设备数量。</summary>
    public static int DeviceCount => WinMmNative.waveInGetNumDevs();

    public void StartRecording()
    {
        foreach (var buf in _buffers)
            buf.QueueBuffer();

        int result = WinMmNative.waveInStart(_hWaveIn);
        if (result != 0)
            throw new InvalidOperationException($"waveInStart failed with error {result}");

        _recording = true;
        _recordThread = new Thread(DoRecording) { IsBackground = true, Name = "AotWaveRecorder" };
        _recordThread.Start();
    }

    public void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        _callbackEvent.Set();
        _recordThread?.Join(TimeSpan.FromSeconds(3));
        WinMmNative.waveInStop(_hWaveIn);
        WinMmNative.waveInReset(_hWaveIn);
    }

    private void DoRecording()
    {
        Exception? exception = null;
        try
        {
            while (_recording)
            {
                if (!_callbackEvent.WaitOne(300))
                    continue;
                if (!_recording) break;

                foreach (var buf in _buffers)
                {
                    if (buf.IsDone)
                    {
                        int bytesRecorded = buf.BytesRecorded;
                        if (bytesRecorded > 0)
                            DataAvailable?.Invoke(buf.Data, bytesRecorded);
                        if (_recording)
                            buf.Requeue();
                    }
                }
            }
        }
        catch (Exception ex) { exception = ex; }
        RecordingStopped?.Invoke(exception);
    }

    public void Dispose()
    {
        StopRecording();
        foreach (var buf in _buffers)
            buf.Dispose();
        if (_hWaveIn != IntPtr.Zero)
        {
            WinMmNative.waveInClose(_hWaveIn);
            _hWaveIn = IntPtr.Zero;
        }
        _callbackEvent.Dispose();
    }

    // ────────── 录制缓冲区 ──────────

    private sealed class RecordBuffer : IDisposable
    {
        private readonly IntPtr _hWaveIn;
        private readonly byte[] _data;
        private readonly GCHandle _dataPin;
        private readonly WAVEHDR* _pHeader;

        public RecordBuffer(IntPtr hWaveIn, int bufferSize)
        {
            _hWaveIn = hWaveIn;
            _data = new byte[bufferSize];
            _dataPin = GCHandle.Alloc(_data, GCHandleType.Pinned);

            _pHeader = (WAVEHDR*)NativeMemory.AllocZeroed((nuint)sizeof(WAVEHDR));
            _pHeader->lpData = _dataPin.AddrOfPinnedObject();
            _pHeader->dwBufferLength = (uint)bufferSize;

            Prepare();
        }

        public byte[] Data => _data;
        public int BytesRecorded => (int)_pHeader->dwBytesRecorded;
        public bool IsDone => (_pHeader->dwFlags & WHDR_DONE) != 0;

        public void QueueBuffer()
        {
            int r = WinMmNative.waveInAddBuffer(_hWaveIn, (IntPtr)_pHeader, sizeof(WAVEHDR));
            if (r != 0) throw new InvalidOperationException($"waveInAddBuffer failed: {r}");
        }

        public void Requeue()
        {
            Unprepare();
            _pHeader->dwBytesRecorded = 0;
            _pHeader->dwFlags = 0;
            Prepare();
            QueueBuffer();
        }

        private void Prepare()
        {
            int r = WinMmNative.waveInPrepareHeader(_hWaveIn, (IntPtr)_pHeader, sizeof(WAVEHDR));
            if (r != 0) throw new InvalidOperationException($"waveInPrepareHeader failed: {r}");
        }

        private void Unprepare()
        {
            WinMmNative.waveInUnprepareHeader(_hWaveIn, (IntPtr)_pHeader, sizeof(WAVEHDR));
        }

        public void Dispose()
        {
            Unprepare();
            NativeMemory.Free(_pHeader);
            if (_dataPin.IsAllocated) _dataPin.Free();
        }
    }
}

// ────────── winmm.dll P/Invoke（AOT 安全） ──────────

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEHDR
{
    public IntPtr lpData;
    public uint dwBufferLength;
    public uint dwBytesRecorded;
    public IntPtr dwUser;
    public uint dwFlags;
    public uint dwLoops;
    public IntPtr lpNext;
    public IntPtr reserved;
}

internal static partial class WinMmNative
{
    [LibraryImport("winmm.dll")]
    public static partial int waveInGetNumDevs();

    [LibraryImport("winmm.dll")]
    public static partial int waveInOpen(out IntPtr phwi, int uDeviceID,
        in WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int fdwOpen);

    [LibraryImport("winmm.dll")]
    public static partial int waveInPrepareHeader(IntPtr hwi, IntPtr lpWaveInHdr, int cbWaveInHdr);

    [LibraryImport("winmm.dll")]
    public static partial int waveInUnprepareHeader(IntPtr hwi, IntPtr lpWaveInHdr, int cbWaveInHdr);

    [LibraryImport("winmm.dll")]
    public static partial int waveInAddBuffer(IntPtr hwi, IntPtr lpWaveInHdr, int cbWaveInHdr);

    [LibraryImport("winmm.dll")]
    public static partial int waveInStart(IntPtr hwi);

    [LibraryImport("winmm.dll")]
    public static partial int waveInStop(IntPtr hwi);

    [LibraryImport("winmm.dll")]
    public static partial int waveInReset(IntPtr hwi);

    [LibraryImport("winmm.dll")]
    public static partial int waveInClose(IntPtr hwi);
}

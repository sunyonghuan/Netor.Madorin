using System.Collections.Concurrent;

namespace Netor.Cortana.Voice;

/// <summary>
/// 分段式音频提供器，实现 <see cref="IAudioDataProvider"/>。
/// 内部按句子维护音频队列，当播放线程切换到新段时触发字幕回调，
/// 确保字幕显示与声卡实际输出精确同步。
/// </summary>
/// <remarks>
/// 创建分段式音频提供器。
/// </remarks>
/// <param name="sampleRate">采样率。</param>
/// <param name="channels">通道数。</param>
/// <param name="onSegmentStarted">
/// 当新段开始被声卡读取时触发的回调，参数为该段对应的文本。
/// 注意：此回调在播放线程上执行，应尽量轻量。
/// </param>
internal sealed class SegmentedWaveProvider(int sampleRate, int channels, Action<string> onSegmentStarted) : IAudioDataProvider, IDisposable
{
    private readonly ConcurrentQueue<AudioSegment> _segments = new();
    private readonly ManualResetEventSlim _dataAvailable = new(false);
    private AudioSegment? _current;
    private volatile bool _completed;

    public WAVEFORMATEX GetWaveFormatEx() => new()
    {
        wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
        nChannels = (ushort)channels,
        nSamplesPerSec = (uint)sampleRate,
        nAvgBytesPerSec = (uint)(sampleRate * channels * sizeof(float)),
        nBlockAlign = (ushort)(channels * sizeof(float)),
        wBitsPerSample = 32,
        cbSize = 0
    };

    /// <summary>
    /// 将一句话的音频数据入队。
    /// </summary>
    /// <param name="samples">PCM 采样数据（float[]）。</param>
    /// <param name="text">对应的文本（用于字幕）。</param>
    public void AddSegment(float[] samples, string text)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        _segments.Enqueue(new AudioSegment(bytes, text));
        _dataAvailable.Set();
    }

    /// <summary>
    /// 标记所有音频数据已入队，当队列消费完毕后 Read 将返回 0，WaveOutEvent 自动停止。
    /// </summary>
    public void Complete()
    {
        _completed = true;
        _dataAvailable.Set();
    }

    /// <summary>
    /// 由 WaveOutEvent 播放线程调用，拉取音频数据。
    /// </summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        var totalRead = 0;

        while (totalRead < count)
        {
            // 如果当前段已读完，尝试切换到下一段
            if (_current is null || _current.IsConsumed)
            {
                if (_segments.TryDequeue(out var next))
                {
                    _current = next;
                    onSegmentStarted(next.Text);
                }
                else if (_completed)
                {
                    // 所有数据播完，返回已读取量（可能为 0），WaveOutEvent 会自动停止
                    break;
                }
                else
                {
                    // 队列暂时为空但还没完成，填充静音保持播放线程活着
                    var silence = count - totalRead;
                    Array.Clear(buffer, offset + totalRead, silence);
                    totalRead += silence;
                    break;
                }
            }

            // 从当前段拷贝数据
            var remaining = count - totalRead;
            var read = _current.Read(buffer, offset + totalRead, remaining);
            totalRead += read;
        }

        return totalRead;
    }

    /// <summary>
    /// 释放内部等待句柄。
    /// </summary>
    public void Dispose()
    {
        _dataAvailable.Dispose();
    }

    /// <summary>
    /// 表示一句话的音频数据段。
    /// </summary>
    private sealed class AudioSegment(byte[] data, string text)
    {
        private int _position;

        public string Text => text;
        public bool IsConsumed => _position >= data.Length;

        /// <summary>
        /// 从当前段读取数据到目标缓冲区。
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            var available = data.Length - _position;
            var toCopy = Math.Min(available, count);
            System.Buffer.BlockCopy(data, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }
}

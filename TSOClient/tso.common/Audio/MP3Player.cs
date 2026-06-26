using Microsoft.Xna.Framework.Audio;
using MP3Sharp;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FSO.Common.Audio
{
    /// <summary>
    /// An MP3 audio player that streams and decodes MP3 files into 
    /// MonoGame's DynamicSoundEffectInstance for playback.
    /// </summary>
    public class MP3Player : ISFXInstanceLike, IDisposable
    {
        public static bool NewMode = true;

        private MP3Stream? _stream;
        private DynamicSoundEffectInstance? _inst;

        /// <summary>
        /// Queue holding decoded audio buffers ready for playback.
        /// </summary>
        private readonly ConcurrentQueue<(byte[] Buffer, int Size)> _nextBuffers = new();

        /// <summary>
        /// Semaphore controlling the number of available buffers.
        /// </summary>
        private readonly SemaphoreSlim _bufferCount = new(0);

        private CancellationTokenSource? _cts;
        private Task? _decoderTask;

        public int SendExtra = 2;

        private bool _endOfStream;
        private bool _disposed;

        private SoundState _state = SoundState.Stopped;
        private float _volume = 1f;
        private float _pan;

        /// <summary>
        /// Lock object for synchronizing access to control properties and state changes.
        /// </summary>
        private readonly object _controlLock = new();

        private readonly string _path;

        /// <summary>
        /// Blank buffer used when no audio data is available.
        /// </summary>
        private static readonly byte[] _blank = new byte[65536];

        // Tunables
        private readonly int _bufferSize;
        private readonly int _initialBuffers;
        private readonly int _maxBuffers;

        private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
        private readonly bool _preload;


        /// <summary>
        /// Initializes a new MP3Player instance with default buffering options.
        /// </summary>
        /// <param name="path">Path to the MP3 file.</param>
        /// </param>
        public MP3Player(string path)
            : this(path, preload: false, bufferSize: 262144, initialBuffers: 6, maxBuffers: 12) { }


        /// <summary>
        /// Initializes a new MP3Player instance with custom buffering and preload options.
        /// </summary>
        /// <param name="path">Path to the MP3 file.</param>
        /// <param name="preload">If true, loads the entire MP3 into memory.</param>
        /// <param name="bufferSize">Size of each buffer chunk in bytes.</param>
        /// <param name="initialBuffers">Number of buffers to prefill before playback.</param>
        /// <param name="maxBuffers">Maximum number of buffers in the queue.</param>
        public MP3Player(string path, bool preload = false, int bufferSize = 262144, int initialBuffers = 6, int maxBuffers = 12)
        {
            _path = path;
            _preload = preload;
            _bufferSize = Math.Max(16384, bufferSize);
            _initialBuffers = Math.Max(1, initialBuffers);
            _maxBuffers = Math.Max(_initialBuffers, maxBuffers);

            Task.Run(Start);
        }

        /// <summary>
        /// Starts decoding the MP3 file and prepares the DynamicSoundEffectInstance.
        /// Runs on a background task and manages buffers asynchronously.
        /// </summary>
        private void Start()
        {
            try
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;

                _stream = new MP3Stream(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read));
                _stream.DecodeFrames(1);
                var freq = _stream.Frequency;

                lock (_controlLock)
                {
                    if (_disposed) return;

                    _inst = new DynamicSoundEffectInstance(freq, AudioChannels.Stereo)
                    {
                        IsLooped = false,
                        Volume = _volume,
                        Pan = _pan
                    };
                    _inst.BufferNeeded += SubmitBufferAsync;

                    switch (_state)
                    {
                        case SoundState.Playing:
                            _inst.Play();
                            break;
                        case SoundState.Paused:
                            _inst.Play();
                            _inst.Pause();
                            break;
                    }
                }

                if (_preload)
                {
                    PreloadStream();
                    return;
                }

                PrefillBuffers(token);

                _decoderTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested && !_endOfStream)
                        {
                            if (_nextBuffers.Count < _maxBuffers)
                            {
                                var rent = _pool.Rent(_bufferSize);
                                int read = _stream.Read(rent, 0, rent.Length);

                                if (read <= 0)
                                {
                                    _pool.Return(rent);
                                    _endOfStream = true;
                                    break;
                                }

                                _nextBuffers.Enqueue((rent, read));
                                _bufferCount.Release();
                                continue; // fill aggressively
                            }

                            await Task.Delay(12, token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { }
                }, token);
            }
            catch
            {
                _endOfStream = true;
            }
        }

        private void PreloadStream()
        {
            using var ms = new MemoryStream();
            var tmp = _pool.Rent(_bufferSize);
            try
            {
                int read;
                while ((read = _stream!.Read(tmp, 0, tmp.Length)) > 0)
                {
                    ms.Write(tmp, 0, read);
                }
            }
            finally
            {
                _pool.Return(tmp);
            }

            if (ms.Length > 0)
            {
                _inst?.SubmitBuffer(ms.ToArray(), 0, (int)ms.Length);
                _endOfStream = true;
            }
        }

        private void PrefillBuffers(CancellationToken token)
        {
            for (int i = 0; i < _initialBuffers && !token.IsCancellationRequested; i++)
            {
                var buf = _pool.Rent(_bufferSize);
                int bytesRead = _stream!.Read(buf, 0, _bufferSize);

                if (bytesRead <= 0)
                {
                    _pool.Return(buf);
                    _endOfStream = true;
                    break;
                }

                _nextBuffers.Enqueue((buf, bytesRead));
                _bufferCount.Release();
            }
        }

        public void Play() { SetState(SoundState.Playing, s => s.Play()); }
        public void Stop() { SetState(SoundState.Stopped, s => s.Stop()); }
        public void Pause() { SetState(SoundState.Paused, s => s.Pause()); }
        public void Resume() { SetState(SoundState.Playing, s => s.Resume()); }

        private void SetState(SoundState state, Action<DynamicSoundEffectInstance> action)
        {
            lock (_controlLock)
            {
                _state = state;
                if (_inst != null) action(_inst);
            }
        }

        /// <summary>
        /// Disposes the MP3Player, releasing all buffers, stopping playback, and canceling decoding.
        /// </summary>
        public void Dispose()
        {
            lock (_controlLock)
            {
                if (_disposed) return;

                _disposed = true;
                _cts?.Cancel();
                _inst?.Dispose();
                _stream?.Dispose();
            }

            while (_nextBuffers.TryDequeue(out var tuple))
            {
                _pool.Return(tuple.Buffer);
            }

            while (_bufferCount.CurrentCount > 0) _bufferCount.Wait(0);
            try { _decoderTask?.Wait(50); } catch { }

            GC.SuppressFinalize(this);
        }

        public bool IsEnded() => _endOfStream && _inst?.PendingBufferCount == 0;

        public float Volume
        {
            get { lock (_controlLock) return _inst?.Volume ?? _volume; }
            // Clamp 0..1: callers (e.g. HIT fade volumes derived from the live RefreshRate) can briefly
            // overshoot, and DynamicSoundEffectInstance.Volume throws on out-of-range.
            set { SetControlProperty(ref _volume, (value < 0f) ? 0f : (value > 1f ? 1f : value), (inst, v) => inst.Volume = v); }
        }

        public float Pan
        {
            get { lock (_controlLock) return _inst?.Pan ?? _pan; }
            // Pan is valid in -1..1; clamp so an out-of-range value can't throw.
            set { SetControlProperty(ref _pan, (value < -1f) ? -1f : (value > 1f ? 1f : value), (inst, v) => inst.Pan = v); }
        }

        private void SetControlProperty(ref float backingField, float value, Action<DynamicSoundEffectInstance, float> setter)
        {
            lock (_controlLock)
            {
                backingField = value;
                if (_inst != null) setter(_inst, value);
            }
        }

        public SoundState State { get { lock (_controlLock) return _inst?.State ?? _state; } }

        public bool IsLooped { get; set; }

        private void SubmitBufferAsync(object? sender, EventArgs e)
        {
            if (_endOfStream && _bufferCount.CurrentCount == 0) return;

            if (!_bufferCount.Wait(50))
            {
                _inst?.SubmitBuffer(_blank, 0, _blank.Length);
                return;
            }

            if (_nextBuffers.TryDequeue(out var tuple))
            {
                try
                {
                    if (tuple.Size > 0)
                        _inst?.SubmitBuffer(tuple.Buffer, 0, tuple.Size);
                }
                finally
                {
                    _pool.Return(tuple.Buffer);
                }
            }
            else
            {
                _inst?.SubmitBuffer(_blank, 0, _blank.Length);
            }
        }
    }

    public interface ISFXInstanceLike
    {
        float Volume { get; set; }
        float Pan { get; set; }
        SoundState State { get; }
        bool IsLooped { get; set; }
        void Play();
        void Stop();
        void Pause();
        void Resume();
        void Dispose();
    }
}

using System.Runtime.InteropServices;
using NeonMon.Models;

namespace NeonMon.Services;

// Plays the embedded interaction sounds on one shared-mode WASAPI stream and mixes them in software.
// The stream keeps running, writing silence, while a pulse or Settings is open: starting a stream per sound let
// virtual audio devices (mixers, enhancers) swallow or ramp the first ~50 ms, which is most of a click.
// Volume is applied to the samples, so NeonMon's own slider in the Windows volume mixer never moves.
// Nothing runs while the pulses are hidden; the stream stops a few seconds after the last sound or hold.
internal sealed class InteractionSounds : IDisposable
{
    private const int SampleRate = 48000;
    private const int MaxVoices = 3;
    private const int IdleStopMs = 5000;
    private const int HoverCooldownMs = 120;
    private const int HoverAfterRevealMs = 250;
    private const int RepeatCooldownMs = 100;
    private const int RevealOverlapMs = 150;

    private readonly Func<AudioSettings> _settings;
    private readonly object _gate = new();
    private readonly List<Voice> _voices = [];
    private readonly long[] _lastPlayed = new long[Enum.GetValues<SoundCue>().Length];
    private long _lastReveal = long.MinValue / 2;
    private short[][]? _sources;
    private float[][]? _scaled;
    private int _scaledVolume;
    private Thread? _thread;
    private RenderStream? _stream;
    private bool _held;
    private bool _stopRequested;
    private bool _disposed;

    public InteractionSounds(Func<AudioSettings> settings)
    {
        _settings = settings;
        Array.Fill(_lastPlayed, long.MinValue / 2);
    }

    // Call from the UI thread. The stream runs on its own thread, so a slow driver never stalls the strips.
    public void Play(SoundCue cue, bool preview = false)
    {
        var settings = _settings();
        if (!(preview ? settings.Enabled : settings.Plays(cue)) || !Admit(cue, Environment.TickCount64))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Prepare(settings.EffectiveVolume);
            }
            catch
            {
                return;
            }

            if (_voices.Count >= MaxVoices)
            {
                _voices.RemoveAt(0);
            }

            _voices.Add(new Voice(_scaled![(int)cue]));
            EnsureRunning();
            _stream?.Wake();
        }
    }

    // Opening a stream takes about 50 ms, too late for a hover tick, so it runs while a pulse or Settings is open.
    public void Hold(bool held)
    {
        lock (_gate)
        {
            _held = held && _settings().Enabled && !_disposed;
            if (_held)
            {
                EnsureRunning();
            }
        }
    }

    // Stops the stream and frees the buffers now, for example when sounds are turned off.
    public void Release()
    {
        lock (_gate)
        {
            _held = false;
            _voices.Clear();
            _stopRequested = true;
        }
    }

    private bool Admit(SoundCue cue, long now)
    {
        var reveal = cue is SoundCue.Peek or SoundCue.Unpeek or SoundCue.Open or SoundCue.Hide;
        var allowed = cue switch
        {
            SoundCue.Hover => now - _lastPlayed[(int)cue] >= HoverCooldownMs && now - _lastReveal >= HoverAfterRevealMs,
            // A pulse opening hides the other one; only the first transition is heard.
            SoundCue.Hide or SoundCue.Unpeek => now - _lastPlayed[(int)cue] >= RepeatCooldownMs && now - _lastReveal >= RevealOverlapMs,
            _ => now - _lastPlayed[(int)cue] >= RepeatCooldownMs
        };
        if (allowed)
        {
            _lastPlayed[(int)cue] = now;
            if (reveal)
            {
                _lastReveal = now;
            }
        }

        return allowed;
    }

    // Volume steps follow loudness rather than amplitude, so 50% sounds about half as loud as 100%.
    private void Prepare(int volume)
    {
        _sources ??= [.. Enum.GetValues<SoundCue>().Select(Load)];
        if (_scaled is not null && _scaledVolume == volume)
        {
            return;
        }

        var gain = (float)(Math.Pow(volume / 100.0, 2) / 32768.0);
        _scaled = [.. _sources.Select(source => source.Select(sample => sample * gain).ToArray())];
        _scaledVolume = volume;
    }

    private void EnsureRunning()
    {
        _stopRequested = false;
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "NeonMon sounds", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            using var stream = new RenderStream(SampleRate);
            lock (_gate)
            {
                _stream = stream;
            }

            var idleSince = Environment.TickCount64;
            while (true)
            {
                stream.Wait();
                lock (_gate)
                {
                    var now = Environment.TickCount64;
                    if (_voices.Count > 0 || _held)
                    {
                        idleSince = now;
                    }

                    // Stopping is decided under the lock, so a sound queued from now on starts a new stream.
                    if (_stopRequested || _disposed || now - idleSince >= IdleStopMs)
                    {
                        StopLocked();
                        return;
                    }

                    stream.Fill(Mix);
                }
            }
        }
        catch
        {
            // A removed or changed output device ends the stream; the next sound opens one on the current default device.
            lock (_gate)
            {
                if (_thread == Thread.CurrentThread)
                {
                    StopLocked();
                }
            }
        }
    }

    private void StopLocked()
    {
        _thread = null;
        _stream = null;
        _voices.Clear();
        _scaled = null;
        _sources = null;
    }

    // Fills the buffer from the active voices; returns false when it is all silence.
    private bool Mix(Span<float> buffer)
    {
        buffer.Clear();
        if (_voices.Count == 0)
        {
            return false;
        }

        foreach (var voice in _voices)
        {
            var count = Math.Min(buffer.Length, voice.Samples.Length - voice.Position);
            var source = voice.Samples.AsSpan(voice.Position, count);
            for (var i = 0; i < count; i++)
            {
                buffer[i] += source[i];
            }

            voice.Position += count;
        }

        _voices.RemoveAll(voice => voice.Position >= voice.Samples.Length);
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
        }

        return true;
    }

    internal static short[] Load(SoundCue cue)
    {
        var name = cue.ToString().ToLowerInvariant();
        using var stream = typeof(InteractionSounds).Assembly.GetManifestResourceStream($"NeonMon.Assets.Sounds.{name}.wav")
            ?? throw new InvalidDataException($"Missing sound {name}.");
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF" || reader.ReadInt32() < 0 || new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException($"{name} is not a WAV file.");
        }

        var formatValid = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                var tag = reader.ReadInt16();
                var channels = reader.ReadInt16();
                var rate = reader.ReadInt32();
                reader.ReadBytes(6);
                var bits = reader.ReadInt16();
                reader.ReadBytes(size - 16);
                formatValid = tag == 1 && channels == 1 && rate == SampleRate && bits == 16;
            }
            else if (id == "data" && formatValid)
            {
                var bytes = reader.ReadBytes(size);
                var samples = new short[bytes.Length / sizeof(short)];
                Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(short));
                return samples;
            }
            else
            {
                reader.ReadBytes(size + (size & 1));
            }
        }

        throw new InvalidDataException($"{name} must be 48 kHz 16-bit mono PCM.");
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            _disposed = true;
            thread = _thread;
        }

        thread?.Join(500);
    }

    private sealed class Voice(float[] samples)
    {
        public float[] Samples { get; } = samples;
        public int Position { get; set; }
    }

    // A shared-mode, event-driven WASAPI stream on the default output device. It follows the session volume like any app.
    private sealed class RenderStream : IDisposable
    {
        private const int StreamFlagsEventCallback = 0x00040000;
        private const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
        private const int StreamFlagsSrcDefaultQuality = 0x08000000;
        private const int BufferFlagsSilent = 2;
        private const long BufferDuration = 0;

        private readonly IAudioClient _client;
        private readonly IAudioRenderClient _render;
        private readonly EventWaitHandle _ready = new(false, EventResetMode.AutoReset);
        private readonly uint _bufferFrames;
        private readonly uint _queueFrames;
        private readonly int _channels;
        private float[] _mono = [];
        private float[] _interleaved = [];

        public RenderStream(int sampleRate)
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            try
            {
                Check(enumerator.GetDefaultAudioEndpoint(0, 0, out var device));
                var clientId = typeof(IAudioClient).GUID;
                Check(device.Activate(ref clientId, 1, 0, out var client));
                Marshal.ReleaseComObject(device);
                _client = (IAudioClient)client;
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }

            // Rendering in the device's own mix format skips Windows' format converter, which added about 50 ms.
            Check(_client.GetMixFormat(out var mixFormat));
            try
            {
                var native = IsFloat(mixFormat) && Marshal.ReadInt32(mixFormat, 4) == sampleRate;
                if (native)
                {
                    _channels = Marshal.ReadInt16(mixFormat, 2);
                    Check(_client.Initialize(0, StreamFlagsEventCallback, BufferDuration, 0, mixFormat, 0));
                }
                else
                {
                    _channels = 1;
                    var format = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormat>());
                    try
                    {
                        Marshal.StructureToPtr(new WaveFormat
                        {
                            FormatTag = 3,
                            Channels = 1,
                            SamplesPerSecond = sampleRate,
                            AverageBytesPerSecond = sampleRate * sizeof(float),
                            BlockAlign = sizeof(float),
                            BitsPerSample = 32
                        }, format, false);
                        Check(_client.Initialize(0, StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality,
                            BufferDuration, 0, format, 0));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(format);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(mixFormat);
            }

            Check(_client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle()));
            Check(_client.GetBufferSize(out _bufferFrames));
            // A new sound waits behind whatever silence is queued, so keep one engine period plus 2 ms, not the whole buffer.
            Check(_client.GetDevicePeriod(out var period, out _));
            _queueFrames = (uint)Math.Min(_bufferFrames, (period + 20_000) * sampleRate / 10_000_000);
            var renderId = typeof(IAudioRenderClient).GUID;
            Check(_client.GetService(ref renderId, out var render));
            _render = (IAudioRenderClient)render;
            Fill(_ => false);
            Check(_client.Start());
        }

        public void Wait() => _ready.WaitOne(200);

        // Lets a new sound go into the free part of the buffer now instead of at the next engine period.
        public void Wake()
        {
            try
            {
                _ready.Set();
            }
            catch (ObjectDisposedException)
            {
                // The stream failed and is shutting down; the sound plays on the next stream.
            }
        }

        public void Fill(Func<Span<float>, bool> mix)
        {
            Check(_client.GetCurrentPadding(out var padding));
            var frames = (int)_queueFrames - (int)padding;
            if (frames <= 0)
            {
                return;
            }

            Check(_render.GetBuffer(frames, out var data));
            if (_mono.Length < frames)
            {
                _mono = new float[frames];
                _interleaved = new float[frames * _channels];
            }

            var audible = mix(_mono.AsSpan(0, frames));
            if (audible)
            {
                for (var i = 0; i < frames; i++)
                {
                    for (var channel = 0; channel < _channels; channel++)
                    {
                        _interleaved[i * _channels + channel] = _mono[i];
                    }
                }

                Marshal.Copy(_interleaved, 0, data, frames * _channels);
            }

            Check(_render.ReleaseBuffer(frames, audible ? 0 : BufferFlagsSilent));
        }

        // IEEE float, either plainly or as WAVE_FORMAT_EXTENSIBLE with the float sub-format.
        private static bool IsFloat(nint format)
        {
            var tag = (ushort)Marshal.ReadInt16(format, 0);
            var bits = Marshal.ReadInt16(format, 14);
            if (bits != 32)
            {
                return false;
            }

            if (tag == 3)
            {
                return true;
            }

            if (tag != 0xFFFE)
            {
                return false;
            }

            var subFormat = new byte[16];
            Marshal.Copy(format + 24, subFormat, 0, 16);
            return new Guid(subFormat) == new Guid("00000003-0000-0010-8000-00AA00389B71");
        }

        public void Dispose()
        {
            _client.Stop();
            Marshal.ReleaseComObject(_render);
            Marshal.ReleaseComObject(_client);
            _ready.Dispose();
        }

        private static void Check(int result)
        {
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints();

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, nint format, nint session);

        [PreserveSig]
        int GetBufferSize(out uint frames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, nint format, out nint closest);

        [PreserveSig]
        int GetMixFormat(out nint format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(nint handle);

        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig]
        int GetBuffer(int frames, out nint data);

        [PreserveSig]
        int ReleaseBuffer(int frames, int flags);
    }
}

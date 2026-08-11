using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace CortexFX.Core.Audio;

/// <summary>
/// Sits in the playback chain and fills live FFT band levels for the spectrum UI.
/// </summary>
public sealed class LiveSpectrumAnalyzer : IWaveProvider
{
    private readonly IWaveProvider _source;
    private readonly int _fftLength;
    private readonly int _m;
    private readonly Complex[] _fftBuffer;
    private readonly float[] _window;
    private readonly float[] _bandLevels;
    private readonly object _sync = new();
    private int _fftPos;
    private readonly int _bytesPerSample;
    private readonly int _channels;

    public LiveSpectrumAnalyzer(IWaveProvider source, int bandCount = 48, int fftLength = 1024)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (bandCount < 8) bandCount = 8;
        if ((fftLength & (fftLength - 1)) != 0)
            throw new ArgumentException("FFT length must be a power of 2.", nameof(fftLength));

        _fftLength = fftLength;
        _m = (int)Math.Log(fftLength, 2);
        _fftBuffer = new Complex[fftLength];
        _window = new float[fftLength];
        _bandLevels = new float[bandCount];
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _bytesPerSample = source.WaveFormat.BitsPerSample / 8;

        for (int i = 0; i < fftLength; i++)
        {
            // Hann window
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (fftLength - 1)));
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Latest band magnitudes in 0..1 range (thread-safe copy).</summary>
    public void CopyBandLevels(float[] destination)
    {
        lock (_sync)
        {
            int n = Math.Min(destination.Length, _bandLevels.Length);
            Array.Copy(_bandLevels, destination, n);
        }
    }

    public int BandCount => _bandLevels.Length;

    public void ResetLevels()
    {
        lock (_sync)
        {
            Array.Clear(_bandLevels, 0, _bandLevels.Length);
            _fftPos = 0;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read > 0)
        {
            ProcessBytes(buffer, offset, read);
        }

        return read;
    }

    private void ProcessBytes(byte[] buffer, int offset, int count)
    {
        // AudioFileReader normally feeds IEEE float; also accept raw 32-bit float buffers.
        if (_bytesPerSample == 4)
        {
            int samples = count / 4;
            for (int i = 0; i < samples; i += _channels)
            {
                float sample = BitConverter.ToSingle(buffer, offset + i * 4);
                if (_channels > 1 && i + 1 < samples)
                {
                    float right = BitConverter.ToSingle(buffer, offset + (i + 1) * 4);
                    sample = 0.5f * (sample + right);
                }

                PushSample(sample);
            }
        }
        else if (_bytesPerSample == 2)
        {
            int samples = count / 2;
            for (int i = 0; i < samples; i += _channels)
            {
                short sample = BitConverter.ToInt16(buffer, offset + i * 2);
                float mono = sample / 32768f;
                if (_channels > 1 && i + 1 < samples)
                {
                    short right = BitConverter.ToInt16(buffer, offset + (i + 1) * 2);
                    mono = 0.5f * (mono + right / 32768f);
                }

                PushSample(mono);
            }
        }
    }

    private void PushSample(float sample)
    {
        _fftBuffer[_fftPos] = new Complex
        {
            X = sample * _window[_fftPos],
            Y = 0
        };
        _fftPos++;

        if (_fftPos < _fftLength)
        {
            return;
        }

        _fftPos = 0;
        FastFourierTransform.FFT(true, _m, _fftBuffer);

        int usable = _fftLength / 2;
        int bands = _bandLevels.Length;
        float[] next = new float[bands];

        for (int band = 0; band < bands; band++)
        {
            // Logarithmic band mapping (more resolution in lows).
            double low = Math.Pow(usable, (double)band / bands);
            double high = Math.Pow(usable, (double)(band + 1) / bands);
            int start = Math.Max(1, (int)low);
            int end = Math.Min(usable - 1, Math.Max(start + 1, (int)high));

            double peak = 0;
            for (int bin = start; bin < end; bin++)
            {
                double mag = Math.Sqrt(
                    _fftBuffer[bin].X * _fftBuffer[bin].X +
                    _fftBuffer[bin].Y * _fftBuffer[bin].Y);
                if (mag > peak) peak = mag;
            }

            // Soft log scale for visual punch without clipping.
            float level = (float)Math.Min(1.0, Math.Log10(1 + peak * 12) / 1.5);
            next[band] = level;
        }

        lock (_sync)
        {
            // Light temporal smoothing so bars feel fluid, not jittery.
            for (int i = 0; i < bands; i++)
            {
                _bandLevels[i] = _bandLevels[i] * 0.55f + next[i] * 0.45f;
            }
        }
    }
}

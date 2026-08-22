using System; using System.Numerics; namespace ShanyuEngine.Audio;

public static class AudioDSP
{
    public static float[] GenerateSineWave(float freq, float sampleRate, float duration, float amp = 0.5f)
    {
        int samples = (int)(sampleRate * duration); var buf = new float[samples];
        for (int i = 0; i < samples; i++) buf[i] = amp * MathF.Sin(2 * MathF.PI * freq * i / sampleRate);
        return buf;
    }

    public static float[] GenerateSquareWave(float freq, float sampleRate, float duration, float amp = 0.5f)
    {
        int samples = (int)(sampleRate * duration); var buf = new float[samples];
        for (int i = 0; i < samples; i++) { float v = MathF.Sin(2 * MathF.PI * freq * i / sampleRate); buf[i] = amp * (v >= 0 ? 1f : -1f); }
        return buf;
    }

    public static float[] GenerateSawWave(float freq, float sampleRate, float duration, float amp = 0.5f)
    {
        int samples = (int)(sampleRate * duration); var buf = new float[samples];
        for (int i = 0; i < samples; i++) buf[i] = amp * (2f * ((freq * i / sampleRate) % 1f) - 1f);
        return buf;
    }

    public static float[] GenerateNoise(float sampleRate, float duration, float amp = 0.5f)
    {
        var rng = new Random(42); int samples = (int)(sampleRate * duration); var buf = new float[samples];
        for (int i = 0; i < samples; i++) buf[i] = amp * (float)(rng.NextDouble() * 2f - 1f);
        return buf;
    }

    public static float[] ApplyADSR(float[] buf, float attack, float decay, float sustain, float release, float sampleRate)
    {
        int total = buf.Length; int aEnd = (int)(attack * sampleRate); int dEnd = aEnd + (int)(decay * sampleRate);
        int rStart = total - (int)(release * sampleRate); var result = new float[total];
        for (int i = 0; i < total; i++)
        {
            float env;
            if (i < aEnd) env = (float)i / aEnd;
            else if (i < dEnd) { float t = (float)(i - aEnd) / (dEnd - aEnd); env = 1f - (1f - sustain) * t; }
            else if (i < rStart) env = sustain;
            else { float t = (float)(i - rStart) / (total - rStart); env = sustain * (1f - t); }
            result[i] = buf[i] * env;
        }
        return result;
    }

    public static float[] ApplyLowPass(float[] buf, float cutoff, float sampleRate)
    {
        float rc = 1f / (2f * MathF.PI * cutoff); float dt = 1f / sampleRate; float alpha = dt / (rc + dt);
        var result = new float[buf.Length]; result[0] = buf[0];
        for (int i = 1; i < buf.Length; i++) result[i] = result[i-1] + alpha * (buf[i] - result[i-1]);
        return result;
    }

    public static float[] ApplyHighPass(float[] buf, float cutoff, float sampleRate)
    {
        float rc = 1f / (2f * MathF.PI * cutoff); float dt = 1f / sampleRate; float alpha = rc / (rc + dt);
        var result = new float[buf.Length]; result[0] = buf[0];
        for (int i = 1; i < buf.Length; i++) result[i] = alpha * (result[i-1] + buf[i] - buf[i-1]);
        return result;
    }

    public static float[] ApplyReverb(float[] buf, float decay, float delay, float sampleRate)
    {
        int delaySamples = (int)(delay * sampleRate); var result = new float[buf.Length];
        for (int i = 0; i < buf.Length; i++)
        {
            result[i] = buf[i];
            if (i >= delaySamples) result[i] += result[i - delaySamples] * decay;
        }
        return result;
    }

    public static float[] ApplyFlanger(float[] buf, float rate, float depth, float sampleRate)
    {
        var result = new float[buf.Length];
        for (int i = 0; i < buf.Length; i++)
        {
            float delaySamples = depth * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * rate * i / sampleRate));
            int d = (int)delaySamples; float frac = delaySamples - d;
            if (i >= d + 1) result[i] = buf[i] + (1f - frac) * buf[i - d] + frac * buf[i - d - 1];
            else result[i] = buf[i];
        }
        return result;
    }

    public static float[] ApplyChorus(float[] buf, float rate, float depth, float mix, float sampleRate)
    {
        var result = new float[buf.Length];
        for (int i = 0; i < buf.Length; i++)
        {
            float d = depth * (0.5f + 0.5f * MathF.Sin(2f * MathF.PI * rate * i / sampleRate));
            int delay = (int)d; float frac = d - delay;
            float wet = 0;
            if (i >= delay + 1) wet = (1f - frac) * buf[i - delay] + frac * buf[i - delay - 1];
            result[i] = (1f - mix) * buf[i] + mix * wet;
        }
        return result;
    }

    public static float[] ApplyDelay(float[] buf, float delayTime, float feedback, float sampleRate)
    {
        int delay = (int)(delayTime * sampleRate); var result = new float[buf.Length];
        for (int i = 0; i < buf.Length; i++)
        {
            result[i] = buf[i];
            if (i >= delay) result[i] += result[i - delay] * feedback;
        }
        return result;
    }

    public static float[] ApplyCompressor(float[] buf, float threshold, float ratio, float attack, float release, float sampleRate)
    {
        float gain = 1f; float aCoeff = MathF.Exp(-1f / (attack * sampleRate));
        float rCoeff = MathF.Exp(-1f / (release * sampleRate)); var result = new float[buf.Length];
        for (int i = 0; i < buf.Length; i++)
        {
            float abs = MathF.Abs(buf[i]);
            if (abs > threshold) gain = aCoeff * gain + (1f - aCoeff) * (threshold + (abs - threshold) / ratio) / (abs + 1e-10f);
            else gain = rCoeff * gain + (1f - rCoeff) * 1f;
            result[i] = buf[i] * gain;
        }
        return result;
    }

    public static float[] ApplyDistortion(float[] buf, float drive)
    {
        var result = new float[buf.Length];
        for (int i = 0; i < buf.Length; i++)
        {
            float s = buf[i] * drive; float clamped = Math.Clamp(s, -1f, 1f);
            result[i] = (clamped * (2f + MathF.Abs(clamped))) / (2f + clamped * clamped);
        }
        return result;
    }

    public static float[] Normalize(float[] buf)
    {
        float max = 0; for (int i = 0; i < buf.Length; i++) max = MathF.Max(max, MathF.Abs(buf[i]));
        if (max < 0.0001f) return buf; float scale = 1f / max;
        for (int i = 0; i < buf.Length; i++) buf[i] *= scale; return buf;
    }

    public static float[] Mix(float[] a, float[] b, float gainA = 1f, float gainB = 1f)
    {
        int len = Math.Min(a.Length, b.Length); var result = new float[len];
        for (int i = 0; i < len; i++) result[i] = a[i] * gainA + b[i] * gainB;
        return result;
    }

    public static float[] Resample(float[] buf, int originalRate, int targetRate)
    {
        if (originalRate == targetRate) return buf;
        float ratio = (float)targetRate / originalRate; int newLen = (int)(buf.Length * ratio);
        var result = new float[newLen];
        for (int i = 0; i < newLen; i++)
        {
            float srcIdx = i / ratio; int idx = (int)srcIdx; float frac = srcIdx - idx;
            if (idx + 1 < buf.Length) result[i] = buf[idx] * (1f - frac) + buf[idx + 1] * frac;
            else result[i] = buf[Math.Min(idx, buf.Length - 1)];
        }
        return result;
    }

    public static float RMS(float[] buf)
    {
        float sum = 0; for (int i = 0; i < buf.Length; i++) sum += buf[i] * buf[i];
        return MathF.Sqrt(sum / buf.Length);
    }

    public static float Peak(float[] buf) { float m = 0; for (int i = 0; i < buf.Length; i++) m = MathF.Max(m, MathF.Abs(buf[i])); return m; }

    public static float[] FadeIn(float[] buf, float duration, float sampleRate)
    {
        int samples = (int)(duration * sampleRate); var r = (float[])buf.Clone();
        for (int i = 0; i < Math.Min(samples, r.Length); i++) r[i] *= (float)i / samples;
        return r;
    }

    public static float[] FadeOut(float[] buf, float duration, float sampleRate)
    {
        int samples = (int)(duration * sampleRate); int start = buf.Length - samples; var r = (float[])buf.Clone();
        for (int i = Math.Max(0, start); i < r.Length; i++) r[i] *= (float)(r.Length - 1 - i) / (r.Length - 1 - start);
        return r;
    }

    public static float[] Convolve(float[] a, float[] b)
    {
        int n = a.Length + b.Length - 1; var result = new float[n];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                result[i + j] += a[i] * b[j];
        return result;
    }

    public static float[] GenerateTone(float freq, float sampleRate, float duration, params float[] harmonics)
    {
        int samples = (int)(sampleRate * duration); var buf = new float[samples];
        for (int i = 0; i < samples; i++)
            for (int h = 0; h < harmonics.Length; h++)
                buf[i] += harmonics[h] * MathF.Sin(2 * MathF.PI * freq * (h + 1) * i / sampleRate);
        return Normalize(buf);
    }
}

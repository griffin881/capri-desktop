using System;
using System.Collections.Generic;

namespace CapriDesktop
{
    // Her living motion while she floats free on the screen (not sitting, walking, riding or being dragged).
    // A hover that keeps retuning itself, a lean toward the cursor, and small moves picked at random:
    // each move gets its own length, direction and strength, and the last few moves can't repeat,
    // so she never runs the same loop twice.
    internal sealed class Life
    {
        internal float X, Y, Tilt, ScaleX = 1, ScaleY = 1, Split, Fade = 1;

        private const int Stretch = 0, Shiver = 1, Flicker = 2, Bounce = 3, Wobble = 4, Drift = 5, LookAround = 6, Skip = 7, Settle = 8, Count = 9;
        private static readonly double[] Weight = { 1.0, 1.1, 0.9, 1.0, 0.9, 1.2, 1.0, 0.7, 0.8 };
        private static readonly double[] ShortestLength = { 0.8, 0.35, 0.55, 0.75, 1.0, 2.2, 1.6, 0.3, 1.2 };
        private static readonly double[] LongestLength = { 1.4, 0.6, 0.9, 1.1, 1.6, 3.6, 2.6, 0.45, 2.0 };
        private static readonly int[] Landings = { Bounce, Shiver, Stretch, Wobble, Settle, Skip };

        private readonly Random random;
        private readonly List<int> recent = new List<int>();
        private int move = -1;
        private double moveStart, moveLength, nextMove = -1, lastNow = -1, retuneAt;
        private double phaseA, phaseB, speedA = 1.6, speedB = 0.5;
        private float strength = 1, direction = 1, lean, jitterX;
        private bool wasFree;

        internal Life(Random random) { this.random = random; }

        internal bool Moving { get { return move >= 0; } }

        // She was just put down somewhere with nothing under her: react, differently each time.
        internal void Landed(double now)
        {
            List<int> options = new List<int>();
            foreach (int landing in Landings) if (!recent.Contains(landing)) options.Add(landing);
            if (options.Count == 0) options.AddRange(Landings);
            Start(options[random.Next(options.Count)], now);
        }

        // Called every frame. dx and dy run from her middle to the cursor, in pixels.
        internal void Update(double now, bool free, bool talking, float size, float dx, float dy)
        {
            double step = lastNow < 0 ? 0 : Math.Max(0, Math.Min(0.1, now - lastNow));
            lastNow = now;
            if (!free)
            {
                Relax(step);
                wasFree = false;
                move = -1;
                return;
            }
            if (!wasFree)
            {
                wasFree = true;
                nextMove = now + 1.2 + random.NextDouble() * 2.5;
            }
            if (now >= retuneAt)
            {
                speedA = 1.1 + random.NextDouble() * 1.0;
                speedB = 0.3 + random.NextDouble() * 0.45;
                retuneAt = now + 5 + random.NextDouble() * 9;
            }
            phaseA += step * speedA;
            phaseB += step * speedB;
            float amp = size * (talking ? 0.007f : 0.015f);
            float hoverY = (float)(Math.Sin(phaseA) * amp + Math.Sin(phaseB * 1.7 + 0.4) * amp * 0.6);
            float hoverX = (float)(Math.Sin(phaseB + 1.3) * amp * 0.45);
            double reach = Math.Sqrt(dx * dx + dy * dy);
            float want = reach < size * 2.2f ? Math.Max(-1f, Math.Min(1f, dx / size)) * 5f : 0f;
            lean += (want - lean) * (float)Math.Min(1, step * 3);

            float mx = 0, my = 0, mt = 0, sx = 1, sy = 1, sp = 0, fd = 1;
            if (move < 0 && now >= nextMove && !talking) Start(Pick(), now);
            if (move >= 0)
            {
                double t = (now - moveStart) / moveLength;
                if (t >= 1)
                {
                    move = -1;
                    nextMove = now + (talking ? 4 : 2.5) + random.NextDouble() * 6.5;
                }
                else Shape(t, size, ref mx, ref my, ref mt, ref sx, ref sy, ref sp, ref fd);
            }
            X = hoverX + mx;
            Y = hoverY + my;
            Tilt = lean + mt;
            ScaleX = sx;
            ScaleY = sy;
            Split = sp;
            Fade = fd;
        }

        private void Relax(double step)
        {
            float keep = (float)Math.Pow(0.02, step);
            X *= keep;
            Y *= keep;
            Tilt *= keep;
            lean *= keep;
            ScaleX = 1 + (ScaleX - 1) * keep;
            ScaleY = 1 + (ScaleY - 1) * keep;
            Split *= keep;
            Fade = 1 - (1 - Fade) * keep;
        }

        private int Pick()
        {
            double total = 0;
            for (int i = 0; i < Count; i++) if (!recent.Contains(i)) total += Weight[i];
            double roll = random.NextDouble() * total;
            for (int i = 0; i < Count; i++)
            {
                if (recent.Contains(i)) continue;
                roll -= Weight[i];
                if (roll <= 0) return i;
            }
            return Drift;
        }

        private void Start(int next, double now)
        {
            move = next;
            moveStart = now;
            moveLength = ShortestLength[next] + random.NextDouble() * (LongestLength[next] - ShortestLength[next]);
            strength = 0.7f + (float)random.NextDouble() * 0.6f;
            direction = random.NextDouble() < 0.5 ? -1 : 1;
            jitterX = 0;
            recent.Add(next);
            if (recent.Count > 3) recent.RemoveAt(0);
        }

        private static float Bump(double t, double at, double width)
        {
            double d = (t - at) / width;
            return (float)Math.Exp(-d * d);
        }

        private void Shape(double t, float size, ref float mx, ref float my, ref float mt, ref float sx, ref float sy, ref float sp, ref float fd)
        {
            float s = strength, dir = direction;
            switch (move)
            {
                case Stretch:
                {
                    float wave = (float)(Math.Sin(Math.PI * t) * (1 - 0.35 * t));
                    sy = 1 + 0.06f * s * wave;
                    sx = 1 / (float)Math.Sqrt(sy);
                    my = -size * 0.012f * s * wave;
                    break;
                }
                case Shiver:
                {
                    float fade = (float)(1 - t);
                    if (random.NextDouble() < 0.5) jitterX = (float)(random.NextDouble() * 2 - 1) * size * 0.02f * s;
                    mx = jitterX * fade;
                    sp = fade * s;
                    fd = 1 - 0.15f * fade;
                    break;
                }
                case Flicker:
                {
                    float dip = Math.Max(Bump(t, 0.18, 0.07), Bump(t, 0.5, 0.06) * 0.8f);
                    fd = 1 - 0.5f * s * dip;
                    sp = 0.4f * dip;
                    break;
                }
                case Bounce:
                {
                    if (t < 0.65)
                    {
                        double u = t / 0.65;
                        my = -size * 0.07f * s * (float)(4 * u * (1 - u));
                        sy = 1 + 0.03f * (float)Math.Sin(Math.PI * u);
                        sx = 1 / sy;
                    }
                    else
                    {
                        float land = (float)Math.Sin((t - 0.65) / 0.35 * Math.PI);
                        sy = 1 - 0.07f * s * land;
                        sx = 1 + 0.05f * s * land;
                        my = size * 0.012f * land;
                    }
                    break;
                }
                case Wobble:
                    mt = dir * 9 * s * (float)(Math.Exp(-4 * t) * Math.Sin(t * 19));
                    break;
                case Drift:
                {
                    float wave = (float)Math.Sin(Math.PI * t);
                    mx = dir * size * 0.055f * s * wave;
                    my = -size * 0.01f * wave;
                    mt = dir * 2.5f * s * (float)Math.Sin(2 * Math.PI * t);
                    break;
                }
                case LookAround:
                {
                    double half = t < 0.5 ? t / 0.5 : (t - 0.5) / 0.5;
                    float side = t < 0.5 ? dir : -dir;
                    mt = side * 4.5f * s * (float)Math.Sin(Math.PI * half);
                    my = -size * 0.008f * (float)Math.Sin(Math.PI * t);
                    break;
                }
                case Skip:
                {
                    // A glitch skip: she blinks a few pixels sideways and snaps back.
                    float on = t > 0.12 && t < 0.62 ? 1 : 0;
                    mx = dir * size * 0.035f * s * on;
                    sp = Math.Max(Bump(t, 0.12, 0.08), Bump(t, 0.62, 0.08)) * s;
                    fd = 1 - 0.25f * on;
                    break;
                }
                case Settle:
                {
                    float sink = (float)Math.Sin(Math.PI * t);
                    my = size * 0.02f * s * sink;
                    sy = 1 - 0.035f * s * sink;
                    sx = 1 + 0.025f * s * sink;
                    mt = dir * 1.5f * sink;
                    break;
                }
            }
        }
    }
}

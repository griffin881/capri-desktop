using System;
using System.Collections.Generic;

namespace CapriDesktop
{
    // Talking over her on a call. While she speaks, her own voice off the speakers can reach the mic, so the mic is held
    // back from the call and she can't cut herself off. When they're clearly louder than her echo for a third of a second,
    // that's them talking over her: she stops at once and the call hears everything they said, from the first word.
    internal sealed class Barge
    {
        // Quietest a voice over hers can be, how many tenth-of-a-second chunks it takes, and how many chunks of her
        // speaking she listens to first, to learn how loud her own echo is before she lets anything cut her off.
        internal const double Level = 0.05;
        internal const int Chunks = 3;
        internal const int Settle = 10;

        private double floor;
        private int heard;
        private int run;
        private readonly List<short[]> held = new List<short[]>();

        // They've cut in: their mic goes straight to the call until she's quiet.
        internal bool In;

        internal void Reset() { floor = 0; heard = 0; run = 0; held.Clear(); In = false; }

        internal static double Loudness(short[] samples)
        {
            if (samples == null || samples.Length == 0) return 0;
            double sum = 0;
            for (int i = 0; i < samples.Length; i++) sum += (double)samples[i] * samples[i];
            return Math.Sqrt(sum / samples.Length) / 32768.0;
        }

        // What to send the call for one chunk of mic while she's talking: silence, or, once they cut in, all of it.
        internal List<short[]> While(short[] chunk, out bool cut)
        {
            cut = false;
            List<short[]> send = new List<short[]>();
            if (In) { send.Add(chunk); return send; }
            double level = Loudness(chunk);
            bool over = heard >= Settle && level >= Math.Max(Level, floor * 3);
            // Her echo sets the floor: it follows the mic down quickly and up slowly, so a steady echo is learned
            // and a voice on top of it stands out.
            if (!over) floor = heard == 0 ? level : level < floor ? floor * 0.5 + level * 0.5 : floor * 0.9 + level * 0.1;
            heard++;
            held.Add(chunk);
            if (held.Count > Chunks + 1) held.RemoveAt(0);
            run = over ? run + 1 : 0;
            if (run < Chunks) { send.Add(new short[chunk.Length]); return send; }
            In = true;
            run = 0;
            cut = true;
            send.AddRange(held);
            held.Clear();
            return send;
        }

        // She's quiet again: their mic is theirs, and the next time she talks it's held back again.
        internal void Quiet() { In = false; run = 0; held.Clear(); }
    }
}

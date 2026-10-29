using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Speech.Recognition;

namespace CapriDesktop
{
    // "Hey Capri". Her own Parakeet ears listen on the mic picked under VOICE: short bursts of speech get
    // transcribed and checked for her name. Without Parakeet installed, Windows' offline recognizer listens
    // on the default mic instead. Nothing it hears is kept; only start-up, trouble and wake-ups are logged.
    internal sealed class WakeWord : IDisposable
    {
        internal event Action Heard;
        internal event Action<string> Failed;
        internal event Action<string> Started;
        // True while she's talking, chatting or otherwise shouldn't wake.
        internal Func<bool> Busy;
        internal float Threshold = 0.45f;

        // Her name takes about a second to say, usually after a pause. Only bursts like that get a listen from her ears;
        // anything continuous (talk, an audiobook, music) is skipped, checks are rate-limited, and they run at low priority.
        private const double Loud = 0.02;
        private const int StartRun = 2, EndQuiet = 4, MinChunks = 3, MaxChunks = 20, Preroll = 3, PerMinute = 10;
        private const double PauseBefore = 0.5, Spacing = 1.2, Rest = 45;
        private static readonly string[] Phrases = { "hey capri", "hi capri", "okay capri", "ok capri", "yo capri", "capri wake up" };
        private static readonly Regex Name = new Regex(@"\b(c|k)a+h?p+(r|l)(i|ee|ie|y|e|ey)\b|\bcapri\w*", RegexOptions.IgnoreCase);
        private static readonly Regex Greeting = new Regex(@"^\W*(hey|hi|hay|okay|ok|yo|a|ay|eh)\b", RegexOptions.IgnoreCase);

        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly LocalEars parakeet;
        private readonly string mic;
        private Ears ears;
        private SpeechRecognitionEngine engine;
        private Timer silenceCheck;
        private readonly List<short[]> preroll = new List<short[]>();
        private List<short[]> utterance, pending;
        private int loudRun, quietRun;
        private volatile bool hearing;
        private double lastHeard = -10, loudest, floor = 0.006, lastLoud = -10, burstGap = 10, lastCheck = -10, restUntil = -1;
        private readonly Queue<double> checks = new Queue<double>();
        private bool disposed, skipping;
        private Thread worker;
        private AutoResetEvent signal;

        internal WakeWord(LocalEars parakeet, string mic)
        {
            this.parakeet = parakeet;
            this.mic = mic;
        }

        internal void Start()
        {
            lock (gate)
            {
                if (disposed || ears != null || engine != null) return;
                try
                {
                    if (parakeet != null && parakeet.Ready) StartOwn();
                    else StartWindows();
                    silenceCheck = new Timer(CheckSilence, null, 20000, Timeout.Infinite);
                }
                catch (Exception error)
                {
                    Shut();
                    Fail("\"Hey Capri\" couldn't start: " + error.Message);
                }
            }
        }

        private void StartOwn()
        {
            // Her ears load the first time something sounds like her name, not before.
            signal = new AutoResetEvent(false);
            worker = new Thread(Listen);
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Name = "Capri wake word";
            worker.Start();
            Ears opened = new Ears();
            opened.Chosen = mic;
            opened.Heard += Gather;
            opened.Start();
            ears = opened;
            Log("listening with her own ears on " + (string.IsNullOrEmpty(mic) ? "the default mic" : "the mic picked under VOICE"));
            Announce("Listening for \"Hey Capri\".");
        }

        private void Listen()
        {
            while (true)
            {
                AutoResetEvent wake = signal;
                if (disposed || wake == null) return;
                wake.WaitOne(1000);
                List<short[]> taken;
                lock (preroll) { taken = pending; pending = null; }
                if (taken != null) Check(taken);
            }
        }

        private void StartWindows()
        {
            List<RecognizerInfo> installed = new List<RecognizerInfo>(SpeechRecognitionEngine.InstalledRecognizers());
            if (installed.Count == 0) throw new InvalidOperationException(Hearing.NoRecognizer);
            List<string> cultures = installed.ConvertAll(delegate(RecognizerInfo info) { return info.Culture.Name; });
            string chosen = Hearing.Pick(cultures, "en-US") ?? cultures[0];
            SpeechRecognitionEngine made = new SpeechRecognitionEngine(installed[cultures.IndexOf(chosen)]);
            GrammarBuilder name = new GrammarBuilder(new Choices(Phrases));
            name.Culture = made.RecognizerInfo.Culture;
            Grammar wake = new Grammar(name);
            wake.Name = "wake";
            made.LoadGrammar(wake);
            made.SpeechRecognized += Recognized;
            made.AudioLevelUpdated += delegate(object sender, AudioLevelUpdatedEventArgs e) { if (e.AudioLevel > 0) loudest = Math.Max(loudest, 1); };
            made.SetInputToDefaultAudioDevice();
            made.RecognizeAsync(RecognizeMode.Multiple);
            engine = made;
            Log("listening with Windows speech (" + chosen + ") on the default mic; her own ears aren't installed");
            Announce("Listening for \"Hey Capri\" (Windows speech, default mic).");
        }

        // Her own ears: catch a short burst that came after a pause, then hear it.
        private void Gather(short[] chunk)
        {
            double level = Speaker.Loudness(chunk, 0, chunk.Length);
            if (level > loudest) loudest = level;
            double now = clock.Elapsed.TotalSeconds;
            bool loud = level >= Math.Max(Loud, floor * 3);
            // A fan or hum raises the bar for what counts as someone talking.
            if (!loud && utterance == null) floor = floor * 0.97 + Math.Min(level, 0.05) * 0.03;
            if (loud) { if (loudRun == 0 && utterance == null) burstGap = now - lastLoud; lastLoud = now; }
            Func<bool> busy = Busy;
            lock (preroll)
            {
                if (hearing || pending != null || (busy != null && busy())) { utterance = null; preroll.Clear(); loudRun = quietRun = 0; skipping = false; return; }
                if (skipping)
                {
                    quietRun = loud ? 0 : quietRun + 1;
                    if (quietRun >= EndQuiet + 1) { skipping = false; quietRun = 0; }
                    return;
                }
                if (utterance == null)
                {
                    preroll.Add(chunk);
                    if (preroll.Count > Preroll) preroll.RemoveAt(0);
                    loudRun = loud ? loudRun + 1 : 0;
                    if (loudRun < StartRun) return;
                    loudRun = quietRun = 0;
                    // Talk that picks up without a pause is the middle of a conversation, not someone calling her.
                    if (burstGap < PauseBefore) { skipping = true; preroll.Clear(); return; }
                    utterance = new List<short[]>(preroll);
                    preroll.Clear();
                    return;
                }
                utterance.Add(chunk);
                quietRun = loud ? 0 : quietRun + 1;
                if (utterance.Count >= MaxChunks + EndQuiet) { utterance = null; skipping = true; quietRun = 0; return; }
                if (quietRun < EndQuiet) return;
                List<short[]> taken = utterance;
                utterance = null;
                quietRun = 0;
                if (taken.Count - EndQuiet < MinChunks) return;
                if (now < restUntil || now - lastCheck < Spacing) return;
                while (checks.Count > 0 && now - checks.Peek() > 60) checks.Dequeue();
                if (checks.Count >= PerMinute) { restUntil = now + Rest; Log("lots of talking nearby, so \"Hey Capri\" rests for " + (int)Rest + " seconds"); return; }
                checks.Enqueue(now);
                lastCheck = now;
                hearing = true;
                pending = taken;
                AutoResetEvent wake = signal;
                if (wake != null) wake.Set();
            }
        }

        private void Check(List<short[]> taken)
        {
            try
            {
                int total = 0;
                foreach (short[] piece in taken) total += piece.Length;
                short[] samples = new short[total];
                int at = 0;
                foreach (short[] piece in taken) { Array.Copy(piece, 0, samples, at, piece.Length); at += piece.Length; }
                string said = (parakeet.Hear(samples) ?? "").Trim();
                if (said.Length == 0) return;
                string[] words = said.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string opening = string.Join(" ", words, 0, Math.Min(4, words.Length));
                if (Name.IsMatch(opening)) { Wake("heard \"" + opening + "\""); return; }
                // A near miss, kept short so nothing else you say lands in the log.
                if (words.Length <= 4 && Greeting.IsMatch(said)) Log("near miss: \"" + said + "\"");
            }
            catch (Exception error) { Log("her ears glitched: " + error.Message); }
            finally { hearing = false; }
        }

        // Windows speech fallback.
        private void Recognized(object sender, SpeechRecognizedEventArgs e)
        {
            Func<bool> busy = Busy;
            if ((busy != null && busy()) || e.Result == null || e.Result.Grammar == null || e.Result.Grammar.Name != "wake") return;
            string said = (e.Result.Text ?? "").Trim().ToLower(CultureInfo.InvariantCulture);
            if (Array.IndexOf(Phrases, said) < 0) return;
            if (e.Result.Confidence < Threshold) { Log("near miss: \"" + said + "\" at " + e.Result.Confidence.ToString("0.00", CultureInfo.InvariantCulture)); return; }
            Wake("heard \"" + said + "\" at " + e.Result.Confidence.ToString("0.00", CultureInfo.InvariantCulture));
        }

        private void Wake(string why)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (now - lastHeard < 2.5) return;
            lastHeard = now;
            Log("woke: " + why);
            Action heard = Heard;
            if (heard != null) heard();
        }

        private void CheckSilence(object unused)
        {
            lock (gate) if (disposed || (ears == null && engine == null)) return;
            if (loudest < 0.002) Fail("I'm getting no sound from your mic. Check that the right mic is picked under VOICE, and that Settings > Privacy & security > Microphone lets desktop apps use it.");
        }

        private void Announce(string text)
        {
            Action<string> started = Started;
            if (started != null) started(text);
        }

        private void Fail(string text)
        {
            Log(text);
            Action<string> failed = Failed;
            if (failed != null) failed(text);
        }

        private static void Log(string text) { try { Brain.Log("wake word", -1, text); } catch (Exception) { } }

        private void Shut()
        {
            AutoResetEvent wake = signal;
            signal = null;
            if (wake != null) { try { wake.Set(); } catch (ObjectDisposedException) { } }
            worker = null;
            Timer timer = silenceCheck;
            silenceCheck = null;
            if (timer != null) timer.Dispose();
            Ears closingEars = ears;
            ears = null;
            if (closingEars != null) { try { closingEars.Heard -= Gather; closingEars.Stop(); } catch (Exception) { } }
            SpeechRecognitionEngine closing = engine;
            engine = null;
            if (closing == null) return;
            try { closing.SpeechRecognized -= Recognized; closing.RecognizeAsyncCancel(); } catch (InvalidOperationException) { }
            try { closing.Dispose(); } catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                Shut();
            }
        }
    }
}

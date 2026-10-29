using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Speech.Recognition;

namespace CapriDesktop
{
    // "Hey Capri": Windows' offline recognizer listening for her name and nothing else.
    // A wildcard branch soaks up other speech, so ordinary talk doesn't get forced into her name.
    internal sealed class WakeWord : IDisposable
    {
        internal event Action Heard;
        internal event Action<string> Failed;
        internal volatile bool Paused;
        internal float Threshold = 0.6f;

        private static readonly string[] Phrases = { "hey capri", "hi capri", "okay capri", "ok capri", "yo capri", "capri wake up" };
        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private SpeechRecognitionEngine engine;
        private double lastHeard = -10;
        private bool disposed;

        internal void Start()
        {
            lock (gate)
            {
                if (disposed || engine != null) return;
                try
                {
                    engine = Open();
                    engine.SpeechRecognized += Recognized;
                    engine.SetInputToDefaultAudioDevice();
                    engine.RecognizeAsync(RecognizeMode.Multiple);
                }
                catch (Exception error)
                {
                    Shut();
                    Action<string> failed = Failed;
                    if (failed != null) failed("\"Hey Capri\" couldn't start: " + error.Message);
                }
            }
        }

        private static SpeechRecognitionEngine Open()
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
            GrammarBuilder other = new GrammarBuilder();
            other.Culture = made.RecognizerInfo.Culture;
            other.AppendWildcard();
            Grammar noise = new Grammar(other);
            noise.Name = "noise";
            made.LoadGrammar(wake);
            made.LoadGrammar(noise);
            return made;
        }

        private void Recognized(object sender, SpeechRecognizedEventArgs e)
        {
            if (Paused || e.Result == null || e.Result.Grammar == null || e.Result.Grammar.Name != "wake") return;
            if (e.Result.Confidence < Threshold) return;
            string said = (e.Result.Text ?? "").Trim().ToLower(CultureInfo.InvariantCulture);
            if (Array.IndexOf(Phrases, said) < 0) return;
            double now = clock.Elapsed.TotalSeconds;
            if (now - lastHeard < 2.5) return;
            lastHeard = now;
            Action heard = Heard;
            if (heard != null) heard();
        }

        private void Shut()
        {
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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace CapriDesktop
{
    // Her longer memory, all on their PC. Every exchange goes in a day's journal; now and then, once they've gone quiet,
    // a background pass boils the new part down to a diary line (days.md) and picks out lasting facts for me.md.
    // She reads the last few diary lines before every reply, so yesterday isn't gone just because her chat window is short.
    internal static class Journal
    {
        internal static string Folder;
        private static readonly object gate = new object();
        private const string DaysHead = "# What Capri remembers of your days\n> One line per stretch of talking. She reads the last few every time you chat. Edit or delete anything here.\n\n";

        private static string JournalDir { get { return Folder == null ? null : Path.Combine(Folder, "journal"); } }
        internal static string DaysPath { get { return Folder == null ? null : Path.Combine(Folder, "days.md"); } }
        private static string MarkPath { get { return Folder == null ? null : Path.Combine(Folder, "journal", "digested.txt"); } }

        private static readonly HashSet<string> Common = new HashSet<string>(("about above after again also always another anything anyway around because been before being below between both cause could didn doesn doing done down during each even every everything from gonna gotta have having here into just know like lmao lol make many maybe more most much must need never nothing okay only other over pretty really right said same says should since some something still such sure take than that thats their them then there these they thing things think this those though thought through today told tomorrow very want wanna was well were what whats when where which while will with without would yeah your youre capri choom the and you are for but not was can get got she her his him how why who did had has its our out one all any now yes idk too way day see say man bro lot off own put try use let yet ago new old big bit few far who'd i'm i've ill ive dont cant wont isnt aint lmk rn tho imo fr ngl tbh omg wtf hey yep nah").Split(' '));

        // Older talk that matches what they just said, from every day she's kept, newest first. It's how she remembers
        // things from weeks ago that no longer fit in her chat window. Today's newest lines are skipped: those are in the chat itself.
        internal static string Recall(string said, int max, int skipToday = 40)
        {
            string dir = JournalDir;
            if (dir == null || max <= 0 || string.IsNullOrWhiteSpace(said)) return "";
            List<string> words = new List<string>();
            foreach (Match word in Regex.Matches(said.ToLowerInvariant().Replace('\u2019', '\''), @"[\p{L}\p{N}][\p{L}\p{N}'-]{2,}"))
            {
                string w = word.Value.Trim('\'', '-');
                if (w.EndsWith("'s", StringComparison.Ordinal)) w = w.Substring(0, w.Length - 2);
                if (w.Length >= 3 && !Common.Contains(w) && !words.Contains(w)) words.Add(w);
                if (words.Count >= 12) break;
            }
            if (words.Count == 0) return "";
            List<Regex> finds = new List<Regex>();
            foreach (string w in words) finds.Add(new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(w) + @"(?:s|es|d|ed|ing)?(?![\p{L}\p{N}])", RegexOptions.IgnoreCase));
            // A short ask needs one strong word to match; a longer one needs two, so a single common-ish word doesn't drag up everything.
            int need = words.Count <= 3 ? 1 : 2;
            string[] files;
            try { if (!Directory.Exists(dir)) return ""; files = Directory.GetFiles(dir, "????-??-??.md"); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
            Array.Sort(files, StringComparer.Ordinal);
            Array.Reverse(files);
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            List<KeyValuePair<int, string>> found = new List<KeyValuePair<int, string>>();
            int order = 0;
            for (int f = 0; f < files.Length && f < 400; f++)
            {
                string day = Path.GetFileNameWithoutExtension(files[f]);
                string[] lines;
                lock (gate)
                {
                    try { lines = File.ReadAllLines(files[f]); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                }
                int end = day == today ? Math.Max(0, lines.Length - skipToday) : lines.Length;
                for (int i = end - 1; i >= 0; i--)
                {
                    string line = lines[i];
                    if (line.Length < 8) continue;
                    int score = 0;
                    foreach (Regex find in finds) if (find.IsMatch(line)) score++;
                    if (score < need) continue;
                    string shown = day + " " + (line.Length > 300 ? line.Substring(0, 297) + "..." : line);
                    // Their line comes with her answer to it, so the memory reads as an exchange.
                    if (Regex.IsMatch(line, @"^\d\d:\d\d them: ") && i + 1 < end && Regex.IsMatch(lines[i + 1], @"^\d\d:\d\d Capri: "))
                        shown += "\n" + day + " " + (lines[i + 1].Length > 300 ? lines[i + 1].Substring(0, 297) + "..." : lines[i + 1]);
                    found.Add(new KeyValuePair<int, string>(score * 100000 - order++, shown));
                }
            }
            found.Sort(delegate(KeyValuePair<int, string> a, KeyValuePair<int, string> b) { return b.Key.CompareTo(a.Key); });
            List<string> picked = new List<string>();
            int used = 0;
            foreach (KeyValuePair<int, string> hit in found)
            {
                if (picked.Count >= max || used + hit.Value.Length > 2400) break;
                if (picked.Contains(hit.Value)) continue;
                picked.Add(hit.Value);
                used += hit.Value.Length;
            }
            picked.Sort(StringComparer.Ordinal);
            return string.Join("\n", picked.ToArray());
        }

        // The last few lines of today's talk, so a job or a new call knows what "that download" means.
        internal static string Recent(int count)
        {
            string dir = JournalDir;
            if (dir == null || count <= 0) return "";
            string path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
            string[] lines;
            lock (gate)
            {
                try { if (!File.Exists(path)) return ""; lines = File.ReadAllLines(path); }
                catch (IOException) { return ""; }
                catch (UnauthorizedAccessException) { return ""; }
            }
            List<string> kept = new List<string>();
            for (int i = lines.Length - 1; i >= 0 && kept.Count < count; i--)
                if (lines[i].Trim().Length > 0) kept.Insert(0, lines[i].Length > 400 ? lines[i].Substring(0, 397) + "..." : lines[i]);
            return string.Join("\n", kept.ToArray());
        }

        internal static void Add(string who, string text)
        {
            string dir = JournalDir;
            if (dir == null || string.IsNullOrWhiteSpace(text)) return;
            string line = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + " " + who + ": " + Regex.Replace(Hands.Hush(text), @"\s+", " ").Trim();
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md"), line + "\n");
                    Prune(dir);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // Journals older than 60 days go; the diary lines they became stay.
        private static void Prune(string dir)
        {
            foreach (string file in Directory.GetFiles(dir, "????-??-??.md"))
            {
                DateTime day;
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day) && (DateTime.Now - day).TotalDays > 3650)
                    try { File.Delete(file); } catch (IOException) { }
            }
        }

        // Lines not boiled down yet, oldest first, with their day.
        internal static List<string> Undigested(out string mark)
        {
            List<string> lines = new List<string>();
            mark = null;
            string dir = JournalDir;
            if (dir == null || !Directory.Exists(dir)) return lines;
            string done = "";
            try { if (File.Exists(MarkPath)) done = File.ReadAllText(MarkPath).Trim(); } catch (IOException) { }
            string[] files = Directory.GetFiles(dir, "????-??-??.md");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                string day = Path.GetFileNameWithoutExtension(file);
                string[] read;
                try { read = File.ReadAllLines(file); } catch (IOException) { continue; }
                for (int i = 0; i < read.Length; i++)
                {
                    string key = day + "#" + i.ToString("D5", CultureInfo.InvariantCulture);
                    if (string.CompareOrdinal(key, done) <= 0 || read[i].Trim().Length == 0) continue;
                    lines.Add(day + " " + read[i]);
                    mark = key;
                }
            }
            return lines;
        }

        internal static int Pending { get { string mark; return Undigested(out mark).Count; } }

        private const string DigestPrompt =
            "You keep Capri's diary. Capri is a desktop companion; the user is the person she lives with on their PC. Below is part of their conversation.\n" +
            "Reply with only JSON: {\"summary\": \"one or two plain sentences on what happened and what they talked about, past tense, specific (names of projects, games, plans), no fluff\", \"facts\": [\"up to three short lasting facts about the user worth remembering for months, third person, like what they're building, people in their life, preferences, plans; none for passing moods or one-off chatter\"]}.\n" +
            "Never include passwords, addresses, phone numbers, account numbers or anything that would find them in real life.";

        // Boils the new part of the journal down. Runs off her main thread with a brain of its own.
        internal static string Digest(Brain brain)
        {
            string mark;
            List<string> lines = Undigested(out mark);
            if (lines.Count < 2 || mark == null) return null;
            if (lines.Count > 160) lines = lines.GetRange(lines.Count - 160, 160);
            string talk = string.Join("\n", lines.ToArray());
            if (talk.Length > 9000) talk = talk.Substring(talk.Length - 9000);
            Summary result = Json.Parse<Summary>(brain.Consult(DigestPrompt, talk, 400));
            if (result == null || string.IsNullOrWhiteSpace(result.Text)) return null;
            string day = lines[lines.Count - 1].Substring(0, 10);
            lock (gate)
            {
                try
                {
                    string path = DaysPath;
                    string current = File.Exists(path) ? File.ReadAllText(path) : DaysHead;
                    if (current.Length > 0 && !current.EndsWith("\n", StringComparison.Ordinal)) current += "\n";
                    File.WriteAllText(path, current + "- " + day + ": " + Regex.Replace(Brain.Visible(result.Text), @"\s+", " ").Trim() + "\n");
                    File.WriteAllText(MarkPath, mark);
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
            if (result.Facts != null)
            {
                int kept = 0;
                foreach (string fact in result.Facts)
                {
                    if (kept >= 3 || string.IsNullOrWhiteSpace(fact)) continue;
                    string saved = Brain.Note(fact);
                    if (saved.StartsWith("Saved", StringComparison.Ordinal)) kept++;
                }
            }
            return result.Text;
        }

        [DataContract]
        private sealed class Summary
        {
            [DataMember(Name = "summary")] public string Text;
            [DataMember(Name = "facts")] public string[] Facts;
        }

        // The last few diary lines, for her prompt.
        internal static string Lately()
        {
            string path = DaysPath;
            if (path == null) return "";
            string[] lines;
            try { if (!File.Exists(path)) return ""; lines = File.ReadAllLines(path); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
            List<string> kept = new List<string>();
            foreach (string line in lines) if (line.StartsWith("- ", StringComparison.Ordinal)) kept.Add(line.Length > 260 ? line.Substring(0, 260) : line);
            int from = Math.Max(0, kept.Count - 10);
            return kept.Count == 0 ? "" : "\nWhat's been going on lately, from your diary (days.md). Bring it up when it fits, like asking how something went; don't recite it:\n" + string.Join("\n", kept.GetRange(from, kept.Count - from).ToArray());
        }
    }

    // Reads the JSON a brain was asked for, even with chatter, code fences or thinking around it.
    internal static class Json
    {
        internal static T Parse<T>(string reply) where T : class
        {
            string text = Brain.Visible(reply ?? "");
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try
            {
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(text.Substring(start, end - start + 1))))
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
            }
            catch (SerializationException) { return null; }
        }
    }

    // Their orbits: the projects they keep going in parallel instead of one at a time. They check in, she keeps the breadcrumbs.
    [DataContract]
    internal sealed class Orbit
    {
        [DataMember] public string Name;
        [DataMember] public string Started;
        [DataMember] public string Touched;
        [DataMember] public string Next;
        [DataMember] public List<string> Crumbs;
        [DataMember] public bool Landed;
    }

    [DataContract]
    internal sealed class OrbitBook { [DataMember] public List<Orbit> Orbits; }

    internal static class Orbits
    {
        internal static string FilePath;
        private static readonly object gate = new object();

        internal static string Handle(ToolArguments args)
        {
            if (args == null) args = new ToolArguments();
            string action = (args.Action ?? "list").Trim().ToLowerInvariant();
            if (action == "list" || action == "today") return List();
            string name = Regex.Replace(args.Name ?? "", @"\s+", " ").Trim(' ', '.', '"');
            string text = Regex.Replace(args.Text ?? "", @"\s+", " ").Trim();
            if (Safety.Stops(name + " " + text) != null) return "Not keeping that one, choom.";
            lock (gate)
            {
                List<Orbit> all = Load();
                Orbit orbit = Find(all, name, action == "note" && name.Length == 0);
                string now = Stamp();
                if (action == "add")
                {
                    if (name.Length == 0) return "What's the project called?";
                    if (orbit != null && !orbit.Landed) { Crumb(orbit, text, now); Save(all); return name + " is already in orbit. " + (text.Length > 0 ? "Logged: " + text + "." : ""); }
                    orbit = new Orbit { Name = Title(name), Started = now, Touched = now, Crumbs = new List<string>() };
                    all.Add(orbit);
                    Crumb(orbit, text, now);
                    Save(all);
                    return "Put " + orbit.Name + " in orbit" + (text.Length > 0 ? ", first note: " + text : "") + ". " + Active(all) + " in orbit now.";
                }
                if (action == "done")
                {
                    if (orbit == null) return "No orbit called " + name + ".";
                    orbit.Landed = true;
                    Crumb(orbit, text.Length > 0 ? text : "Landed.", now);
                    Save(all);
                    return orbit.Name + " landed. Nice. " + Active(all) + " still in orbit.";
                }
                // note: a check-in on a project, made if it's new
                if (orbit == null)
                {
                    if (name.Length == 0) return "Which project is that for?";
                    orbit = new Orbit { Name = Title(name), Started = now, Crumbs = new List<string>() };
                    all.Add(orbit);
                }
                orbit.Landed = false;
                if (Regex.IsMatch(text, @"^(next|next up|next step|then|todo|to do)\b[:\s]", RegexOptions.IgnoreCase)) orbit.Next = Regex.Replace(text, @"^(next|next up|next step|then|todo|to do)\b[:\s]*", "", RegexOptions.IgnoreCase);
                Crumb(orbit, text.Length > 0 ? text : "Checked in.", now);
                Save(all);
                return "Logged on " + orbit.Name + ": " + (text.Length > 0 ? text : "checked in") + ".";
            }
        }

        // What's in orbit, the one left alone longest first, so she can nudge the right one.
        internal static string List()
        {
            List<Orbit> all;
            lock (gate) all = Load();
            List<Orbit> active = all.FindAll(o => !o.Landed);
            if (active.Count == 0) return "Nothing in orbit yet. Tell me what you're juggling and I'll keep track.";
            active.Sort((a, b) => string.CompareOrdinal(a.Touched ?? "", b.Touched ?? ""));
            StringBuilder text = new StringBuilder("In orbit, the one left alone longest first:");
            foreach (Orbit o in active)
            {
                string last = o.Crumbs != null && o.Crumbs.Count > 0 ? o.Crumbs[o.Crumbs.Count - 1] : "";
                text.Append("\n- " + o.Name + " (" + Since(o.Touched) + ")" + (last.Length > 0 ? ". Last: " + Crop(last, 120) : "") + (string.IsNullOrEmpty(o.Next) ? "" : ". Next: " + Crop(o.Next, 100)));
            }
            text.Append("\nPick one to touch next and a first step small enough to start in two minutes.");
            return text.ToString();
        }

        // For her prompt: a line per orbit.
        internal static string Context()
        {
            List<Orbit> all;
            lock (gate) all = Load();
            List<Orbit> active = all.FindAll(o => !o.Landed);
            if (active.Count == 0) return "";
            active.Sort((a, b) => string.CompareOrdinal(b.Touched ?? "", a.Touched ?? ""));
            if (active.Count > 8) active = active.GetRange(0, 8);
            StringBuilder text = new StringBuilder("\nTheir orbits, the projects they keep going in parallel (on purpose, so never push schedules or doing one thing at a time). When they check in, report progress, start or finish one, or ask what they're doing today, use orbit:");
            foreach (Orbit o in active)
            {
                string last = o.Crumbs != null && o.Crumbs.Count > 0 ? o.Crumbs[o.Crumbs.Count - 1] : "";
                text.Append("\n- " + o.Name + " (" + Since(o.Touched) + ")" + (last.Length > 0 ? ": " + Crop(last, 90) : ""));
            }
            return text.ToString();
        }

        private static Orbit Find(List<Orbit> all, string name, bool latest)
        {
            if (latest)
            {
                Orbit newest = null;
                foreach (Orbit o in all) if (!o.Landed && (newest == null || string.CompareOrdinal(o.Touched ?? "", newest.Touched ?? "") > 0)) newest = o;
                return newest;
            }
            string want = Plain(name);
            if (want.Length == 0) return null;
            Orbit best = null;
            int score = 0;
            foreach (Orbit o in all)
            {
                string have = Plain(o.Name);
                int s = have == want ? 100 : have.Contains(want) || want.Contains(have) ? 60 : Shared(have, want) * 15;
                if (!o.Landed) s += 5;
                if (s > score && s >= 20) { score = s; best = o; }
            }
            return best;
        }

        private static int Shared(string a, string b)
        {
            int n = 0;
            HashSet<string> words = new HashSet<string>(a.Split(' '));
            foreach (string w in b.Split(' ')) if (w.Length > 2 && words.Contains(w)) n++;
            return n;
        }

        private static string Plain(string text) { return Regex.Replace(Regex.Replace((text ?? "").ToLowerInvariant(), @"[^a-z0-9 ]", " "), @"\s+", " ").Replace(" the ", " ").Trim(); }

        private static void Crumb(Orbit orbit, string text, string now)
        {
            if (orbit.Crumbs == null) orbit.Crumbs = new List<string>();
            orbit.Touched = now;
            if (text.Length == 0) return;
            orbit.Crumbs.Add(now.Substring(0, 10) + ": " + Crop(text, 300));
            while (orbit.Crumbs.Count > 30) orbit.Crumbs.RemoveAt(0);
        }

        private static string Title(string name) { return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1); }

        private static string Crop(string text, int max) { return text.Length <= max ? text : text.Substring(0, max - 3) + "..."; }

        private static string Stamp() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }

        private static string Since(string stamp)
        {
            DateTime at;
            if (!DateTime.TryParseExact(stamp ?? "", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at)) return "not touched yet";
            double days = (DateTime.Now.Date - at.Date).TotalDays;
            return days < 1 ? "touched today" : days < 2 ? "touched yesterday" : "untouched for " + (int)days + " days";
        }

        private static int Active(List<Orbit> all) { return all.FindAll(o => !o.Landed).Count; }

        private static List<Orbit> Load()
        {
            string path = FilePath;
            if (path == null || !File.Exists(path)) return new List<Orbit>();
            try
            {
                using (FileStream file = File.OpenRead(path))
                {
                    OrbitBook book = (OrbitBook)new DataContractJsonSerializer(typeof(OrbitBook)).ReadObject(file);
                    return book == null || book.Orbits == null ? new List<Orbit>() : book.Orbits.FindAll(o => o != null && !string.IsNullOrEmpty(o.Name));
                }
            }
            catch (Exception) { return new List<Orbit>(); }
        }

        private static void Save(List<Orbit> all)
        {
            string path = FilePath;
            if (path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (FileStream file = File.Create(path)) new DataContractJsonSerializer(typeof(OrbitBook)).WriteObject(file, new OrbitBook { Orbits = all });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

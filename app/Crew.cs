using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CapriDesktop
{
    // A job she hands to her crew: it runs on its own thread with its own brain, so talking to her never cuts it off.
    [DataContract]
    internal sealed class Gig
    {
        [DataMember] public string Id;
        [DataMember] public string Ask;
        [DataMember] public string Title;
        [DataMember] public string Worker;
        [DataMember] public string Plan;
        [DataMember] public string Folder;
        [DataMember] public string Status;
        [DataMember] public string Started;
        [DataMember] public string Ended;
        [DataMember] public List<string> Log;
        [DataMember] public string Result;
        [DataMember] public string Review;
        [DataMember] public string Report;
        [DataMember] public string Mind;
        // What they two were just talking about, so "that download" means the one they meant.
        [DataMember] public string Context;
        // A coding job: OpenCode's session, the commit it started from, the branch it works on, and whether the repo was clean then.
        [DataMember] public string Session;
        [DataMember] public string Base;
        [DataMember] public string Branch;
        [DataMember] public bool WasClean;
        internal string Diff, Checks;
        internal volatile bool Stop;
        internal volatile Process Running;
        internal bool Finished { get { return Status == "done" || Status == "failed" || Status == "stopped"; } }
    }

    [DataContract]
    internal sealed class GigBook { [DataMember] public List<Gig> Gigs; }

    [DataContract]
    internal sealed class GigPlan
    {
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "worker")] public string Worker;
        [DataMember(Name = "plan")] public string Plan;
    }

    [DataContract]
    internal sealed class GigVerdict
    {
        [DataMember(Name = "verdict")] public string Verdict;
        [DataMember(Name = "notes")] public string Notes;
        [DataMember(Name = "report")] public string Report;
    }

    internal sealed class Crew
    {
        // A fresh brain wired like hers, for one job's thread.
        internal Func<Brain> Hire;
        // The brain that checks finished work; null means the worker's own brain checks it.
        internal Func<Brain> Overseer;
        // She says this out loud when she's free.
        internal Action<string> Speak;
        // The jobs window redraws.
        internal Action Changed;
        internal Func<bool> Blackwall = delegate { return false; };
        internal Func<string> OpenCodeModel = delegate { return null; };
        // The last few lines of their talk, read when a job starts.
        internal Func<string> Context = delegate { return null; };

        // Saved keys from the vault, name and value: every command and OpenCode get them as environment variables, and no output ever shows them.
        internal Func<List<KeyValuePair<string, string>>> Keys = delegate { return new List<KeyValuePair<string, string>>(); };
        // The OpenCode model for her job brain, like cerebras/gpt-oss-120b, used when they haven't named one.
        internal Func<string> JobCodeModel = delegate { return null; };
        // Her coding brain: plans, briefs and reviews coding jobs (a model that doesn't refuse security work), and her switchboard OpenCode codes through.
        internal Func<Brain> CodeHire;
        internal readonly Relay Relay = new Relay();
        internal Func<bool> HasCodeBrains = delegate { return false; };

        // A small brain on this PC gets short jobs and a short memory of them; a cloud brain gets room to really work.
        internal const int MaxSteps = 16, CloudSteps = 40;
        private static int StepsFor(Brain brain) { return brain.Remote != null ? CloudSteps : MaxSteps; }
        private readonly string bookPath;
        private readonly string home;
        private readonly List<Gig> gigs = new List<Gig>();
        private readonly object gate = new object();
        private int next = 1;
        private static bool? openCode;

        // Their connected apps (MCP servers), for the apps worker.
        internal readonly Apps Apps;

        private readonly string data;

        internal Crew(string data)
        {
            this.data = data;
            Apps = new Apps(data);
            Apps.Fill = Fill;
            bookPath = Path.Combine(data, "jobs.json");
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Capri Jobs");
            Load();
        }

        internal string Home { get { return home; } }

        internal List<Gig> All { get { lock (gate) return new List<Gig>(gigs); } }

        // ---- her tools -------------------------------------------------------------------------------------

        internal string Handle(string tool, ToolArguments args)
        {
            if (args == null) args = new ToolArguments();
            if (tool == "start_job") return Start(args.Task, args.Kind, args.Target);
            if (tool == "jobs") return args.Action == "stop" ? Halt(args.Name) : Summary();
            return "There's no crew tool called " + tool + ".";
        }

        internal string Start(string ask, string kind, string target)
        {
            ask = Regex.Replace(ask ?? "", @"\s+", " ").Trim();
            if (ask.Length == 0) return "Which job, choom?";
            string blocked = Safety.Stops(ask);
            if (blocked != null) return blocked;
            Gig gig = new Gig { Ask = ask, Title = Shorten(ask), Worker = Pick(kind, ask), Status = "planning", Started = Now(), Log = new List<string>() };
            if (WantsApps(kind, ask)) gig.Worker = "apps";
            try { string talk = Context(); if (!string.IsNullOrWhiteSpace(talk)) gig.Context = Tail(talk.Trim(), 2500); } catch (Exception) { }
            lock (gate) { gig.Id = (next++).ToString(CultureInfo.InvariantCulture); gigs.Add(gig); while (gigs.Count > 40) gigs.RemoveAt(0); }
            gig.Folder = FolderFor(gig, target);
            Save();
            Notify();
            Thread worker = new Thread(delegate() { Run(gig); });
            worker.IsBackground = true;
            worker.Name = "Capri crew job " + gig.Id;
            worker.Start();
            return "Started job " + gig.Id + " (" + gig.Title + ") with the " + WorkerName(gig.Worker) + " worker. It runs in the background: the plan comes in a moment and you'll report back when it's done.";
        }

        internal string Summary()
        {
            List<Gig> all = All;
            if (all.Count == 0) return "No jobs yet. Nothing's running.";
            StringBuilder text = new StringBuilder();
            int shown = 0;
            for (int i = all.Count - 1; i >= 0 && shown < 5; i--, shown++)
            {
                Gig gig = all[i];
                string[] lines = LogLines(gig);
                string last = lines.Length > 0 ? lines[lines.Length - 1] : "";
                if (last.Length > 120) last = last.Substring(0, 117) + "...";
                text.Append("Job " + gig.Id + " (" + gig.Title + "): " + gig.Status + ", " + WorkerName(gig.Worker) + " worker, " + Age(gig) + (gig.Finished ? "" : ". Last: " + last) + (gig.Finished && !string.IsNullOrEmpty(gig.Report) ? ". " + gig.Report : "") + "\n");
            }
            return text.ToString().Trim();
        }

        internal string Halt(string which)
        {
            Gig gig = Find(which);
            if (gig == null) return "No running job matches that.";
            Stop(gig);
            return "Stopping job " + gig.Id + " (" + gig.Title + ").";
        }

        internal void Stop(Gig gig)
        {
            gig.Stop = true;
            Process running = gig.Running;
            if (running != null) Kill(running);
            Note(gig, "Stopped by you.");
        }

        private Gig Find(string which)
        {
            List<Gig> all = All;
            string want = (which ?? "").Trim().ToLowerInvariant().TrimStart('#');
            for (int i = all.Count - 1; i >= 0; i--)
            {
                Gig gig = all[i];
                if (gig.Finished) continue;
                if (want.Length == 0 || want == gig.Id || Regex.Replace(want, @"^job\s*", "") == gig.Id || gig.Title.ToLowerInvariant().Contains(want) || gig.Ask.ToLowerInvariant().Contains(want)) return gig;
            }
            return null;
        }

        // ---- a job, start to finish ------------------------------------------------------------------------

        private void Run(Gig gig)
        {
            try
            {
                Brain brain = Hire();
                if (gig.Worker == "builder" && CodeHire != null)
                {
                    Brain coder = null;
                    try { coder = CodeHire(); } catch (Exception) { coder = null; }
                    if (coder != null && coder.Model != null) brain = coder;
                }
                if (brain == null || brain.Model == null) { Finish(gig, "failed", "My brain's offline, so the crew can't work. Set one up in Capri Mainframe first."); return; }
                gig.Mind = MindName(brain);
                PlanIt(gig, brain);
                if (gig.Stop) { Finish(gig, "stopped", "Stopped job " + gig.Id + " before it started."); return; }
                Say(gig.Plan);
                string notes = null;
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    Set(gig, "running");
                    if (attempt > 1) Note(gig, "Second try, with the overseer's notes.");
                    if (gig.Worker == "research") Research(gig, brain, notes);
                    else if (gig.Worker == "builder") Build(gig, brain, notes);
                    else if (gig.Worker == "kaggle") Terminal(gig, brain, notes, KagglePrompt + KaggleState());
                    else if (gig.Worker == "apps") AppsWork(gig, brain, notes);
                    else Terminal(gig, brain, notes);
                    if (gig.Stop) { Finish(gig, "stopped", "Stopped " + gig.Title + ". " + (gig.Result ?? "")); return; }
                    Set(gig, "reviewing");
                    GigVerdict verdict = Oversee(gig, brain);
                    gig.Review = verdict.Notes;
                    if (verdict.Verdict == "retry" && attempt < 2) { notes = verdict.Notes; Note(gig, "Overseer sent it back: " + verdict.Notes); continue; }
                    if (gig.Worker == "builder" && verdict.Verdict != "fail") Wrap(gig);
                    Finish(gig, verdict.Verdict == "fail" ? "failed" : "done", verdict.Report + (gig.Worker == "builder" && gig.Branch != null && verdict.Verdict != "fail" ? " It's on the " + gig.Branch + " branch." : ""));
                    return;
                }
            }
            catch (Exception error)
            {
                Brain.Log("crew", -1, "job " + gig.Id + ": " + error.Message);
                Finish(gig, "failed", "Job " + gig.Id + ", " + gig.Title + ", crashed: " + Voice.Short(error.Message));
            }
        }

        private void Finish(Gig gig, string status, string report)
        {
            report = Hide(report);
            gig.Result = Hide(gig.Result);
            gig.Report = string.IsNullOrWhiteSpace(report) ? (status == "done" ? "Done with " + gig.Title + "." : gig.Title + " didn't work out.") : report.Trim();
            gig.Ended = Now();
            Set(gig, status);
            Note(gig, "Report: " + gig.Report);
            Say(gig.Report);
        }

        private const string PlanPrompt =
            "You plan jobs for Capri's crew. Capri is a cocky, fiery desktop companion and the user's partner, living on their Windows PC, and her crew does the bigger jobs in the background.\n" +
            "Workers:\n" +
            "- terminal: runs PowerShell on their PC. Installs, setups, downloads, models, git, moving or making files, checking things.\n" +
            "- kaggle: anything on Kaggle. Runs and sets up their Kaggle notebooks on Kaggle's free GPUs from this PC with the kaggle command: making or editing a notebook, starting a run, checking on it, getting its output, downloading a model onto Kaggle.\n" +
            "- research: searches the web and reads pages, then writes up notes.\n" +
            "- builder: her coding crew, OpenCode with her coding brains, for real software work in their repos: features, fixes, refactors, debugging, tools, exploits for their own legal security work, new projects. It works on its own git branch and gets reviewed against the diff and the tests.\n" +
            "- apps: works inside their connected apps (listed below, like a real web browser or GitHub) through the apps' own tools, plus PowerShell. Anything that needs clicking around a website, signing into a site, or one of those apps goes here.\n" +
            "Reply with only JSON, no other text: {\"title\": \"a short name for the job, 2 to 5 words\", \"worker\": \"terminal|kaggle|research|builder|apps\", \"plan\": \"what Capri says out loud about the plan\"}.\n" +
            "Use the talk around the job to work out what they really meant (which thing, where), and plan for that.\n" +
            "The plan is Capri talking to them, casual and in first person, one or two sentences: what she's putting which worker on and how it'll go, and which brain is doing the thinking. " +
            "Talk like a friend, not an assistant: no lists, no step numbers, no 'I will now', no 'Certainly', no 'Here's the plan'. Vary how you start it.";

        private void PlanIt(Gig gig, Brain brain)
        {
            List<string> apps = Apps.Names;
            string user = "Their job: " + Asked(gig) + "\nWorker that looks right: " + gig.Worker + (HasOpenCode ? "" : " (OpenCode isn't installed, so no builder)") +
                "\nConnected apps: " + (apps.Count == 0 ? "none, so no apps worker" : string.Join(", ", apps.ToArray())) +
                "\nBrain doing the thinking: " + gig.Mind + "\nJob folder: " + gig.Folder;
            GigPlan plan = null;
            try { plan = ParseJson<GigPlan>(brain.Consult(PlanPrompt, user, 300)); }
            catch (Exception error) { Note(gig, "Planning glitched: " + Voice.Short(error.Message)); }
            if (plan != null)
            {
                if (!string.IsNullOrWhiteSpace(plan.Title)) gig.Title = Shorten(plan.Title);
                string worker = (plan.Worker ?? "").Trim().ToLowerInvariant();
                if (worker == "terminal" || worker == "kaggle" || worker == "research" || (worker == "builder" && HasOpenCode) || (worker == "apps" && apps.Count > 0)) gig.Worker = worker;
                if (!string.IsNullOrWhiteSpace(plan.Plan)) gig.Plan = Brain.Visible(plan.Plan).Trim();
            }
            if (string.IsNullOrWhiteSpace(gig.Plan)) gig.Plan = "Putting my " + WorkerName(gig.Worker) + " worker on " + gig.Title + ", with " + gig.Mind + " doing the thinking. I'll tell you when it's done.";
            Note(gig, "Plan: " + gig.Plan);
            Save();
            Notify();
        }

        // ---- the terminal worker ---------------------------------------------------------------------------

        private const string TerminalPrompt =
            "You are a worker in Capri's crew. You do one job on the user's Windows 11 PC through PowerShell, one tool call per reply.\n" +
            "Each run_powershell is a fresh Windows PowerShell 5.1 session that starts in the job folder: nothing carries over except files, installs and environment changes saved to disk.\n" +
            "Prefer winget for installing apps, with --silent --accept-source-agreements --accept-package-agreements. Nothing can wait for typing: never use Read-Host, pause, or anything that opens a prompt; pass yes, silent or force flags instead.\n" +
            "Don't delete or overwrite their files unless the job says to, and leave system settings, the registry, drivers and disks alone unless the job is about them.\n" +
            "If something needs them, like signing in, a password, an API key that isn't in their saved keys, a payment or a click in a browser, stop and call finish with action failed and say exactly what they need to do (a key goes in the Keys box on Capri's jobs board).\n" +
            "Jobs are often short, like \"download DeepSeek\": read what you two were just talking about to work out what they meant, like which model, which site or which machine (a job about a Kaggle notebook happens on Kaggle, through the kaggle command, not on this PC). Don't install some other app just because its name matches. If it's still unclear what they want, don't guess: call finish with action failed and ask them the one question that settles it.\n" +
            "Check your work before you finish, like running the program's --version or listing the files. Then call finish with action done and what you did, what they need to know and where things are, in plain words.";

        private void Terminal(Gig gig, Brain brain, string notes, string extra = null)
        {
            string prompt = TerminalPrompt + KeysNote() + AppsNote() + (extra ?? "");
            List<string> steps = new List<string>();
            int most = StepsFor(brain);
            bool cloud = brain.Remote != null;
            for (int step = 0; step < most && !gig.Stop; step++)
            {
                List<KeyValuePair<string, ToolArguments>> calls = new List<KeyValuePair<string, ToolArguments>>();
                string written = brain.Step(prompt, Brief(gig, notes, steps, cloud ? 8 : 5, cloud ? 16000 : 6500, most), Render(TerminalTools, false), Render(TerminalTools, true), Listing(TerminalTools), calls, 700);
                string tool;
                ToolArguments args;
                if (!Pick(calls, written, out tool, out args)) { steps.Add("(You replied without a tool call. Call exactly one tool.)"); continue; }
                if (tool == "finish") { gig.Result = Clean(args.Text, "Done."); if (args.Action == "failed") Note(gig, "Worker says it can't finish: " + gig.Result); return; }
                string outcome = Hide(Act(gig, tool, args));
                steps.Add(Describe(tool, args) + "\n" + Trim(outcome, cloud ? 2500 : 1100));
            }
            if (!gig.Stop && gig.Result == null) gig.Result = "Ran out of steps before finishing.";
        }

        private static readonly string[,] TerminalTools =
        {
            { "run_powershell", "Run a PowerShell command on their PC and get its output.", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"description\":\"The PowerShell command or script\"},\"seconds\":{\"type\":\"string\",\"description\":\"How long it may run, in seconds, for long installs or downloads (default 120, up to 1800)\"}},\"required\":[\"text\"]}" },
            { "read_file", "Read a text file.", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"The file's path\"}},\"required\":[\"name\"]}" },
            { "write_file", "Write a text file, replacing it if it exists.", "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"The file's path\"},\"text\":{\"type\":\"string\",\"description\":\"Everything the file should contain\"}},\"required\":[\"name\",\"text\"]}" },
            { "finish", "End the job and say how it went.", "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"How it went\",\"enum\":[\"done\",\"failed\"]},\"text\":{\"type\":\"string\",\"description\":\"What you did, what they need to know or do, and where things are\"}},\"required\":[\"action\",\"text\"]}" }
        };

        private string Act(Gig gig, string tool, ToolArguments args)
        {
            try
            {
                if (tool == "run_powershell")
                {
                    string command = (args.Text ?? "").Trim();
                    if (command.Length == 0) return "Empty command.";
                    string danger = Danger(command, Blackwall());
                    if (danger != null) { Note(gig, "Blocked: " + command); return "Blocked: " + danger; }
                    int seconds;
                    if (!int.TryParse(args.Seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) seconds = 120;
                    seconds = Math.Max(10, Math.Min(1800, seconds));
                    Note(gig, "PS> " + command);
                    int code;
                    string output = Shell(gig, command, gig.Folder, seconds, out code);
                    Note(gig, "exit " + code + ": " + Trim(output, 300).Replace("\n", " | "));
                    return "Exit code " + code + ". Output:\n" + Trim(output, 3000);
                }
                if (tool == "read_file")
                {
                    string path = Resolve(gig, args.Name);
                    Note(gig, "Read " + path);
                    if (!File.Exists(path)) return "No file at " + path + ".";
                    return Trim(File.ReadAllText(path), 4000);
                }
                if (tool == "write_file")
                {
                    string path = Resolve(gig, args.Name);
                    if (Regex.IsMatch(path, @"^[a-z]:\\windows\\|\\system32\\", RegexOptions.IgnoreCase)) return "Blocked: that's a system folder.";
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, args.Text ?? "");
                    Note(gig, "Wrote " + path);
                    return "Wrote " + (args.Text ?? "").Length + " characters to " + path + ".";
                }
                return "There's no tool called " + tool + ".";
            }
            catch (Exception error) { return "That failed: " + error.Message; }
        }

        // What the crew never runs: wiping disks, the boot setup or Windows itself, turning off protection, and (unless Blackwall's on) scripts piped straight from the web.
        private static readonly Regex Wrecking = new Regex(
            @"\b(format-volume|clear-disk|initialize-disk|remove-partition|diskpart|bcdedit|vssadmin\s+delete|wbadmin\s+delete|cipher(\.exe)?\s+/w|stop-computer|restart-computer|set-mppreference|add-mppreference|uninstall-windowsfeature)\b" +
            @"|\bshutdown(\.exe)?\s+[/-][srhpg]\b|\bformat(\.com)?\s+[a-z]:|\breg(\.exe)?\s+delete\s+hk(lm|ey_local_machine)|\bnetsh\s+advfirewall\s+set\b" +
            @"|\b(remove-item|rm|del|erase|rd|rmdir|ri)\b[^;|\r\n]*(\s|'|"")([a-z]:\\?(\*)?|c:\\windows\S*|c:\\program files\S*|\$env:(windir|systemroot|programfiles\S*|userprofile)\\?|~\\?)(\s|'|""|;|$)",
            RegexOptions.IgnoreCase);
        private static readonly Regex Piped = new Regex(@"\b(iwr|irm|invoke-webrequest|invoke-restmethod|curl|wget|downloadstring)\b[^|\r\n]*\|\s*(iex|invoke-expression|powershell|pwsh|cmd|sh|bash)\b|\biex\s*\(\s*(new-object|\(?irm|\(?iwr|\(?invoke-)", RegexOptions.IgnoreCase);

        internal static string Danger(string command, bool blackwall)
        {
            if (Wrecking.IsMatch(command)) return "that could wipe or break their PC. Don't run it; if the job really needs it, finish and tell them to do it themselves.";
            if (!blackwall && Piped.IsMatch(command)) return "running a script straight from the web isn't allowed. Download the file, read it, then run it, or use winget.";
            return null;
        }

        // ---- the research worker ---------------------------------------------------------------------------

        private const string ResearchPrompt =
            "You are the research worker in Capri's crew. You look a topic up on the web for the user, one tool call per reply.\n" +
            "Search, open the most useful results and read them. Prefer primary and recent sources, and search again with better words when results are thin. Usually three to five good pages is enough.\n" +
            "Then call finish with action done and your notes as text: the answer first, in plain language, then the key details, then the sources as plain links. Under 400 words. Say when sources disagree or something couldn't be confirmed.";

        private static readonly string[,] ResearchTools =
        {
            { "web_search", "Search the web.", "{\"type\":\"object\",\"properties\":{\"search\":{\"type\":\"string\",\"description\":\"What to search for\"}},\"required\":[\"search\"]}" },
            { "open_page", "Open a web page and read its text.", "{\"type\":\"object\",\"properties\":{\"site\":{\"type\":\"string\",\"description\":\"The page's full address\"}},\"required\":[\"site\"]}" },
            { "finish", "End the research with your notes.", "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"description\":\"How it went\",\"enum\":[\"done\",\"failed\"]},\"text\":{\"type\":\"string\",\"description\":\"Your notes: the answer, key details, then sources\"}},\"required\":[\"action\",\"text\"]}" }
        };

        private void Research(Gig gig, Brain brain, string notes)
        {
            List<string> steps = new List<string>();
            int most = brain.Remote != null ? 30 : MaxSteps;
            bool cloud = brain.Remote != null;
            for (int step = 0; step < most && !gig.Stop; step++)
            {
                List<KeyValuePair<string, ToolArguments>> calls = new List<KeyValuePair<string, ToolArguments>>();
                string written = brain.Step(ResearchPrompt, Brief(gig, notes, steps, cloud ? 6 : 3, cloud ? 20000 : 7000, most), Render(ResearchTools, false), Render(ResearchTools, true), Listing(ResearchTools), calls, 1100);
                string tool;
                ToolArguments args;
                if (!Pick(calls, written, out tool, out args)) { steps.Add("(You replied without a tool call. Call exactly one tool.)"); continue; }
                if (tool == "finish")
                {
                    gig.Result = Clean(args.Text, "No notes.");
                    try
                    {
                        Directory.CreateDirectory(gig.Folder);
                        string path = Path.Combine(gig.Folder, "notes.md");
                        File.WriteAllText(path, "# " + gig.Title + "\n\n> Asked: " + gig.Ask + "\n\n" + gig.Result.Replace("\r\n", "\n") + "\n");
                        Note(gig, "Notes saved to " + path);
                    }
                    catch (Exception error) { Note(gig, "Couldn't save the notes: " + error.Message); }
                    return;
                }
                string found;
                if (tool == "web_search") { Note(gig, "Searching: " + args.Search); found = Search(args.Search); }
                else if (tool == "open_page") { Note(gig, "Reading: " + args.Site); found = Page(args.Site); }
                else found = "There's no tool called " + tool + ".";
                steps.Add(Describe(tool, args) + "\n" + Trim(found, cloud ? 4000 : 2200));
            }
            if (!gig.Stop && gig.Result == null) gig.Result = "Ran out of steps before writing it up.";
        }

        internal static Func<string, string> Fetch = Get;

        internal static string Search(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return "Empty search.";
            List<string> results = new List<string>();
            string html = Fetch("https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query));
            foreach (Match hit in Regex.Matches(html ?? "", @"class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>(?:.*?class=""result__snippet""[^>]*>(.*?)</a>)?", RegexOptions.Singleline))
            {
                string link = WebUtility.HtmlDecode(hit.Groups[1].Value);
                Match real = Regex.Match(link, @"[?&]uddg=([^&]+)");
                if (real.Success) link = Uri.UnescapeDataString(real.Groups[1].Value);
                if (link.StartsWith("//", StringComparison.Ordinal)) link = "https:" + link;
                if (link.Contains("duckduckgo.com/y.js")) continue;
                results.Add((results.Count + 1) + ". " + Text(hit.Groups[2].Value) + "\n   " + link + "\n   " + Text(hit.Groups[3].Value));
                if (results.Count >= 7) break;
            }
            if (results.Count == 0)
            {
                html = Fetch("https://www.bing.com/search?q=" + Uri.EscapeDataString(query) + "&setlang=en");
                foreach (Match hit in Regex.Matches(html ?? "", @"<li class=""b_algo""[^>]*>.*?<h2[^>]*><a[^>]*href=""([^""]+)""[^>]*>(.*?)</a>.*?(?:<p[^>]*>(.*?)</p>)?", RegexOptions.Singleline))
                {
                    results.Add((results.Count + 1) + ". " + Text(hit.Groups[2].Value) + "\n   " + WebUtility.HtmlDecode(hit.Groups[1].Value) + "\n   " + Text(hit.Groups[3].Value));
                    if (results.Count >= 7) break;
                }
            }
            return results.Count == 0 ? "No results came back for that search. Try different words." : string.Join("\n", results.ToArray());
        }

        // A quick answer for her, not a job: live weather when it's about weather, else the top search results and the best page's text.
        internal static string LookUp(string query)
        {
            query = Regex.Replace(query ?? "", @"\s+", " ").Trim();
            if (query.Length == 0) return "Nothing to look up.";
            if (Regex.IsMatch(query, @"\b(weather|forecast|temperature|temp|how (hot|cold|warm)|rain(ing|y)?|snow(ing|y)?|sunny|humid(ity)?|wind(y)?|degrees|storm(s|y)?)\b", RegexOptions.IgnoreCase))
            {
                string weather = Weather(query);
                if (weather != null) return "Looked up " + Voice.Quote(query) + ":\n" + weather;
            }
            string results = Search(query);
            StringBuilder text = new StringBuilder("Search results:\n" + Trim(results, 1800));
            Match first = Regex.Match(results, @"^1\. .*\n\s+(https?://\S+)", RegexOptions.Multiline);
            if (first.Success)
            {
                string page = Page(first.Groups[1].Value);
                if (!page.StartsWith("Couldn't", StringComparison.Ordinal)) text.Append("\n\nFrom " + first.Groups[1].Value + ":\n" + Trim(page, 1800));
            }
            return "Looked up " + Voice.Quote(query) + " online:\n" + text;
        }

        // Live weather from wttr.in; with no place named, it goes by where the PC is.
        internal static string Weather(string query)
        {
            Match at = Regex.Match(query, @"\b(?:in|for|at)\s+([A-Za-z][A-Za-z .,'-]{1,40}?)(?:\s+(?:today|tomorrow|tonight|this week|right now|now|later)|[?.!]|$)", RegexOptions.IgnoreCase);
            string place = at.Success ? at.Groups[1].Value.Trim() : "";
            string json = Fetch("https://wttr.in/" + Uri.EscapeDataString(place) + "?format=j1");
            if (string.IsNullOrEmpty(json)) return null;
            Match current = Regex.Match(json, @"""current_condition""\s*:\s*\[(.*?)\]\s*,\s*""nearest_area""", RegexOptions.Singleline);
            string now = current.Success ? current.Groups[1].Value : json;
            string temp = Field(now, "temp_F");
            if (temp == null) return null;
            Match desc = Regex.Match(now, @"""weatherDesc""\s*:\s*\[\s*\{\s*""value""\s*:\s*""([^""]*)""");
            Match area = Regex.Match(json, @"""areaName""\s*:\s*\[\s*\{\s*""value""\s*:\s*""([^""]*)""");
            StringBuilder text = new StringBuilder("Weather for " + (area.Success ? area.Groups[1].Value : place.Length > 0 ? place : "their area") +
                " (wttr.in): right now " + temp + "\u00B0F, feels like " + Field(now, "FeelsLikeF") + "\u00B0F, " + (desc.Success ? desc.Groups[1].Value.Trim().ToLowerInvariant() : "") + ", humidity " + Field(now, "humidity") + "%.");
            string[] days = { "Today", "Tomorrow", "The day after" };
            int d = 0;
            foreach (Match day in Regex.Matches(json, @"""maxtempF""\s*:\s*""(-?\d+)"".{0,300}?""mintempF""\s*:\s*""(-?\d+)""", RegexOptions.Singleline))
            {
                if (d >= days.Length) break;
                text.Append(" " + days[d++] + ": high " + day.Groups[1].Value + "\u00B0F, low " + day.Groups[2].Value + "\u00B0F.");
            }
            return text.ToString();
        }

        private static string Field(string json, string key)
        {
            Match m = Regex.Match(json ?? "", @"""" + key + @"""\s*:\s*""([^""]*)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        internal static string Page(string address)
        {
            address = (address ?? "").Trim();
            if (!Regex.IsMatch(address, @"^https?://", RegexOptions.IgnoreCase)) address = "https://" + address;
            string html = Fetch(address);
            if (string.IsNullOrEmpty(html)) return "Couldn't open that page.";
            string text = Text(Regex.Replace(html, @"<(script|style|noscript|svg|nav|footer|header)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase));
            return text.Length == 0 ? "That page had no readable text." : Trim(text, 6000);
        }

        private static string Text(string html)
        {
            string text = Regex.Replace(html ?? "", @"<br\s*/?>|</p>|</li>|</h\d>|</div>", "\n", RegexOptions.IgnoreCase);
            text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));
            text = Regex.Replace(text, @"[ \t\f\v]+", " ");
            return Regex.Replace(text, @"\s*\n\s*(\n\s*)+", "\n").Trim();
        }

        private static string Get(string address)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
                request.Headers["Accept-Language"] = "en-US,en;q=0.8";
                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    char[] buffer = new char[400000];
                    int read = reader.ReadBlock(buffer, 0, buffer.Length);
                    return new string(buffer, 0, read);
                }
            }
            catch (WebException) { return ""; }
            catch (IOException) { return ""; }
            catch (UriFormatException) { return ""; }
        }

        // ---- the builder worker (OpenCode) -----------------------------------------------------------------

        internal static bool HasOpenCode
        {
            get
            {
                if (openCode.HasValue) return openCode.Value;
                bool found = false;
                try
                {
                    string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                    foreach (string dir in path.Split(';'))
                    {
                        if (dir.Trim().Length == 0) continue;
                        foreach (string name in new[] { "opencode.cmd", "opencode.exe", "opencode.ps1", "opencode" })
                            if (File.Exists(Path.Combine(dir.Trim(), name))) { found = true; break; }
                        if (found) break;
                    }
                    if (!found) found = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "opencode.cmd"));
                }
                catch (Exception) { }
                openCode = found;
                return found;
            }
        }

        private const string BriefPrompt =
            "You write the brief a senior engineer hands to a coding agent (OpenCode) that will work alone in the user's repo. The user is a serious developer who works on complex systems, including security tools; take the job as asked.\n" +
            "From their words and the talk around the job, write: the goal in one or two sentences; the context that matters (what they said, what exists in the repo, decisions already made); the concrete changes or deliverables; constraints (keep their style, don't touch unrelated code, no new heavy dependencies unless needed); and how to verify it (which tests or commands to run, what working looks like).\n" +
            "Be specific and technical, and don't invent requirements they didn't imply. Plain text with short headings, no preamble.";

        // A coding job: on its own branch, with a real brief, OpenCode through her switchboard, then the actual diff and checks for the reviewer.
        private void Build(Gig gig, Brain brain, string notes)
        {
            Directory.CreateDirectory(gig.Folder);
            bool fresh = gig.Base == null && gig.Branch == null;
            if (fresh) Prepare(gig);
            string briefPath = Path.Combine(home, gig.Id + "-brief.md");
            string message;
            if (notes == null)
            {
                string brief = null;
                try { brief = Brain.Visible(brain.Consult(BriefPrompt, "Their job: " + Asked(gig) + "\nPlan: " + gig.Plan + "\nRepo: " + gig.Folder + "\nWhat's in it:\n" + Layout(gig.Folder) + ProjectNotes(gig.Folder), 2500)).Trim(); }
                catch (Exception error) { Note(gig, "Couldn't write the brief: " + Voice.Short(error.Message)); }
                if (string.IsNullOrWhiteSpace(brief) || Regex.IsMatch(brief, @"^(I can'?t|I cannot|I won'?t|Sorry)", RegexOptions.IgnoreCase)) brief = "Job: " + gig.Ask + (string.IsNullOrEmpty(gig.Context) ? "" : "\n\nWhat we were just talking about:\n" + gig.Context);
                brief += "\n\nWhen you're done: run the project's tests or build if it has them, then end with a short summary of what you changed, how to run or use it, and anything left to do.";
                try { Directory.CreateDirectory(home); File.WriteAllText(briefPath, brief + KeysNote(), new UTF8Encoding(false)); } catch (Exception) { }
                Note(gig, "Brief written: " + briefPath);
                message = "Do the job in the attached brief, working in this repo.";
            }
            else
            {
                try { File.WriteAllText(briefPath, "A reviewer checked your work and found problems:\n" + notes + "\n\nFix them, run the checks again, and end with a short summary of what you changed.", new UTF8Encoding(false)); } catch (Exception) { }
                message = "The reviewer sent your work back; the attached note says why. Fix it.";
            }
            string session = gig.Session ?? (fresh ? RecentSession(gig.Folder) : null);
            if (session != null && gig.Session == null) Note(gig, "Picking up OpenCode's last session in this repo.");
            string model = OpenCodeModel();
            bool own = !string.IsNullOrWhiteSpace(model) && model.Contains("/") && !model.Contains(":");
            Dictionary<string, string> extra = new Dictionary<string, string>();
            if (!own && HasCodeBrains())
            {
                Relay.Start();
                model = "capri/" + Relay.ModelName;
                extra["OPENCODE_CONFIG_CONTENT"] = Relay.OpenCodeConfig();
            }
            else if (!own)
            {
                string fallback = JobCodeModel == null ? null : JobCodeModel();
                model = fallback;
            }
            // The message goes first: --file takes every word after it as another file.
            string line = "opencode run " + Arg(message) + " --auto --format json --dir " + Arg(gig.Folder) + " --title " + Arg(gig.Title) + (string.IsNullOrWhiteSpace(model) ? "" : " --model " + Arg(model.Trim())) +
                (session != null ? " --session " + Arg(session) : "") + " --file " + Arg(briefPath);
            Note(gig, "OpenCode is coding in " + gig.Folder + (string.IsNullOrWhiteSpace(model) ? "" : " with " + (model.StartsWith("capri/", StringComparison.Ordinal) ? "her coding brains" : model.Trim())));
            string last = null;
            int code = Stream(gig, line, gig.Folder, 3600, extra, delegate(string output)
            {
                Dictionary<string, object> e;
                try { e = AppJson.Object(output.Trim()); } catch (FormatException) { if (output.Trim().Length > 0) Note(gig, "OpenCode: " + Trim(Hide(output.Trim()), 300)); return; }
                if (e == null) return;
                string sid = AppJson.Str(e, "sessionID");
                if (!string.IsNullOrEmpty(sid) && gig.Session != sid) { gig.Session = sid; Save(); }
                string kind = AppJson.Str(e, "type");
                Dictionary<string, object> part = AppJson.Obj(e, "part");
                if (kind == "text") { string said = AppJson.Str(part, "text"); if (!string.IsNullOrWhiteSpace(said)) { last = said.Trim(); Note(gig, "OpenCode: " + Trim(Hide(last), 400)); } }
                else if (kind == "tool_use")
                {
                    Dictionary<string, object> state = AppJson.Obj(part, "state");
                    Dictionary<string, object> input = AppJson.Obj(state, "input");
                    string what = AppJson.Str(input, "command") ?? AppJson.Str(input, "filePath") ?? AppJson.Str(input, "pattern") ?? AppJson.Str(input, "path") ?? AppJson.Str(input, "url") ?? "";
                    string status = AppJson.Str(state, "status") ?? "";
                    Note(gig, "OpenCode " + (AppJson.Str(part, "tool") ?? "tool") + " " + Trim(Hide(what), 200) + (status == "error" ? " (failed: " + Trim(Hide(AppJson.Str(state, "error") ?? ""), 200) + ")" : ""));
                }
                else if (kind == "error") Note(gig, "OpenCode error: " + Trim(Hide(AppJson.Write(e.ContainsKey("error") ? e["error"] : e)), 400));
            });
            if (Relay.LastUsed != null && model != null && model.StartsWith("capri/", StringComparison.Ordinal)) Note(gig, "Coding brain used: " + Relay.LastUsed);
            if (gig.Session != null) RememberSession(gig.Folder, gig.Session);
            Review(gig);
            gig.Result = (code == 0 ? "" : "OpenCode exited with code " + code + ".\n") + (last ?? "OpenCode didn't say what it did.") +
                (gig.Diff != null ? "\n\nChanged:\n" + Trim(DiffStat(gig), 1500) : "") + (gig.Checks != null ? "\n\nChecks:\n" + Trim(gig.Checks, 1200) : "");
            gig.Result = Hide(gig.Result);
        }

        private static string Arg(string text) { return "\"" + (text ?? "").Replace("\"", "'") + "\""; }

        private bool IsGit(string folder) { return Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git")); }

        // git itself, not through PowerShell, which turns git's chatter on stderr into errors. Null when it fails.
        private string Git(Gig gig, string args, int seconds = 60)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo("git", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = gig.Folder };
                start.StandardOutputEncoding = new UTF8Encoding(false);
                start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                using (Process git = new Process { StartInfo = start })
                {
                    StringBuilder output = new StringBuilder();
                    git.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                    git.ErrorDataReceived += delegate { };
                    git.Start();
                    git.BeginOutputReadLine();
                    git.BeginErrorReadLine();
                    if (!git.WaitForExit(seconds * 1000)) { Kill(git); return null; }
                    git.WaitForExit();
                    lock (output) return git.ExitCode == 0 ? output.ToString().Trim() : null;
                }
            }
            catch (Exception) { return null; }
        }

        // Its own branch, so nothing lands on theirs until they say so. A new project in her jobs folder becomes a repo first.
        private void Prepare(Gig gig)
        {
            if (!IsGit(gig.Folder) && gig.Folder.StartsWith(home, StringComparison.OrdinalIgnoreCase)) Git(gig, "init -q");
            if (!IsGit(gig.Folder)) { Note(gig, gig.Folder + " isn't a git repo, so the changes go straight in, with no branch to review them on."); return; }
            gig.Base = Git(gig, "rev-parse HEAD");
            string dirty = Git(gig, "status --porcelain");
            gig.WasClean = dirty != null && dirty.Length == 0;
            string current = Git(gig, "branch --show-current") ?? "";
            if (current.StartsWith("capri/", StringComparison.Ordinal)) { gig.Branch = current; Note(gig, "Staying on " + current + "."); return; }
            string slug = Regex.Replace(gig.Title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 30) slug = slug.Substring(0, 30).Trim('-');
            string branch = "capri/" + gig.Id + "-" + (slug.Length == 0 ? "job" : slug);
            if (Git(gig, "switch -c " + branch) != null || Git(gig, "checkout -b " + branch) != null) { gig.Branch = branch; Note(gig, "Working on a new branch, " + branch + ", from " + (current.Length == 0 ? "the start" : current) + (gig.WasClean ? "." : " (their uncommitted changes came along).")); }
            else Note(gig, "Couldn't make a branch, so it works on " + (current.Length == 0 ? "the current one" : current) + ".");
            Save();
        }

        // The real changes since the job started, and the project's own tests or build, for the reviewer.
        private void Review(Gig gig)
        {
            gig.Diff = null;
            gig.Checks = null;
            if (IsGit(gig.Folder))
            {
                Git(gig, "add -A --intent-to-add .");
                string against = gig.Base ?? "";
                string diff = Git(gig, "--no-pager diff " + against, 120);
                string status = Git(gig, "status --short");
                gig.Diff = (string.IsNullOrEmpty(status) ? "" : "Files:\n" + status + "\n\n") + (diff ?? "");
                if (gig.Diff.Trim().Length == 0) gig.Diff = "(nothing changed)";
            }
            string check = CheckCommand(gig.Folder);
            if (check == null) return;
            Note(gig, "Running the checks: " + check);
            int code;
            string output = Shell(gig, check, gig.Folder, 900, out code);
            gig.Checks = check + "\nexit " + code + "\n" + Tail(Hide(Regex.Replace(output, @"\x1B\[[0-9;?]*[A-Za-z]", "")), 3000);
            Note(gig, "Checks " + (code == 0 ? "passed" : "failed (exit " + code + ")") + ".");
        }

        private string DiffStat(Gig gig)
        {
            string stat = IsGit(gig.Folder) ? Git(gig, "--no-pager diff --stat " + (gig.Base ?? "")) : null;
            return string.IsNullOrEmpty(stat) ? Trim(gig.Diff ?? "", 600) : stat;
        }

        // The project's own test or build command, if it has an obvious one.
        internal static string CheckCommand(string folder)
        {
            try
            {
                string package = Path.Combine(folder, "package.json");
                if (File.Exists(package))
                {
                    string json = File.ReadAllText(package);
                    if (Regex.IsMatch(json, @"""test""\s*:\s*""(?!echo \\""Error: no test specified)")) return "npm test --silent";
                    if (Regex.IsMatch(json, @"""build""\s*:")) return "npm run build --silent";
                }
                if (File.Exists(Path.Combine(folder, "Cargo.toml"))) return "cargo test -q";
                if (File.Exists(Path.Combine(folder, "go.mod"))) return "go test ./...";
                if (File.Exists(Path.Combine(folder, "pytest.ini")) || File.Exists(Path.Combine(folder, "pyproject.toml")) && Directory.Exists(Path.Combine(folder, "tests")) || File.Exists(Path.Combine(folder, "setup.cfg")) && Directory.Exists(Path.Combine(folder, "tests"))) return "python -m pytest -q";
                if (Directory.GetFiles(folder, "*.sln").Length > 0 || Directory.GetFiles(folder, "*.csproj").Length > 0) return "dotnet build --nologo -v q";
                if (File.Exists(Path.Combine(folder, "Makefile")) && Regex.IsMatch(File.ReadAllText(Path.Combine(folder, "Makefile")), @"^test:", RegexOptions.Multiline)) return "make test";
            }
            catch (Exception) { }
            return null;
        }

        // On the branch, a passed job becomes one commit, unless they had uncommitted work there when it started.
        private void Wrap(Gig gig)
        {
            if (gig.Branch == null || !gig.WasClean || !IsGit(gig.Folder)) return;
            Git(gig, "add -A");
            string done = Git(gig, "-c user.name=Capri -c user.email=capri@localhost commit -q -m " + Arg("capri: " + gig.Title));
            string hash = Git(gig, "rev-parse --short HEAD");
            if (done != null && hash != null) Note(gig, "Committed " + hash + " on " + gig.Branch + ".");
            NoteProject(gig);
        }

        // The top of the repo, so the brief knows what it's working with.
        private static string Layout(string folder)
        {
            try
            {
                List<string> names = new List<string>();
                foreach (string dir in Directory.GetDirectories(folder)) { string n = Path.GetFileName(dir); if (!n.StartsWith(".") && n != "node_modules" && n != "bin" && n != "obj") names.Add(n + "/"); }
                foreach (string file in Directory.GetFiles(folder)) names.Add(Path.GetFileName(file));
                names.Sort(StringComparer.OrdinalIgnoreCase);
                if (names.Count > 60) names = names.GetRange(0, 60);
                string readme = null;
                foreach (string name in new[] { "README.md", "readme.md", "README.txt", "AGENTS.md", "CLAUDE.md" }) { string path = Path.Combine(folder, name); if (File.Exists(path)) { readme = name + ":\n" + Trim(File.ReadAllText(path), 2500); break; } }
                return (names.Count == 0 ? "(empty)" : string.Join("  ", names.ToArray())) + (readme == null ? "" : "\n\n" + readme);
            }
            catch (Exception) { return "(couldn't list it)"; }
        }

        // Her notes on each project, kept in her data folder, never in their repo.
        private string ProjectFile(string folder)
        {
            string name = Regex.Replace(Path.GetFileName(folder.TrimEnd('\\', '/')).ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            return Path.Combine(Path.Combine(data, "projects"), (name.Length == 0 ? "project" : name) + ".md");
        }

        private string ProjectNotes(string folder)
        {
            try { string path = ProjectFile(folder); return File.Exists(path) ? "\n\nCapri's notes on this project from earlier jobs:\n" + Tail(File.ReadAllText(path), 4000) : ""; }
            catch (Exception) { return ""; }
        }

        private void NoteProject(Gig gig)
        {
            try
            {
                string path = ProjectFile(gig.Folder);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, "\n## " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " · " + gig.Title + (gig.Branch != null ? " (" + gig.Branch + ")" : "") + "\n" + Trim(gig.Result ?? "", 900) + "\n", new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        // OpenCode's session per repo, so the next job there picks up with everything it knew.
        private string SessionsPath { get { return Path.Combine(data, "code-sessions.txt"); } }

        private string RecentSession(string folder)
        {
            try
            {
                if (!File.Exists(SessionsPath)) return null;
                foreach (string line in File.ReadAllLines(SessionsPath))
                {
                    string[] parts = line.Split('\t');
                    long ticks;
                    if (parts.Length == 3 && string.Equals(parts[0], folder, StringComparison.OrdinalIgnoreCase) && long.TryParse(parts[2], out ticks) && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromHours(12)) return parts[1];
                }
            }
            catch (Exception) { }
            return null;
        }

        private void RememberSession(string folder, string session)
        {
            try
            {
                List<string> kept = new List<string>();
                if (File.Exists(SessionsPath)) foreach (string line in File.ReadAllLines(SessionsPath)) if (!line.StartsWith(folder + "\t", StringComparison.OrdinalIgnoreCase)) kept.Add(line);
                kept.Add(folder + "\t" + session + "\t" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                if (kept.Count > 50) kept.RemoveRange(0, kept.Count - 50);
                File.WriteAllLines(SessionsPath, kept.ToArray());
            }
            catch (Exception) { }
        }

        // Runs a command through cmd with each output line handed over as it comes, for live progress; stops with the job.
        private int Stream(Gig gig, string line, string folder, int seconds, Dictionary<string, string> env, Action<string> each)
        {
            ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + line + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.RedirectStandardInput = true;
            start.StandardOutputEncoding = new UTF8Encoding(false);
            start.StandardErrorEncoding = new UTF8Encoding(false);
            start.WorkingDirectory = folder;
            foreach (KeyValuePair<string, string> key in SavedKeys()) start.EnvironmentVariables[key.Key] = key.Value;
            if (env != null) foreach (KeyValuePair<string, string> pair in env) start.EnvironmentVariables[pair.Key] = pair.Value;
            StringBuilder errors = new StringBuilder();
            using (Process process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) try { each(e.Data); } catch (Exception) { } };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (errors) { if (errors.Length < 20000) errors.AppendLine(e.Data); } };
                try { process.Start(); }
                catch (Exception error) { Note(gig, "Couldn't start it: " + error.Message); return -1; }
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                gig.Running = process;
                bool exited = false;
                Stopwatch took = Stopwatch.StartNew();
                while (!gig.Stop && took.Elapsed.TotalSeconds < seconds) { if (process.WaitForExit(500)) { exited = true; break; } }
                if (!exited) { Kill(process); Note(gig, gig.Stop ? "Stopped." : "Stopped after " + seconds / 60 + " minutes: it was taking too long."); }
                else process.WaitForExit();
                gig.Running = null;
                string said;
                lock (errors) said = errors.ToString().Trim();
                if (said.Length > 0 && (!exited || process.ExitCode != 0)) Note(gig, "It said: " + Trim(Hide(Regex.Replace(said, @"\x1B\[[0-9;?]*[A-Za-z]", "")), 800));
                return exited ? process.ExitCode : -1;
            }
        }

        // ---- the overseer ----------------------------------------------------------------------------------

        private const string OversightPrompt =
            "You are the overseer of Capri's crew. A worker just finished a job on the user's PC. Check the work against what they asked: did it really get done, and is anything wrong, missing or risky? Don't trust the worker's own word: look at the commands, outputs and results.\n" +
            "Reply with only JSON, no other text: {\"verdict\": \"pass|retry|fail\", \"notes\": \"what's wrong or missing, for the worker if it tries again\", \"report\": \"what Capri tells the user out loud\"}.\n" +
            "Judge it against what they meant, using the talk around the job: if the worker did something else, like finding a different app or reporting what they obviously already have, it didn't get done.\n" +
            "Use retry only when the same worker trying again would likely fix it. Use fail when it needs the user (a sign-in, a key, a decision) or can't be done.\n" +
            "The report is Capri, their partner, talking to them in her own cocky voice: one to three casual sentences saying what got done, anything they need to do, and where the results are. Plain words, no lists, no corporate phrases, and don't start with 'So,' or 'Alright,' every time.";

        private GigVerdict Oversee(Gig gig, Brain worker)
        {
            Brain judge = null;
            try { judge = Overseer == null ? null : Overseer(); } catch (Exception) { judge = null; }
            if (judge != null) Note(gig, "Overseer: " + MindName(judge));
            List<string> log = new List<string>(LogLines(gig));
            int from = Math.Max(0, log.Count - 30);
            string user = "They asked: " + Asked(gig) + "\nPlan: " + gig.Plan + "\nWorker: " + gig.Worker + "\nJob folder: " + gig.Folder +
                "\nWhat the worker did:\n" + string.Join("\n", log.GetRange(from, log.Count - from).ToArray()) + "\n\nThe worker's result:\n" + Trim(gig.Result ?? "(none)", 3500);
            if (gig.Worker == "builder")
                user += "\n\nThis is a coding job: judge the real changes and checks below, not the worker's summary. A diff that doesn't do the job, or checks that fail because of it, means retry, with the exact errors in your notes." +
                    "\n\nThe code changes (git diff):\n" + Trim(gig.Diff ?? "(no git diff: not a git repo, or nothing changed)", 9000) +
                    "\n\nChecks Capri ran herself:\n" + (gig.Checks ?? "(no test or build command found)");
            GigVerdict verdict = null;
            foreach (Brain brain in new[] { judge, worker })
            {
                if (brain == null || verdict != null) continue;
                try { verdict = ParseJson<GigVerdict>(brain.Consult(OversightPrompt, user, 500)); }
                catch (Exception error) { Note(gig, "Review glitched: " + Voice.Short(error.Message)); }
            }
            if (verdict == null) verdict = new GigVerdict { Verdict = Regex.IsMatch(gig.Result ?? "", @"^(ran out|couldn't|failed)", RegexOptions.IgnoreCase) ? "fail" : "pass", Report = Trim(gig.Result ?? "", 300) };
            verdict.Verdict = (verdict.Verdict ?? "pass").Trim().ToLowerInvariant();
            if (verdict.Verdict != "retry" && verdict.Verdict != "fail") verdict.Verdict = "pass";
            verdict.Report = Brain.Visible(verdict.Report ?? "").Trim();
            Note(gig, "Overseer: " + verdict.Verdict + (string.IsNullOrWhiteSpace(verdict.Notes) ? "" : " - " + verdict.Notes));
            return verdict;
        }

        // ---- running commands ------------------------------------------------------------------------------

        private string Shell(Gig gig, string command, string folder, int seconds, out int code)
        {
            Directory.CreateDirectory(folder);
            string script = "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $ErrorActionPreference='Continue'; Set-Location -LiteralPath " + Quote(folder) + "\n" + command + "\nif ($LASTEXITCODE) { exit $LASTEXITCODE }";
            ProcessStartInfo start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.RedirectStandardInput = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            start.WorkingDirectory = folder;
            foreach (KeyValuePair<string, string> key in SavedKeys()) start.EnvironmentVariables[key.Key] = key.Value;
            StringBuilder output = new StringBuilder();
            object sink = new object();
            using (Process process = new Process { StartInfo = start })
            {
                DataReceivedEventHandler take = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (sink) { if (output.Length < 200000) output.AppendLine(e.Data); }
                };
                process.OutputDataReceived += take;
                process.ErrorDataReceived += take;
                process.Start();
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                gig.Running = process;
                bool exited = false;
                Stopwatch took = Stopwatch.StartNew();
                while (!gig.Stop && took.Elapsed.TotalSeconds < seconds) { if (process.WaitForExit(500)) { exited = true; break; } }
                if (!exited) { Kill(process); lock (sink) output.AppendLine(gig.Stop ? "(stopped)" : "(stopped after " + seconds + " seconds: it was taking too long)"); }
                else process.WaitForExit();
                gig.Running = null;
                code = exited ? process.ExitCode : -1;
            }
            lock (sink) return Hide(output.ToString().TrimEnd());
        }

        private List<KeyValuePair<string, string>> SavedKeys()
        {
            List<KeyValuePair<string, string>> all = null;
            try { all = Keys == null ? null : Keys(); } catch (Exception) { all = null; }
            List<KeyValuePair<string, string>> kept = new List<KeyValuePair<string, string>>();
            if (all != null) foreach (KeyValuePair<string, string> key in all) if (!string.IsNullOrEmpty(key.Key) && !string.IsNullOrEmpty(key.Value) && Regex.IsMatch(key.Key, @"^[A-Za-z_][A-Za-z0-9_]*$")) kept.Add(key);
            return kept;
        }

        // Anything a command prints, a file holds or a report says never carries a saved key's value.
        internal string Hide(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (KeyValuePair<string, string> key in SavedKeys())
                if (key.Value.Length >= 8) text = text.Replace(key.Value, "[" + key.Key + " hidden]");
            return text;
        }

        // Where their apps are listed, so "add the Spotify app" can be a terminal job.
        private string AppsNote()
        {
            return "\nTheir connected apps (MCP servers) are listed in " + Apps.Path + ", in Claude Desktop's format: {\"mcpServers\": {\"name\": {\"command\": \"npx\", \"args\": [...], \"env\": {...}}}} or {\"url\": ..., \"headers\": {...}} for a link. " +
                "To add an app, read that file, add its entry and keep the others; for a token, use ${NAME} with the name of one of their saved keys instead of the value. Most apps need Node.js (winget install OpenJS.NodeJS.LTS) or uv (winget install astral-sh.uv).";
        }

        // What the worker is told about the vault: names only, never values.
        private string KeysNote()
        {
            List<KeyValuePair<string, string>> keys = SavedKeys();
            if (keys.Count == 0) return "";
            List<string> names = keys.ConvertAll(key => "$env:" + key.Key);
            return "\nTheir saved keys are already set as environment variables in every command: " + string.Join(", ", names.ToArray()) + ". " +
                "Use them by name, never print or echo their values, and when a key has to be saved somewhere, write it from the variable, like [Environment]::SetEnvironmentVariable('NAME', $env:NAME, 'User'), or in a config that reads the variable.";
        }

        private static void Kill(Process process)
        {
            try
            {
                if (process.HasExited) return;
                using (Process killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/T /F /PID " + process.Id) { UseShellExecute = false, CreateNoWindow = true })) killer.WaitForExit(5000);
            }
            catch (Exception) { try { process.Kill(); } catch (Exception) { } }
        }

        private static string Quote(string text) { return "'" + (text ?? "").Replace("'", "''").Replace("\u2019", "''") + "'"; }

        // ---- shared bits -----------------------------------------------------------------------------------

        // The job as they asked it, plus the talk around it: the ask can be as short as "download DeepSeek" when they'd just said where.
        private static string Asked(Gig gig)
        {
            return Voice.Quote(gig.Ask) + (string.IsNullOrEmpty(gig.Context) ? "" : "\nWhat you and they were just talking about, oldest first (use it to work out what they meant, like which download or where):\n" + gig.Context);
        }

        private static string Brief(Gig gig, string notes, List<string> steps, int full = 5, int room = 6500, int most = MaxSteps)
        {
            StringBuilder text = new StringBuilder("Job: " + Asked(gig) + "\nPlan: " + gig.Plan + "\nJob folder: " + gig.Folder);
            if (!string.IsNullOrEmpty(notes)) text.Append("\nThe overseer checked your last try and said: " + notes);
            if (steps.Count > 0)
            {
                text.Append("\n\nDone so far:");
                // Her brain's window is small: the latest steps in full, older ones cut short, the oldest dropped.
                List<string> kept = new List<string>();
                int used = 0;
                for (int i = steps.Count - 1; i >= 0; i--)
                {
                    string step = i >= steps.Count - full ? steps[i] : Trim(steps[i], 220);
                    if (used + step.Length > room && kept.Count > 0) { kept.Insert(0, "(" + (i + 1) + " earlier steps not shown)"); break; }
                    used += step.Length;
                    kept.Insert(0, (i + 1) + ". " + step);
                }
                foreach (string line in kept) text.Append("\n" + line);
            }
            text.Append(steps.Count >= most - 3 ? "\n\nYou're nearly out of steps: finish now with what you have." : "\n\nWhat's next? Call exactly one tool.");
            return text.ToString();
        }

        private static string Describe(string tool, ToolArguments args)
        {
            if (tool == "run_powershell") return "run_powershell: " + Trim(args.Text ?? "", 400);
            if (tool == "web_search") return "web_search: " + args.Search;
            if (tool == "open_page") return "open_page: " + args.Site;
            if (tool == "read_file") return "read_file: " + args.Name;
            if (tool == "write_file") return "write_file: " + args.Name;
            return tool;
        }

        private static readonly Regex StepText = new Regex(@"\b(run_powershell|read_file|write_file|web_search|open_page|finish)\s*(\{.*\})", RegexOptions.Singleline);

        private static bool Pick(List<KeyValuePair<string, ToolArguments>> calls, string written, out string tool, out ToolArguments args)
        {
            tool = null;
            args = null;
            if (calls.Count > 0) { tool = calls[0].Key; args = calls[0].Value ?? new ToolArguments(); return true; }
            Match found = StepText.Match(Brain.Visible(written ?? ""));
            if (!found.Success) return false;
            string json = found.Groups[2].Value;
            int depth = 0, end = -1;
            for (int i = 0; i < json.Length; i++) { if (json[i] == '{') depth++; else if (json[i] == '}' && --depth == 0) { end = i; break; } }
            string raw = end > 0 ? json.Substring(0, end + 1) : json;
            args = Hands.Read(raw);
            if (args == null && found.Groups[1].Value.Contains("__")) args = new ToolArguments();
            if (args == null) return false;
            args.Raw = raw;
            tool = found.Groups[1].Value;
            return true;
        }

        private static string Render(string[,] specs, bool claude)
        {
            List<string> tools = new List<string>();
            for (int i = 0; i < specs.GetLength(0); i++)
                tools.Add(claude
                    ? "{\"name\":\"" + specs[i, 0] + "\",\"description\":" + Voice.Quote(specs[i, 1]) + ",\"input_schema\":" + specs[i, 2] + "}"
                    : "{\"type\":\"function\",\"function\":{\"name\":\"" + specs[i, 0] + "\",\"description\":" + Voice.Quote(specs[i, 1]) + ",\"parameters\":" + specs[i, 2] + "}}");
            return "[" + string.Join(",", tools.ToArray()) + "]";
        }

        private static string Listing(string[,] specs)
        {
            StringBuilder text = new StringBuilder("\nYour tools:");
            for (int i = 0; i < specs.GetLength(0); i++)
            {
                List<string> keys = new List<string>();
                foreach (Match key in Regex.Matches(specs[i, 2], @"""(\w+)"":\{""type"":""string"",""description"":""[^""]*""(?:,""enum"":\[([^\]]*)\])?"))
                    keys.Add("\"" + key.Groups[1].Value + "\": \"" + (key.Groups[2].Success ? key.Groups[2].Value.Replace("\"", "").Replace(",", "|") : "...") + "\"");
                text.Append("\n- " + specs[i, 0] + "{" + string.Join(", ", keys.ToArray()) + "}: " + specs[i, 1]);
            }
            return text.Append("\nReply with only one call, like: run_powershell{\"text\": \"winget --version\"}").ToString();
        }

        internal static T ParseJson<T>(string reply) where T : class { return Json.Parse<T>(reply); }

        internal static string Pick(string kind, string ask)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            if (k == "terminal" || k == "research" || k == "kaggle") return k;
            if (k == "builder") return HasOpenCode ? "builder" : "terminal";
            // Anything on Kaggle, setting up its command included, goes to the worker that knows Kaggle.
            if (Regex.IsMatch(ask ?? "", @"\bkaggle\b", RegexOptions.IgnoreCase)) return "kaggle";
            if (Regex.IsMatch(ask ?? "", @"\b(research|look into|look up|find out|read up|dig into|learn about|what('s| is| are)|who (is|are|was)|compare|summari[sz]e|news (on|about)|explain)\b", RegexOptions.IgnoreCase)) return "research";
            if (HasOpenCode && Regex.IsMatch(ask ?? "", @"\b(build|code|program|refactor|debug|make (me )?(a|an) (app|site|website|web ?app|script|tool|bot|game|extension|plugin|api)|write (me )?(a|an|the) (script|program|app|function|class)|fix (the|my|this) (code|bug|app|site|script|build))\b", RegexOptions.IgnoreCase)) return "builder";
            return "terminal";
        }

        internal static string WorkerName(string worker) { return worker == "research" ? "research" : worker == "builder" ? "coding" : worker == "kaggle" ? "Kaggle" : worker == "apps" ? "apps" : "terminal"; }

        // An apps job: asked for by kind, or about one of their connected apps, or about clicking around a website when the browser is connected.
        private bool WantsApps(string kind, string ask)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            List<string> apps = Apps.Names;
            if (apps.Count == 0) return false;
            if (k == "apps") return true;
            if (k.Length > 0 && k != "auto") return false;
            foreach (string app in apps) if (app.Length >= 3 && Regex.IsMatch(ask ?? "", @"\b" + Regex.Escape(app) + @"\b", RegexOptions.IgnoreCase)) return true;
            return apps.Contains("browser") && Regex.IsMatch(ask ?? "", @"\b(in (the|my|a) browser|on (the|that) (site|website|page)|fill (in|out)|sign (in|up) (to|on|for)|log ?in to|click (on )?|book (a|an|me)|order (a|an|me)|buy (a|an|me)|apply (for|to))\b", RegexOptions.IgnoreCase);
        }

        // ${NAME} in apps.json is a saved key from the vault, or else an environment variable.
        private string Fill(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("${", StringComparison.Ordinal) < 0) return text;
            List<KeyValuePair<string, string>> keys = SavedKeys();
            return Regex.Replace(text, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", delegate(Match m)
            {
                foreach (KeyValuePair<string, string> key in keys) if (string.Equals(key.Key, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) return key.Value;
                return Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "";
            });
        }

        internal void Shutdown() { try { Apps.StopAll(); } catch (Exception) { } try { Relay.Stop(); } catch (Exception) { } }

        // ---- the apps worker -------------------------------------------------------------------------------

        private const string AppsPrompt =
            "You are the apps worker in Capri's crew. You get one job done for the user inside their connected apps, through each app's own tools (every tool's name starts with its app), plus PowerShell on their Windows PC when you need it, one tool call per reply.\n" +
            "Read each tool's result before the next step, and use what it shows you: in the browser, look at the page snapshot to find what to click or type into. Don't guess at buttons or links you haven't seen.\n" +
            "Never buy, pay, send money, delete something or post publicly unless the job clearly says to. If a site or app needs them, like signing in, a code sent to their phone, a captcha or a payment, stop and call finish with action failed and say exactly what they need to do.\n" +
            "Check your work before you finish. Then call finish with action done and what you did, what you found and what they need to know, in plain words.";

        private void AppsWork(Gig gig, Brain brain, string notes)
        {
            List<AppTool> tools = Apps.Ready(delegate(string line) { Note(gig, line); });
            if (tools.Count == 0) { gig.Result = "None of their apps started, so this couldn't be done. See the log on the jobs board."; return; }
            List<string> named = new List<string>();
            foreach (AppTool t in tools) if (!named.Contains(t.App)) named.Add(t.App);
            Note(gig, "Apps ready: " + tools.Count + " tools from " + string.Join(", ", named.ToArray()));
            int extra = TerminalTools.GetLength(0) - 1;
            string[,] specs = new string[tools.Count + extra + 1, 3];
            for (int i = 0; i < extra; i++) for (int j = 0; j < 3; j++) specs[i, j] = TerminalTools[i, j];
            Dictionary<string, AppTool> byName = new Dictionary<string, AppTool>();
            for (int i = 0; i < tools.Count; i++)
            {
                specs[extra + i, 0] = tools[i].Exposed;
                specs[extra + i, 1] = "[" + tools[i].App + "] " + tools[i].Description;
                specs[extra + i, 2] = tools[i].Schema;
                byName[tools[i].Exposed] = tools[i];
            }
            for (int j = 0; j < 3; j++) specs[tools.Count + extra, j] = TerminalTools[extra, j];
            string prompt = AppsPrompt + KeysNote();
            string openAi = Render(specs, false), claude = Render(specs, true), listing = Listing(specs);
            List<string> steps = new List<string>();
            int most = StepsFor(brain);
            bool cloud = brain.Remote != null;
            for (int step = 0; step < most && !gig.Stop; step++)
            {
                List<KeyValuePair<string, ToolArguments>> calls = new List<KeyValuePair<string, ToolArguments>>();
                string written = brain.Step(prompt, Brief(gig, notes, steps, cloud ? 4 : 3, cloud ? 24000 : 6500, most), openAi, claude, listing, calls, 900);
                string tool;
                ToolArguments args;
                if (!Pick(calls, written, out tool, out args)) { steps.Add("(You replied without a tool call. Call exactly one tool.)"); continue; }
                if (tool == "finish") { gig.Result = Clean(args.Text, "Done."); if (args.Action == "failed") Note(gig, "Worker says it can't finish: " + gig.Result); return; }
                string outcome;
                AppTool app;
                if (byName.TryGetValue(tool, out app))
                {
                    Note(gig, app.App + " > " + app.Name + " " + Trim(Hide(args.Raw ?? "{}"), 300));
                    outcome = Hide(Apps.Call(app, args.Raw, 180000));
                    Note(gig, Trim(outcome, 400));
                }
                else outcome = Hide(Act(gig, tool, args));
                steps.Add(tool + " " + Trim(args.Raw ?? Describe(tool, args), 400) + "\n" + Trim(outcome, cloud ? 9000 : 1500));
            }
            if (!gig.Stop && gig.Result == null) gig.Result = "Ran out of steps before finishing.";
        }

        // ---- Kaggle ------------------------------------------------------------------------------------------

        // The kaggle command reads its key from these files, so a key saved here works for her workers and for them in any terminal.
        internal static string KaggleHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kaggle");

        internal static bool KaggleReady
        {
            get
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KAGGLE_API_TOKEN")) || (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KAGGLE_USERNAME")) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KAGGLE_KEY")))) return true;
                try { return File.Exists(Path.Combine(KaggleHome, "access_token")) || File.Exists(Path.Combine(KaggleHome, "kaggle.json")); }
                catch (Exception) { return false; }
            }
        }

        // A new-style API token goes in access_token; a pasted kaggle.json (the legacy key) goes in kaggle.json. Blank does nothing.
        internal static string SaveKaggle(string pasted)
        {
            string text = (pasted ?? "").Trim();
            if (text.Length == 0) return KaggleReady ? "Kaggle key saved." : "Paste a Kaggle API token first.";
            bool legacy = text.StartsWith("{", StringComparison.Ordinal);
            if (legacy ? !Regex.IsMatch(text, @"""username""\s*:\s*""[^""]+"".*""key""\s*:\s*""[0-9a-fA-F]{20,}""|""key""\s*:\s*""[0-9a-fA-F]{20,}"".*""username""\s*:\s*""[^""]+""", RegexOptions.Singleline) : !Regex.IsMatch(text, @"\A[A-Za-z0-9_\-\.]{20,400}\z"))
                return "That doesn't look like a Kaggle API token. Copy it from kaggle.com/settings/api.";
            try
            {
                Directory.CreateDirectory(KaggleHome);
                File.WriteAllText(Path.Combine(KaggleHome, legacy ? "kaggle.json" : "access_token"), text, new UTF8Encoding(false));
            }
            catch (Exception error) { return "Couldn't save it: " + error.Message; }
            return "Kaggle key saved. Her Kaggle worker can use it now.";
        }

        private static string KaggleState()
        {
            return KaggleReady ? "\nTheir Kaggle key is saved, so kaggle commands are signed in." : "\nTheir Kaggle key isn't saved yet. Try the kaggle command once; if it says it isn't authenticated, finish with action failed and tell them: open her Jobs board, paste a token from kaggle.com/settings/api (Generate New Token) into the Kaggle box, then ask again. Never ask for the key in chat.";
        }

        private const string KagglePrompt =
            "\nYou're her Kaggle worker: you run their Kaggle notebooks on Kaggle's free GPUs from this PC, with the kaggle command.\n" +
            "If kaggle isn't installed, install it with py -m pip install --user --upgrade kaggle (or python -m pip if py is missing). If the kaggle command still isn't found, run it as py -m kaggle.\n" +
            "kaggle kernels list --mine shows their notebooks. kaggle kernels pull <user>/<slug> -p <folder> -m downloads one with its kernel-metadata.json, so you can edit it and push it back.\n" +
            "To run code on Kaggle, put the .ipynb or .py in a folder of the job folder with a kernel-metadata.json: {\"id\": \"<user>/<slug>\", \"title\": \"<title, its slug must match>\", \"code_file\": \"<file>\", \"language\": \"python\", \"kernel_type\": \"script or notebook\", \"is_private\": \"true\", \"enable_gpu\": \"true\", \"enable_internet\": \"true\", \"machine_shape\": \"NvidiaTeslaT4\"} (NvidiaTeslaT4 is GPU T4 x2). Then kaggle kernels push -p <folder> --accelerator NvidiaTeslaT4 saves it and starts a full run in the cloud, up to 12 hours on a GPU.\n" +
            "kaggle kernels status <user>/<slug> says if it's queued, running, complete or errored, and kaggle kernels output <user>/<slug> -p <folder> downloads its log and files. Check status a few times with Start-Sleep between, but don't wait more than a few minutes in total: finish with it running and say how far it got and how to watch it (its page is https://www.kaggle.com/code/<user>/<slug>).\n" +
            "Their username is the first part of their notebook ids, or in the output of kaggle config view.\n" +
            "Big downloads like model weights happen inside the notebook on Kaggle (huggingface_hub or wget, into /kaggle/working or /kaggle/tmp), never on this PC: their laptop has little disk. Kaggle disk and GPU memory are limited, so check a model fits before you pick it.\n" +
            "If you change one of their existing notebooks, push it as a new version of the same id; never delete their notebooks or datasets.";

        private static string MindName(Brain brain)
        {
            Cloud remote = brain.Remote;
            if (remote != null) return remote.Model + (remote.Kind == "custom" ? " on " + remote.Company : " (" + remote.Name + ")");
            return (brain.Model ?? "her brain") + " on this PC";
        }

        private string FolderFor(Gig gig, string target)
        {
            string wanted = (target ?? "").Trim().Trim('"');
            try { if (wanted.Length > 0 && Path.IsPathRooted(wanted) && Directory.Exists(wanted)) return wanted; } catch (ArgumentException) { }
            string found = wanted.Length > 1 && !Path.IsPathRooted(wanted) ? FindProject(wanted) : null;
            if (found != null) return found;
            string slug = Regex.Replace(gig.Title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 40) slug = slug.Substring(0, 40).Trim('-');
            return Path.Combine(home, gig.Id + "-" + (slug.Length == 0 ? "job" : slug));
        }

        // A project they name ("my capri repo", "the harness") in the usual places on their PC, repos first.
        internal static string FindProject(string name)
        {
            string want = Regex.Replace(Regex.Replace(name.ToLowerInvariant(), @"\b(my|the|repo|repository|project|folder|code|codebase)\b", " "), @"[^a-z0-9]+", "");
            if (want.Length < 2) return null;
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            List<string> roots = new List<string> { user, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
            foreach (string sub in new[] { "source\\repos", "Documents\\GitHub", "GitHub", "code", "Code", "projects", "Projects", "dev", "repos", "src", "ai-lab", "agent_work" }) roots.Add(Path.Combine(user, sub));
            string best = null;
            int bestScore = 0;
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); } catch (Exception) { continue; }
                foreach (string dir in dirs)
                {
                    string plain = Regex.Replace(Path.GetFileName(dir).ToLowerInvariant(), @"[^a-z0-9]+", "");
                    int score = plain == want ? 3 : plain.Contains(want) || (want.Contains(plain) && plain.Length >= 4) ? 2 : 0;
                    if (score == 0) continue;
                    if (Directory.Exists(Path.Combine(dir, ".git"))) score += 2;
                    if (score > bestScore) { best = dir; bestScore = score; }
                }
            }
            return best;
        }

        private static string Resolve(Gig gig, string path)
        {
            string p = Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));
            if (p.StartsWith("~", StringComparison.Ordinal)) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p.Substring(1);
            return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(gig.Folder, p));
        }

        private static string Shorten(string ask)
        {
            string title = Regex.Replace(ask ?? "", @"^(can you|could you|please|pls|hey capri|capri|go|i need you to|i want you to)\s+", "", RegexOptions.IgnoreCase).Trim(' ', '.', '!', '?');
            if (title.Length > 48) { title = title.Substring(0, 48); int cut = title.LastIndexOf(' '); if (cut > 20) title = title.Substring(0, cut); title += "..."; }
            return title.Length == 0 ? "a job" : char.ToUpperInvariant(title[0]) + title.Substring(1);
        }

        private static string Clean(string text, string fallback) { string t = Brain.Visible(text ?? "").Trim(); return t.Length == 0 ? fallback : t; }

        internal static string Trim(string text, int max)
        {
            text = text ?? "";
            return text.Length <= max ? text : text.Substring(0, max) + "\n...(" + (text.Length - max) + " more characters)";
        }

        private static string Tail(string text, int max)
        {
            text = text ?? "";
            return text.Length <= max ? text : "..." + text.Substring(text.Length - max);
        }

        private static string Now() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }

        private static string Age(Gig gig)
        {
            DateTime started;
            if (!DateTime.TryParseExact(gig.Started ?? "", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out started)) return "just now";
            TimeSpan age = DateTime.Now - started;
            return age.TotalMinutes < 1 ? "started just now" : age.TotalHours < 1 ? "started " + (int)age.TotalMinutes + " min ago" : age.TotalDays < 1 ? "started " + (int)age.TotalHours + " h ago" : "started " + started.ToString("MMM d", CultureInfo.InvariantCulture);
        }

        private void Set(Gig gig, string status) { gig.Status = status; Save(); Notify(); }

        internal string[] LogLines(Gig gig) { lock (gate) return gig.Log == null ? new string[0] : gig.Log.ToArray(); }

        private void Note(Gig gig, string line)
        {
            line = Hide(line ?? "");
            lock (gate)
            {
                if (gig.Log == null) gig.Log = new List<string>();
                gig.Log.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + (line.Length > 2000 ? line.Substring(0, 2000) + "..." : line));
                while (gig.Log.Count > 300) gig.Log.RemoveAt(0);
            }
            Notify();
        }

        private void Say(string text)
        {
            Action<string> speak = Speak;
            if (speak != null && !string.IsNullOrWhiteSpace(text)) speak(text.Trim());
        }

        private void Notify()
        {
            Action changed = Changed;
            if (changed != null) changed();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(bookPath)) return;
                using (FileStream file = File.OpenRead(bookPath))
                {
                    GigBook book = (GigBook)new DataContractJsonSerializer(typeof(GigBook)).ReadObject(file);
                    if (book == null || book.Gigs == null) return;
                    foreach (Gig gig in book.Gigs)
                    {
                        if (gig == null || gig.Id == null) continue;
                        if (!gig.Finished) { gig.Status = "stopped"; if (gig.Log == null) gig.Log = new List<string>(); gig.Log.Add("Capri closed while this was running."); }
                        gigs.Add(gig);
                        int id;
                        if (int.TryParse(gig.Id, out id) && id >= next) next = id + 1;
                    }
                }
            }
            catch (Exception) { }
        }

        internal void Save()
        {
            try
            {
                GigBook book;
                lock (gate) book = new GigBook { Gigs = new List<Gig>(gigs) };
                Directory.CreateDirectory(Path.GetDirectoryName(bookPath));
                string temporary = bookPath + ".tmp";
                lock (gate) using (FileStream file = File.Create(temporary)) new DataContractJsonSerializer(typeof(GigBook)).WriteObject(file, book);
                if (File.Exists(bookPath)) File.Replace(temporary, bookPath, null); else File.Move(temporary, bookPath);
            }
            catch (Exception) { }
        }

        internal void Clear()
        {
            lock (gate) gigs.RemoveAll(g => g.Finished);
            Save();
            Notify();
        }
    }

    // Her crew's board: every job, what it's doing, and the overseer's say.
    internal sealed class CrewWindow : Form
    {
        private readonly Crew crew;
        private readonly ListBox list = new ListBox();
        private readonly TextBox details = new TextBox();
        private readonly Button stop = new Button(), open = new Button(), clear = new Button(), apps = new Button(), saveKey = new Button(), forgetKey = new Button(), saveModel = new Button(), saveKaggle = new Button();
        private readonly TextBox keyBox = new TextBox(), model = new TextBox(), kaggle = new TextBox();
        private readonly ComboBox saved = new ComboBox();
        private readonly Label keysLabel = new Label(), savedLabel = new Label(), modelLabel = new Label(), kaggleLabel = new Label(), status = new Label();
        // The vault: save what they pasted (and say how it went), forget one by name, and list what's saved.
        internal Func<string, string> SaveKey;
        internal Action<string> ForgetKey;
        internal Func<List<string>> KeyNames;
        internal Action<string> SaveModel;
        private string keySaid;
        private string shownId;
        private static readonly Color Back = Color.FromArgb(18, 19, 24), Panel = Color.FromArgb(28, 30, 38), Ink = Color.FromArgb(232, 234, 240), Dim = Color.FromArgb(140, 146, 162), Cyan = Color.FromArgb(102, 222, 242);

        internal CrewWindow(Crew crew, Icon icon)
        {
            this.crew = crew;
            Text = "Capri \u00B7 Jobs";
            if (icon != null) Icon = icon;
            BackColor = Back;
            ForeColor = Ink;
            Font = new Font("Consolas", 9.5f);
            ClientSize = new Size(820, 680);
            MinimumSize = new Size(640, 540);
            StartPosition = FormStartPosition.CenterScreen;

            list.BackColor = Panel; list.ForeColor = Ink; list.BorderStyle = BorderStyle.None; list.IntegralHeight = false;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 22;
            list.DrawItem += DrawGig;
            list.SelectedIndexChanged += delegate { Display(Selected); };
            details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical; details.WordWrap = true;
            details.BackColor = Panel; details.ForeColor = Ink; details.BorderStyle = BorderStyle.None;
            foreach (Button b in new[] { stop, open, clear, apps, saveKey, forgetKey, saveModel, saveKaggle })
            {
                b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Color.FromArgb(60, 64, 78); b.BackColor = Panel; b.ForeColor = Ink;
            }
            stop.Text = "Stop job"; open.Text = "Open folder"; clear.Text = "Clear finished"; saveKey.Text = "Save"; forgetKey.Text = "Remove"; saveModel.Text = "Save"; saveKaggle.Text = "Save";
            stop.Click += delegate { Gig g = Selected; if (g != null && !g.Finished) crew.Stop(g); };
            open.Click += delegate { Gig g = Selected; string folder = g != null ? g.Folder : crew.Home; try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\""); } catch (Exception) { } };
            clear.Click += delegate { crew.Clear(); };
            apps.Text = "Apps \u25BE";
            apps.Click += delegate
            {
                ContextMenuStrip menu = new ContextMenuStrip();
                List<string> have = crew.Apps.Names;
                for (int i = 0; i < Apps.Presets.GetLength(0); i++)
                {
                    string name = Apps.Presets[i, 0], about = Apps.Presets[i, 1];
                    ToolStripMenuItem item = new ToolStripMenuItem((have.Contains(name) ? "\u2713 " : "Add ") + name + "  \u2014  " + about) { Enabled = !have.Contains(name) };
                    item.Click += delegate { keySaid = crew.Apps.Add(name); Reload(); };
                    menu.Items.Add(item);
                }
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem edit = new ToolStripMenuItem("Edit apps.json (paste any MCP server's config here)");
                edit.Click += delegate { crew.Apps.Open(); };
                menu.Items.Add(edit);
                menu.Show(apps, new Point(0, apps.Height));
            };
            keysLabel.ForeColor = Dim; keysLabel.AutoSize = false;
            keyBox.BackColor = Panel; keyBox.ForeColor = Ink; keyBox.BorderStyle = BorderStyle.FixedSingle; keyBox.UseSystemPasswordChar = true;
            keyBox.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; saveKey.PerformClick(); } };
            saveKey.Click += delegate
            {
                Func<string, string> save = SaveKey;
                string text = keyBox.Text.Trim();
                keyBox.Text = "";
                if (save == null || text.Length == 0) return;
                saveKey.Enabled = false;
                keySaid = "Checking that key...";
                Reload();
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    string said;
                    try { said = save(text); } catch (Exception error) { said = "Couldn't save that: " + error.Message; }
                    try { BeginInvoke((Action)delegate { keySaid = said; saveKey.Enabled = true; Reload(); }); } catch (InvalidOperationException) { }
                });
            };
            savedLabel.Text = "Saved:"; savedLabel.ForeColor = Dim; savedLabel.AutoSize = false;
            saved.DropDownStyle = ComboBoxStyle.DropDownList; saved.BackColor = Panel; saved.ForeColor = Ink; saved.FlatStyle = FlatStyle.Flat;
            forgetKey.Click += delegate { Action<string> forget = ForgetKey; string name = saved.SelectedItem as string; if (forget != null && name != null) { forget(name); keySaid = "Removed " + name + "."; Reload(); } };
            modelLabel.Text = "OpenCode model (optional, like cerebras/gpt-oss-120b). Blank = her job brain.";
            modelLabel.ForeColor = Dim; modelLabel.AutoSize = false;
            model.BackColor = Panel; model.ForeColor = Ink; model.BorderStyle = BorderStyle.FixedSingle;
            saveModel.Click += delegate { Action<string> save = SaveModel; if (save != null) save(model.Text.Trim()); };
            kaggleLabel.ForeColor = Dim; kaggleLabel.AutoSize = false;
            kaggle.BackColor = Panel; kaggle.ForeColor = Ink; kaggle.BorderStyle = BorderStyle.FixedSingle; kaggle.UseSystemPasswordChar = true;
            saveKaggle.Click += delegate { string said = Crew.SaveKaggle(kaggle.Text); kaggle.Text = ""; Reload(); kaggleLabel.Text = said; };
            status.ForeColor = Dim; status.AutoSize = false;
            Controls.AddRange(new Control[] { list, details, stop, open, clear, apps, keysLabel, keyBox, saveKey, savedLabel, saved, forgetKey, modelLabel, model, saveModel, kaggleLabel, kaggle, saveKaggle, status });
            Resize += delegate { Arrange(); };
            Arrange();
            Reload();
        }

        internal string ModelText { set { model.Text = value ?? ""; } }

        private void Arrange()
        {
            int w = ClientSize.Width, h = ClientSize.Height, pad = 12, bottom = 244;
            int listW = Math.Max(240, w * 2 / 5);
            list.Bounds = new Rectangle(pad, pad, listW, h - bottom - pad * 2 - 34);
            details.Bounds = new Rectangle(pad * 2 + listW, pad, w - listW - pad * 3, h - bottom - pad * 2 - 34);
            int by = h - bottom - pad - 28;
            stop.Bounds = new Rectangle(pad, by, 100, 28);
            open.Bounds = new Rectangle(pad + 108, by, 110, 28);
            clear.Bounds = new Rectangle(pad + 226, by, 120, 28);
            apps.Bounds = new Rectangle(pad + 354, by, 80, 28);
            status.Bounds = new Rectangle(pad + 444, by + 6, w - pad * 2 - 444, 20);
            keysLabel.Bounds = new Rectangle(pad, h - bottom + 2, w - pad * 2, 48);
            keyBox.Bounds = new Rectangle(pad, h - bottom + 52, w - pad * 2 - 80, 24);
            saveKey.Bounds = new Rectangle(w - pad - 72, h - bottom + 50, 72, 28);
            savedLabel.Bounds = new Rectangle(pad, h - bottom + 88, 52, 20);
            saved.Bounds = new Rectangle(pad + 56, h - bottom + 84, w - pad * 2 - 56 - 88, 24);
            forgetKey.Bounds = new Rectangle(w - pad - 80, h - bottom + 82, 80, 28);
            modelLabel.Bounds = new Rectangle(pad, h - bottom + 120, w - pad * 2, 18);
            model.Bounds = new Rectangle(pad, h - bottom + 142, w - pad * 2 - 80, 24);
            saveModel.Bounds = new Rectangle(w - pad - 72, h - bottom + 140, 72, 28);
            kaggleLabel.Bounds = new Rectangle(pad, h - bottom + 178, w - pad * 2, 18);
            kaggle.Bounds = new Rectangle(pad, h - bottom + 200, w - pad * 2 - 80, 24);
            saveKaggle.Bounds = new Rectangle(w - pad - 72, h - bottom + 198, 72, 28);
        }

        private Gig Selected { get { return list.SelectedItem as Gig; } }

        internal void Reload()
        {
            if (IsDisposed) return;
            List<Gig> all = crew.All;
            all.Reverse();
            Gig keep = Selected;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Gig g in all) list.Items.Add(g);
            list.EndUpdate();
            Gig pick = null;
            foreach (Gig g in all) if (keep != null && g.Id == keep.Id) pick = g;
            if (pick == null && all.Count > 0) pick = all[0];
            if (pick != null) list.SelectedItem = pick;
            Display(pick);
            Func<List<string>> names = KeyNames;
            List<string> keys = names == null ? new List<string>() : names();
            string pickedKey = saved.SelectedItem as string;
            saved.BeginUpdate();
            saved.Items.Clear();
            foreach (string name in keys) saved.Items.Add(name);
            saved.EndUpdate();
            if (saved.Items.Count > 0) saved.SelectedItem = pickedKey != null && keys.Contains(pickedKey) ? pickedKey : saved.Items[0];
            forgetKey.Enabled = saved.Items.Count > 0;
            bool brains = keys.Exists(name => name.EndsWith(")", StringComparison.Ordinal) && name.Contains(" brain ("));
            keysLabel.Text = (keySaid != null ? keySaid + "\r\n" : "") +
                (brains ? "Keys for her jobs. The first brain does the work, the next one checks it, and the rest are backups. " : "Keys for her jobs. Paste a free brain key and her jobs run on it: Cerebras (cloud.cerebras.ai, fastest) or Gemini (aistudio.google.com). ") +
                "Any other key: put its name first, like UPLINK_API_KEY sk-... Workers use keys without ever seeing them.";
            kaggleLabel.Text = Crew.KaggleReady ? "Kaggle: key saved, so her Kaggle worker is signed in. Paste a new token here to replace it." : "Kaggle API token: get one at kaggle.com/settings/api (Generate New Token) and paste it here, so her Kaggle worker can run your notebooks.";
            int running = all.FindAll(g => !g.Finished).Count;
            List<string> connected = crew.Apps.Names;
            status.Text = (running == 0 ? "Nothing running." : running + " running.") + " Apps: " + (connected.Count == 0 ? "none" : string.Join(", ", connected.ToArray())) + "." + (Crew.HasOpenCode ? "" : " OpenCode: not found.");
        }

        private void Display(Gig g)
        {
            if (g == null) { details.Text = "No jobs yet. Ask her for something bigger, like \"set up the Kaggle CLI\" or \"research the best local voice models\"."; shownId = null; return; }
            StringBuilder text = new StringBuilder();
            text.Append("JOB " + g.Id + "  \u00B7  " + g.Status.ToUpperInvariant() + "\r\n" + g.Title + "\r\n\r\n");
            text.Append("You asked: " + g.Ask + "\r\n");
            text.Append("Worker: " + Crew.WorkerName(g.Worker) + "    Brain: " + (g.Mind ?? "-") + "\r\n");
            text.Append("Folder: " + g.Folder + "\r\n");
            text.Append("Started: " + g.Started + (g.Ended != null ? "    Ended: " + g.Ended : "") + "\r\n\r\n");
            if (!string.IsNullOrEmpty(g.Plan)) text.Append("PLAN\r\n" + g.Plan + "\r\n\r\n");
            if (!string.IsNullOrEmpty(g.Report)) text.Append("HER REPORT\r\n" + g.Report + "\r\n\r\n");
            if (!string.IsNullOrEmpty(g.Review)) text.Append("OVERSEER\r\n" + g.Review + "\r\n\r\n");
            if (!string.IsNullOrEmpty(g.Result)) text.Append("RESULT\r\n" + g.Result.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n\r\n");
            text.Append("LOG\r\n");
            foreach (string line in crew.LogLines(g)) text.Append(line.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n");
            string s = text.ToString();
            bool same = shownId == g.Id;
            if (details.Text != s)
            {
                bool atEnd = !same || details.SelectionStart >= details.TextLength - 2;
                details.Text = s;
                if (atEnd && !g.Finished) { details.SelectionStart = details.TextLength; details.ScrollToCaret(); }
            }
            shownId = g.Id;
            stop.Enabled = !g.Finished;
        }

        private void DrawGig(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0) return;
            Gig g = (Gig)list.Items[e.Index];
            bool picked = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush back = new SolidBrush(picked ? Color.FromArgb(40, 44, 58) : Panel)) e.Graphics.FillRectangle(back, e.Bounds);
            Color dot = g.Status == "done" ? Color.FromArgb(40, 200, 64) : g.Status == "failed" ? Color.FromArgb(255, 95, 87) : g.Status == "stopped" ? Dim : Cyan;
            using (SolidBrush b = new SolidBrush(dot)) e.Graphics.FillEllipse(b, e.Bounds.Left + 6, e.Bounds.Top + 7, 8, 8);
            TextRenderer.DrawText(e.Graphics, "#" + g.Id + "  " + g.Title, list.Font, new Point(e.Bounds.Left + 20, e.Bounds.Top + 3), Ink);
            TextRenderer.DrawText(e.Graphics, g.Status, list.Font, new Rectangle(e.Bounds.Right - 90, e.Bounds.Top + 3, 86, 18), Dim, TextFormatFlags.Right);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }
    }
}

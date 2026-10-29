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

        internal const int MaxSteps = 16;
        private readonly string bookPath;
        private readonly string home;
        private readonly List<Gig> gigs = new List<Gig>();
        private readonly object gate = new object();
        private int next = 1;
        private static bool? openCode;

        internal Crew(string data)
        {
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
                    else if (gig.Worker == "builder") Build(gig, notes);
                    else if (gig.Worker == "kaggle") Terminal(gig, brain, notes, KagglePrompt + KaggleState());
                    else Terminal(gig, brain, notes);
                    if (gig.Stop) { Finish(gig, "stopped", "Stopped " + gig.Title + ". " + (gig.Result ?? "")); return; }
                    Set(gig, "reviewing");
                    GigVerdict verdict = Oversee(gig, brain);
                    gig.Review = verdict.Notes;
                    if (verdict.Verdict == "retry" && attempt < 2) { notes = verdict.Notes; Note(gig, "Overseer sent it back: " + verdict.Notes); continue; }
                    Finish(gig, verdict.Verdict == "fail" ? "failed" : "done", verdict.Report);
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
            "- builder: OpenCode, a coding agent that writes and edits code in a project folder.\n" +
            "Reply with only JSON, no other text: {\"title\": \"a short name for the job, 2 to 5 words\", \"worker\": \"terminal|kaggle|research|builder\", \"plan\": \"what Capri says out loud about the plan\"}.\n" +
            "Use the talk around the job to work out what they really meant (which thing, where), and plan for that.\n" +
            "The plan is Capri talking to them, casual and in first person, one or two sentences: what she's putting which worker on and how it'll go, and which brain is doing the thinking. " +
            "Talk like a friend, not an assistant: no lists, no step numbers, no 'I will now', no 'Certainly', no 'Here's the plan'. Vary how you start it.";

        private void PlanIt(Gig gig, Brain brain)
        {
            string user = "Their job: " + Asked(gig) + "\nWorker that looks right: " + gig.Worker + (HasOpenCode ? "" : " (OpenCode isn't installed, so no builder)") +
                "\nBrain doing the thinking: " + gig.Mind + "\nJob folder: " + gig.Folder;
            GigPlan plan = null;
            try { plan = ParseJson<GigPlan>(brain.Consult(PlanPrompt, user, 300)); }
            catch (Exception error) { Note(gig, "Planning glitched: " + Voice.Short(error.Message)); }
            if (plan != null)
            {
                if (!string.IsNullOrWhiteSpace(plan.Title)) gig.Title = Shorten(plan.Title);
                string worker = (plan.Worker ?? "").Trim().ToLowerInvariant();
                if (worker == "terminal" || worker == "kaggle" || worker == "research" || (worker == "builder" && HasOpenCode)) gig.Worker = worker;
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
            "If something needs them, like signing in, a password, an API key, a payment or a click in a browser, stop and call finish with action failed and say exactly what they need to do.\n" +
            "Jobs are often short, like \"download DeepSeek\": read what you two were just talking about to work out what they meant, like which model, which site or which machine (a job about a Kaggle notebook happens on Kaggle, through the kaggle command, not on this PC). Don't install some other app just because its name matches. If it's still unclear what they want, don't guess: call finish with action failed and ask them the one question that settles it.\n" +
            "Check your work before you finish, like running the program's --version or listing the files. Then call finish with action done and what you did, what they need to know and where things are, in plain words.";

        private void Terminal(Gig gig, Brain brain, string notes, string extra = null)
        {
            string prompt = TerminalPrompt + (extra ?? "");
            List<string> steps = new List<string>();
            for (int step = 0; step < MaxSteps && !gig.Stop; step++)
            {
                List<KeyValuePair<string, ToolArguments>> calls = new List<KeyValuePair<string, ToolArguments>>();
                string written = brain.Step(prompt, Brief(gig, notes, steps), Render(TerminalTools, false), Render(TerminalTools, true), Listing(TerminalTools), calls, 700);
                string tool;
                ToolArguments args;
                if (!Pick(calls, written, out tool, out args)) { steps.Add("(You replied without a tool call. Call exactly one tool.)"); continue; }
                if (tool == "finish") { gig.Result = Clean(args.Text, "Done."); if (args.Action == "failed") Note(gig, "Worker says it can't finish: " + gig.Result); return; }
                string outcome = Act(gig, tool, args);
                steps.Add(Describe(tool, args) + "\n" + Trim(outcome, 1100));
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
            for (int step = 0; step < MaxSteps && !gig.Stop; step++)
            {
                List<KeyValuePair<string, ToolArguments>> calls = new List<KeyValuePair<string, ToolArguments>>();
                string written = brain.Step(ResearchPrompt, Brief(gig, notes, steps, 3, 7000), Render(ResearchTools, false), Render(ResearchTools, true), Listing(ResearchTools), calls, 1100);
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
                steps.Add(Describe(tool, args) + "\n" + Trim(found, 2200));
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

        private void Build(Gig gig, string notes)
        {
            Directory.CreateDirectory(gig.Folder);
            string prompt = gig.Ask + (string.IsNullOrEmpty(gig.Context) ? "" : "\n\nWhat we were just talking about, for context:\n" + gig.Context) + "\n\nPlan: " + gig.Plan + (string.IsNullOrEmpty(notes) ? "" : "\n\nA reviewer checked your last attempt and said: " + notes) +
                "\n\nWork in this folder. When you're done, finish with a short summary of what you built or changed, and how to run or use it.";
            string model = OpenCodeModel();
            string command = "opencode run --auto --dir " + Quote(gig.Folder) + " --title " + Quote(gig.Title) + (string.IsNullOrWhiteSpace(model) ? "" : " --model " + Quote(model.Trim())) + " " + Quote(prompt);
            Note(gig, "OpenCode is building in " + gig.Folder + (string.IsNullOrWhiteSpace(model) ? "" : " with " + model.Trim()));
            int code;
            string output = Shell(gig, command, gig.Folder, 2400, out code);
            output = Regex.Replace(output, @"\x1B\[[0-9;?]*[A-Za-z]", "");
            gig.Result = "OpenCode exited with code " + code + ". The end of its output:\n" + Tail(output, 3000);
            Note(gig, "OpenCode finished (exit " + code + ").");
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
            lock (sink) return output.ToString().TrimEnd();
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

        private static string Brief(Gig gig, string notes, List<string> steps, int full = 5, int room = 6500)
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
            text.Append(steps.Count >= MaxSteps - 3 ? "\n\nYou're nearly out of steps: finish now with what you have." : "\n\nWhat's next? Call exactly one tool.");
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
            args = Hands.Read(end > 0 ? json.Substring(0, end + 1) : json);
            if (args == null) return false;
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

        internal static string WorkerName(string worker) { return worker == "research" ? "research" : worker == "builder" ? "OpenCode builder" : worker == "kaggle" ? "Kaggle" : "terminal"; }

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
            string slug = Regex.Replace(gig.Title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 40) slug = slug.Substring(0, 40).Trim('-');
            return Path.Combine(home, gig.Id + "-" + (slug.Length == 0 ? "job" : slug));
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
        private readonly Button stop = new Button(), open = new Button(), clear = new Button(), saveOverseer = new Button(), saveModel = new Button(), saveKaggle = new Button();
        private readonly TextBox overseer = new TextBox(), model = new TextBox(), kaggle = new TextBox();
        private readonly Label overseerLabel = new Label(), modelLabel = new Label(), kaggleLabel = new Label(), status = new Label();
        internal Action<string> SaveOverseer;
        internal Action<string> SaveModel;
        internal Func<string> OverseerTail;
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
            ClientSize = new Size(820, 620);
            MinimumSize = new Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;

            list.BackColor = Panel; list.ForeColor = Ink; list.BorderStyle = BorderStyle.None; list.IntegralHeight = false;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 22;
            list.DrawItem += DrawGig;
            list.SelectedIndexChanged += delegate { Display(Selected); };
            details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical; details.WordWrap = true;
            details.BackColor = Panel; details.ForeColor = Ink; details.BorderStyle = BorderStyle.None;
            foreach (Button b in new[] { stop, open, clear, saveOverseer, saveModel, saveKaggle })
            {
                b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Color.FromArgb(60, 64, 78); b.BackColor = Panel; b.ForeColor = Ink;
            }
            stop.Text = "Stop job"; open.Text = "Open folder"; clear.Text = "Clear finished"; saveOverseer.Text = "Save"; saveModel.Text = "Save"; saveKaggle.Text = "Save";
            stop.Click += delegate { Gig g = Selected; if (g != null && !g.Finished) crew.Stop(g); };
            open.Click += delegate { Gig g = Selected; string folder = g != null ? g.Folder : crew.Home; try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\""); } catch (Exception) { } };
            clear.Click += delegate { crew.Clear(); };
            overseerLabel.Text = "Overseer brain (optional): paste a server link, then a space and its key - like your Kaggle DeepSeek tunnel. Blank = her own brain checks the work.";
            overseerLabel.ForeColor = Dim; overseerLabel.AutoSize = false;
            overseer.BackColor = Panel; overseer.ForeColor = Ink; overseer.BorderStyle = BorderStyle.FixedSingle; overseer.UseSystemPasswordChar = true;
            saveOverseer.Click += delegate { Action<string> save = SaveOverseer; if (save != null) save(overseer.Text.Trim()); overseer.Text = ""; Reload(); };
            modelLabel.Text = "OpenCode model (optional, like opencode/grok-code). Blank = OpenCode's own default.";
            modelLabel.ForeColor = Dim; modelLabel.AutoSize = false;
            model.BackColor = Panel; model.ForeColor = Ink; model.BorderStyle = BorderStyle.FixedSingle;
            saveModel.Click += delegate { Action<string> save = SaveModel; if (save != null) save(model.Text.Trim()); };
            kaggleLabel.ForeColor = Dim; kaggleLabel.AutoSize = false;
            kaggle.BackColor = Panel; kaggle.ForeColor = Ink; kaggle.BorderStyle = BorderStyle.FixedSingle; kaggle.UseSystemPasswordChar = true;
            saveKaggle.Click += delegate { string said = Crew.SaveKaggle(kaggle.Text); kaggle.Text = ""; Reload(); kaggleLabel.Text = said; };
            status.ForeColor = Dim; status.AutoSize = false;
            Controls.AddRange(new Control[] { list, details, stop, open, clear, overseerLabel, overseer, saveOverseer, modelLabel, model, saveModel, kaggleLabel, kaggle, saveKaggle, status });
            Resize += delegate { Arrange(); };
            Arrange();
            Reload();
        }

        internal string ModelText { set { model.Text = value ?? ""; } }

        private void Arrange()
        {
            int w = ClientSize.Width, h = ClientSize.Height, pad = 12, bottom = 188;
            int listW = Math.Max(240, w * 2 / 5);
            list.Bounds = new Rectangle(pad, pad, listW, h - bottom - pad * 2 - 34);
            details.Bounds = new Rectangle(pad * 2 + listW, pad, w - listW - pad * 3, h - bottom - pad * 2 - 34);
            int by = h - bottom - pad - 28;
            stop.Bounds = new Rectangle(pad, by, 100, 28);
            open.Bounds = new Rectangle(pad + 108, by, 110, 28);
            clear.Bounds = new Rectangle(pad + 226, by, 120, 28);
            status.Bounds = new Rectangle(pad + 356, by + 6, w - pad * 2 - 356, 20);
            overseerLabel.Bounds = new Rectangle(pad, h - bottom + 4, w - pad * 2, 32);
            overseer.Bounds = new Rectangle(pad, h - bottom + 38, w - pad * 2 - 80, 24);
            saveOverseer.Bounds = new Rectangle(w - pad - 72, h - bottom + 36, 72, 28);
            modelLabel.Bounds = new Rectangle(pad, h - bottom + 72, w - pad * 2, 18);
            model.Bounds = new Rectangle(pad, h - bottom + 94, w - pad * 2 - 80, 24);
            saveModel.Bounds = new Rectangle(w - pad - 72, h - bottom + 92, 72, 28);
            kaggleLabel.Bounds = new Rectangle(pad, h - bottom + 128, w - pad * 2, 18);
            kaggle.Bounds = new Rectangle(pad, h - bottom + 150, w - pad * 2 - 80, 24);
            saveKaggle.Bounds = new Rectangle(w - pad - 72, h - bottom + 148, 72, 28);
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
            Func<string> tail = OverseerTail;
            string t = tail == null ? null : tail();
            overseer.Text = "";
            overseerLabel.Text = "Overseer brain (optional): paste a server link, then a space and its key - like your Kaggle DeepSeek tunnel. " + (string.IsNullOrEmpty(t) ? "Blank = her own brain checks the work." : "Saved: " + t + ". Save it empty to go back to her own brain.");
            kaggleLabel.Text = Crew.KaggleReady ? "Kaggle: key saved, so her Kaggle worker is signed in. Paste a new token here to replace it." : "Kaggle API token: get one at kaggle.com/settings/api (Generate New Token) and paste it here, so her Kaggle worker can run your notebooks.";
            int running = all.FindAll(g => !g.Finished).Count;
            status.Text = running == 0 ? "Nothing running." + (Crew.HasOpenCode ? " OpenCode: found." : " OpenCode: not found on PATH.") : running + " running." + (Crew.HasOpenCode ? " OpenCode: found." : "");
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

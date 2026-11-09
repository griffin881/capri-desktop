using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CapriDesktop
{
    // Her eyes on their real code. She reads the actual source file and the real build errors, so when she pairs
    // with them she diagnoses from the true text, not from a blurry screenshot. She reads and compiles; she never
    // edits their files. The person stays the one who types the fix.
    internal static class Coder
    {
        // A text file listing the project folders they've let her into, one per line, newest last. Set at start; tests repoint it.
        internal static string RootsPath;
        // Runs a build/test command in a folder and returns its output. Swapped out in tests so nothing really runs.
        internal static Func<string, string, string> RunBuild = Compile;

        private const int ReadCap = 16000;
        private const int Window = 60;
        private const int MaxHits = 40;
        private const int MaxFiles = 6000;
        private const int BuildSeconds = 240;

        // Source and text files she'll read or search; build junk she skips.
        private static readonly Regex Source = new Regex(@"\.(cs|csproj|sln|js|jsx|ts|tsx|py|java|c|h|cpp|cc|hpp|go|rs|rb|php|swift|kt|scala|sh|ps1|bat|sql|html?|css|scss|json|ya?ml|toml|xml|md|txt|ini|cfg|conf|gradle|make|mk|Dockerfile)$", RegexOptions.IgnoreCase);
        private static readonly Regex Skip = new Regex(@"(^|[\\/])(\.git|node_modules|bin|obj|dist|build|out|target|\.vs|\.vscode|\.idea|__pycache__|venv|env|\.venv|packages|vendor)([\\/]|$)", RegexOptions.IgnoreCase);

        // The project folders she's allowed into, newest first (newest is the one they're most likely working in).
        internal static List<string> Roots()
        {
            List<string> roots = new List<string>();
            string path = RootsPath;
            try
            {
                if (path != null && File.Exists(path))
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        string folder = raw.Trim();
                        if (folder.Length > 0 && Directory.Exists(folder) && !roots.Contains(folder)) roots.Insert(0, folder);
                    }
            }
            catch (Exception) { }
            return roots;
        }

        // The project they're working in: the one named, else the newest allowed folder.
        internal static string Active(string hint)
        {
            List<string> roots = Roots();
            if (!string.IsNullOrWhiteSpace(hint))
            {
                string want = hint.Trim().Trim('"', '\'');
                foreach (string root in roots) if (root.Replace('\\', '/').EndsWith("/" + want.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(root.TrimEnd('\\', '/')), want, StringComparison.OrdinalIgnoreCase)) return root;
                if (Directory.Exists(want)) return want;
            }
            return roots.Count > 0 ? roots[0] : null;
        }

        internal static string Handle(ToolArguments args)
        {
            if (args == null) args = new ToolArguments();
            string action = (args.Action ?? "").Trim().ToLowerInvariant();
            if (action.Length == 0) action = !string.IsNullOrWhiteSpace(args.Search) ? "find" : !string.IsNullOrWhiteSpace(args.Name) ? "read" : "build";
            if (action == "set" || action == "open" || action == "use") return Set(args.Text ?? args.Name);
            string root = Active(args.Target);
            if (root == null) return Missing;
            try
            {
                if (action == "read" || action == "show") return Read(root, args.Name, args.Level);
                if (action == "find" || action == "search" || action == "grep") return Search(root, args.Search ?? args.Text ?? args.Name);
                if (action == "tree" || action == "map" || action == "files" || action == "list") return Tree(root);
                if (action == "build" || action == "compile" || action == "run" || action == "test" || action == "errors" || action == "check") return Build(root);
                return "I can read, find, show the tree, or build this project. I don't do " + action + ".";
            }
            catch (Exception error) { return "Couldn't do that in your project: " + error.Message; }
        }

        internal const string Missing = "I don't have a project folder to look in yet. Tell me where it is, like: my project is at C:\\Users\\you\\capri-desktop.";

        private static string Set(string where)
        {
            string folder = (where ?? "").Trim().Trim('"', '\'', '`').TrimEnd('.', ' ');
            folder = Regex.Replace(folder, @"^(my |the |our )?(project|repo|code|folder|codebase) (is )?(at |in )", "", RegexOptions.IgnoreCase);
            if (folder.StartsWith("~", StringComparison.Ordinal)) folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + folder.Substring(1);
            if (folder.Length == 0) return "Which folder is your project in, choom?";
            if (!Directory.Exists(folder)) return "There's no folder at " + folder + ".";
            string path = RootsPath;
            if (path == null) return "I can't save where your project is right now.";
            List<string> roots = Roots();
            roots.Remove(folder);
            roots.Insert(0, folder);
            if (roots.Count > 12) roots = roots.GetRange(0, 12);
            roots.Reverse();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, roots.ToArray());
            int count = Files(folder).Count;
            return "Got it, I'm in " + Path.GetFileName(folder.TrimEnd('\\', '/')) + " (" + count + (count == 1 ? " file" : " files") + "). Show me what you're stuck on and I'll read it.";
        }

        // Every source file in the project, skipping build output.
        private static List<string> Files(string root)
        {
            List<string> files = new List<string>();
            Walk(root, files);
            return files;
        }

        private static void Walk(string folder, List<string> files)
        {
            if (files.Count >= MaxFiles) return;
            try
            {
                foreach (string file in Directory.GetFiles(folder))
                {
                    if (files.Count >= MaxFiles) return;
                    if (Source.IsMatch(file) && !Skip.IsMatch(file)) files.Add(file);
                }
                foreach (string child in Directory.GetDirectories(folder))
                {
                    if (Skip.IsMatch(child + Path.DirectorySeparatorChar)) continue;
                    Walk(child, files);
                }
            }
            catch (Exception) { }
        }

        private static string Rel(string root, string file)
        {
            string baseline = Path.GetFullPath(root).TrimEnd('\\', '/');
            string full = Path.GetFullPath(file);
            return full.StartsWith(baseline, StringComparison.OrdinalIgnoreCase) ? full.Substring(baseline.Length).TrimStart('\\', '/').Replace('\\', '/') : Path.GetFileName(file);
        }

        // The file they mean: exact path, exact name, or best partial name match.
        private static string Locate(string root, string name)
        {
            string want = (name ?? "").Trim().Trim('"', '\'', '`').Replace('\\', '/');
            Match at = Regex.Match(want, @"^(.*?)[(:\s]+(?:line\s*)?(\d+)", RegexOptions.IgnoreCase);
            if (at.Success && at.Groups[1].Value.Trim().Length > 0) want = at.Groups[1].Value.Trim();
            if (want.Length == 0) return null;
            List<string> files = Files(root);
            string plain = want.ToLowerInvariant();
            foreach (string file in files) if (Rel(root, file).ToLowerInvariant() == plain) return file;
            string leaf = plain.Contains("/") ? plain.Substring(plain.LastIndexOf('/') + 1) : plain;
            foreach (string file in files) if (Path.GetFileName(file).ToLowerInvariant() == leaf) return file;
            foreach (string file in files) if (Path.GetFileName(file).ToLowerInvariant().StartsWith(leaf, StringComparison.Ordinal)) return file;
            if (leaf.Length >= 3) foreach (string file in files) if (Rel(root, file).ToLowerInvariant().Contains(leaf)) return file;
            return null;
        }

        // A line number tucked inside the name, like Brain.cs(952,13) or Brain.cs:952, so "read the error file" lands on the spot.
        private static int LineIn(string name)
        {
            Match at = Regex.Match(name ?? "", @"[(:]\s*(?:line\s*)?(\d+)", RegexOptions.IgnoreCase);
            return at.Success ? int.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        private static string Read(string root, string name, string lineText)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Which file, choom?";
            string file = Locate(root, name);
            if (file == null) return "I can't find a file called " + name.Trim() + " in " + Path.GetFileName(root.TrimEnd('\\', '/')) + ". Try the tree so I can see what's there.";
            string[] lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
            int focus = 0;
            if (!string.IsNullOrWhiteSpace(lineText)) int.TryParse(Regex.Match(lineText, @"\d+").Value, out focus);
            if (focus == 0) focus = LineIn(name);
            StringBuilder said = new StringBuilder(Rel(root, file) + (focus > 0 ? " around line " + focus : "") + ":\n");
            int from = 1, to = lines.Length;
            if (focus > 0) { from = Math.Max(1, focus - Window / 2); to = Math.Min(lines.Length, focus + Window / 2); }
            int shown = 0, chars = said.Length;
            for (int i = from; i <= to && chars < ReadCap; i++)
            {
                string row = (i == focus ? ">>" : "  ") + i.ToString(CultureInfo.InvariantCulture) + " | " + lines[i - 1];
                said.Append(row + "\n");
                chars += row.Length + 1;
                shown++;
            }
            if (from > 1 || to < lines.Length) said.Append("(showing lines " + from + "-" + (from + shown - 1) + " of " + lines.Length + ")");
            return said.ToString().TrimEnd();
        }

        private static string Search(string root, string query)
        {
            string want = (query ?? "").Trim();
            if (want.Length < 2) return "Search the code for what, choom?";
            Regex term;
            try { term = new Regex(Regex.Escape(want), RegexOptions.IgnoreCase); } catch (Exception) { return "I couldn't search for that."; }
            List<string> hits = new List<string>();
            foreach (string file in Files(root))
            {
                string[] lines;
                try { lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n'); } catch (Exception) { continue; }
                for (int i = 0; i < lines.Length && hits.Count < MaxHits; i++)
                    if (term.IsMatch(lines[i])) hits.Add(Rel(root, file) + ":" + (i + 1) + ":  " + lines[i].Trim());
                if (hits.Count >= MaxHits) break;
            }
            if (hits.Count == 0) return "Nothing in this project matches " + want + ".";
            return "Found " + want + (hits.Count >= MaxHits ? " (first " + MaxHits + ")" : "") + ":\n" + string.Join("\n", hits.ToArray());
        }

        private static string Tree(string root)
        {
            List<string> files = Files(root);
            if (files.Count == 0) return "I don't see any source files in " + Path.GetFileName(root.TrimEnd('\\', '/')) + ".";
            files.Sort(StringComparer.OrdinalIgnoreCase);
            List<string> rel = new List<string>();
            foreach (string file in files) rel.Add(Rel(root, file));
            StringBuilder said = new StringBuilder(Path.GetFileName(root.TrimEnd('\\', '/')) + " (" + files.Count + " files):");
            int shown = 0;
            foreach (string line in rel)
            {
                if (shown >= 200) { said.Append("\n...and " + (rel.Count - shown) + " more"); break; }
                said.Append("\n" + line);
                shown++;
            }
            return said.ToString();
        }

        private static string Build(string root)
        {
            Func<string, string, string> run = RunBuild;
            string command = CommandFor(root);
            if (command == null) return "I don't see a build or test command for this project, so I can't compile it. Paste the error you're getting and I'll read the file around it.";
            string output = (run ?? Compile)(root, command);
            output = Trim(output);
            if (output.Length == 0) return "Ran " + command + " and it came back clean, no errors.";
            return "Ran " + command + ":\n" + output;
        }

        // The right build or test command for the project, from what's in the folder.
        internal static string CommandFor(string root)
        {
            try
            {
                if (Directory.GetFiles(root, "*.sln").Length > 0 || Has(root, "*.csproj")) return "dotnet build --nologo -v q";
                string package = Path.Combine(root, "package.json");
                if (File.Exists(package))
                {
                    string json = File.ReadAllText(package);
                    if (Regex.IsMatch(json, @"""build""\s*:")) return "npm run build";
                    if (Regex.IsMatch(json, @"""test""\s*:\s*""(?!echo)")) return "npm test";
                }
                if (File.Exists(Path.Combine(root, "Cargo.toml"))) return "cargo build";
                if (File.Exists(Path.Combine(root, "go.mod"))) return "go build ./...";
                if (File.Exists(Path.Combine(root, "pyproject.toml")) || File.Exists(Path.Combine(root, "requirements.txt")) || Has(root, "*.py")) return "python -m py_compile .";
                if (File.Exists(Path.Combine(root, "Makefile"))) return "make";
            }
            catch (Exception) { }
            return null;
        }

        private static bool Has(string root, string pattern)
        {
            try { return Directory.GetFiles(root, pattern).Length > 0 || Directory.GetDirectories(root).Length > 0 && NestedHas(root, pattern, 0); } catch (Exception) { return false; }
        }

        private static bool NestedHas(string folder, string pattern, int depth)
        {
            if (depth > 3) return false;
            try
            {
                if (Directory.GetFiles(folder, pattern).Length > 0) return true;
                foreach (string child in Directory.GetDirectories(folder)) { if (Skip.IsMatch(child + Path.DirectorySeparatorChar)) continue; if (NestedHas(child, pattern, depth + 1)) return true; }
            }
            catch (Exception) { }
            return false;
        }

        // Runs the build through cmd and hands back whatever it printed, errors and all. Only known build/test commands reach here.
        private static string Compile(string root, string command)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/c " + command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
                StringBuilder sink = new StringBuilder();
                using (Process build = new Process { StartInfo = start })
                {
                    build.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sink) sink.AppendLine(e.Data); };
                    build.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sink) sink.AppendLine(e.Data); };
                    build.Start();
                    build.BeginOutputReadLine();
                    build.BeginErrorReadLine();
                    if (!build.WaitForExit(BuildSeconds * 1000)) { try { build.Kill(); } catch (Exception) { } return "(the build ran longer than " + BuildSeconds + "s, so I stopped it)"; }
                    build.WaitForExit();
                    lock (sink) return sink.ToString();
                }
            }
            catch (Exception error) { return "(couldn't run the build: " + error.Message + ")"; }
        }

        // Keeps the lines that matter: errors and warnings first, so a long clean build doesn't bury the one real problem.
        private static string Trim(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return "";
            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            List<string> problems = new List<string>();
            List<string> rest = new List<string>();
            foreach (string line in lines)
            {
                string row = line.TrimEnd();
                if (row.Trim().Length == 0) continue;
                if (Regex.IsMatch(row, @"\b(error|errors|failed|failure|exception|traceback|cannot|undefined|not found|unresolved)\b", RegexOptions.IgnoreCase) || Regex.IsMatch(row, @"\berror\s+[A-Z]{1,4}\d", RegexOptions.IgnoreCase)) problems.Add(row);
                else rest.Add(row);
            }
            List<string> keep = problems.Count > 0 ? problems : rest;
            if (keep.Count > 50) { keep = keep.GetRange(0, 50); keep.Add("...(more output cut)"); }
            return string.Join("\n", keep.ToArray());
        }

        // What she brings into a coding chat: that she can read their project and compile it, and which project is in front.
        internal static string Context()
        {
            string root;
            try { root = Active(null); } catch (Exception) { return ""; }
            if (root == null) return "\nTo pair on their code you need a project folder: ask them where it is, or they can say \"my project is at ...\", then use the code tool to read the real file and build it.";
            return "\nYou can read and build their project " + Path.GetFileName(root.TrimEnd('\\', '/')) + " with the code tool: read a file (with a line number to land on the spot), find text across it, show its tree, or build it to see the real errors. Diagnose from the real file and the real build output, not from a screenshot alone. You read and compile; they type the fix.";
        }
    }
}

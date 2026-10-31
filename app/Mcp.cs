// Her app plug: MCP servers, the same ones Claude Desktop and other agents use, so her crew can work inside their apps.
// A server is a program on this PC that talks JSON-RPC over its stdin and stdout (most of them, started with npx or uvx),
// or a link that talks it over HTTP. They're listed in apps.json in her data folder, in Claude Desktop's format.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CapriDesktop
{
    // A small JSON reader and writer for MCP's free-form messages: objects are Dictionary<string, object>, arrays List<object>, numbers double.
    internal static class AppJson
    {
        internal static object Parse(string text)
        {
            if (text == null) throw new FormatException("No JSON.");
            int i = 0;
            object value = Value(text, ref i);
            Skip(text, ref i);
            if (i != text.Length) throw new FormatException("Extra text after the JSON.");
            return value;
        }

        internal static Dictionary<string, object> Object(string text) { return Parse(text) as Dictionary<string, object>; }

        private static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static void Expect(string s, ref int i, char c)
        {
            Skip(s, ref i);
            if (i >= s.Length || s[i] != c) throw new FormatException("Expected " + c + " in the JSON.");
            i++;
        }

        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("The JSON ended early.");
            char c = s[i];
            if (c == '{')
            {
                i++;
                Dictionary<string, object> obj = new Dictionary<string, object>();
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Skip(s, ref i);
                    string key = Text(s, ref i);
                    Expect(s, ref i, ':');
                    obj[key] = Value(s, ref i);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return obj;
                }
            }
            if (c == '[')
            {
                i++;
                List<object> list = new List<object>();
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return list;
                }
            }
            if (c == '"') return Text(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double number;
            if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) throw new FormatException("Bad value in the JSON.");
            return number;
        }

        private static string Text(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected a string in the JSON.");
            i++;
            StringBuilder text = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return text.ToString();
                if (c != '\\') { text.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad escape in the JSON.");
                        text.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: text.Append(e); break;
                }
            }
            throw new FormatException("A string in the JSON never ended.");
        }

        // Plain ASCII out, so it survives any console code page.
        internal static string Write(object value)
        {
            StringBuilder text = new StringBuilder();
            Write(text, value);
            return text.ToString();
        }

        private static void Write(StringBuilder text, object value)
        {
            if (value == null) { text.Append("null"); return; }
            string s = value as string;
            if (s != null) { Quote(text, s); return; }
            if (value is bool) { text.Append((bool)value ? "true" : "false"); return; }
            if (value is double || value is float || value is int || value is long || value is decimal)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                text.Append(d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            IDictionary<string, object> obj = value as IDictionary<string, object>;
            if (obj != null)
            {
                text.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in obj)
                {
                    if (!first) text.Append(',');
                    first = false;
                    Quote(text, pair.Key);
                    text.Append(':');
                    Write(text, pair.Value);
                }
                text.Append('}');
                return;
            }
            IEnumerable list = value as IEnumerable;
            if (list != null)
            {
                text.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) text.Append(',');
                    first = false;
                    Write(text, item);
                }
                text.Append(']');
                return;
            }
            Quote(text, value.ToString());
        }

        private static void Quote(StringBuilder text, string s)
        {
            text.Append('"');
            foreach (char c in s)
            {
                if (c == '"') text.Append("\\\"");
                else if (c == '\\') text.Append("\\\\");
                else if (c == '\n') text.Append("\\n");
                else if (c == '\r') text.Append("\\r");
                else if (c == '\t') text.Append("\\t");
                else if (c < 32 || c > 126) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            text.Append('"');
        }

        internal static string Str(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as string : null;
        }

        internal static Dictionary<string, object> Obj(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        internal static List<object> List(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as List<object> : null;
        }
    }

    // One tool on one app, named for the model as app__tool.
    internal sealed class AppTool
    {
        internal string App, Name, Exposed, Description, Schema;
    }

    // One MCP server: started when a job first needs it, kept running while it's useful.
    internal sealed class AppServer
    {
        internal readonly string Name;
        private readonly string command, url;
        private readonly List<string> args;
        private readonly Dictionary<string, string> env, headers;
        private readonly Func<string, string> fill;
        private readonly object gate = new object();
        private readonly Dictionary<int, AppReply> waiting = new Dictionary<int, AppReply>();
        private readonly StringBuilder errors = new StringBuilder();
        private Process process;
        private StreamWriter input;
        private int nextId, heard;
        private string session;
        internal List<AppTool> Tools;
        internal string Problem;
        internal DateTime Used = DateTime.UtcNow;
        internal const string Protocol = "2025-06-18";

        private sealed class AppReply
        {
            internal readonly ManualResetEvent Done = new ManualResetEvent(false);
            internal Dictionary<string, object> Message;
        }

        internal AppServer(string name, Dictionary<string, object> spec, Func<string, string> fill)
        {
            Name = name;
            this.fill = fill;
            command = AppJson.Str(spec, "command");
            url = AppJson.Str(spec, "url") ?? AppJson.Str(spec, "serverUrl");
            args = new List<string>();
            List<object> given = AppJson.List(spec, "args");
            if (given != null) foreach (object arg in given) if (arg != null) args.Add(Convert.ToString(arg, CultureInfo.InvariantCulture));
            env = Strings(AppJson.Obj(spec, "env"));
            headers = Strings(AppJson.Obj(spec, "headers"));
        }

        private static Dictionary<string, string> Strings(Dictionary<string, object> obj)
        {
            Dictionary<string, string> all = new Dictionary<string, string>();
            if (obj != null) foreach (KeyValuePair<string, object> pair in obj) if (pair.Value != null) all[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
            return all;
        }

        internal bool Running { get { lock (gate) return url != null ? session != null || Tools != null : process != null && !process.HasExited; } }

        // ${NAME} in a server's settings is a saved key from the vault, or an environment variable.
        private string Filled(string text) { return fill == null ? text : fill(text); }

        internal void Start(int wait)
        {
            lock (gate) if (Tools != null && (url != null || (process != null && !process.HasExited))) return;
            Problem = null;
            Tools = null;
            try
            {
                if (url == null) Launch();
                Dictionary<string, object> hello = new Dictionary<string, object>
                {
                    { "protocolVersion", Protocol },
                    { "capabilities", new Dictionary<string, object>() },
                    { "clientInfo", new Dictionary<string, object> { { "name", "Capri" }, { "version", typeof(AppServer).Assembly.GetName().Version.ToString(3) } } }
                };
                Request("initialize", hello, wait);
                Notify("notifications/initialized");
                List<AppTool> found = new List<AppTool>();
                string cursor = null;
                for (int page = 0; page < 20; page++)
                {
                    Dictionary<string, object> asking = new Dictionary<string, object>();
                    if (cursor != null) asking["cursor"] = cursor;
                    Dictionary<string, object> listed = Request("tools/list", asking, 60000);
                    List<object> tools = AppJson.List(listed, "tools");
                    if (tools != null)
                        foreach (object item in tools)
                        {
                            Dictionary<string, object> tool = item as Dictionary<string, object>;
                            string name = AppJson.Str(tool, "name");
                            if (name == null) continue;
                            Dictionary<string, object> schema = AppJson.Obj(tool, "inputSchema") ?? new Dictionary<string, object> { { "type", "object" }, { "properties", new Dictionary<string, object>() } };
                            Clean(schema);
                            if (!schema.ContainsKey("type")) schema["type"] = "object";
                            if (!schema.ContainsKey("properties")) schema["properties"] = new Dictionary<string, object>();
                            string about = AppJson.Str(tool, "description") ?? AppJson.Str(tool, "title") ?? name;
                            found.Add(new AppTool { App = Name, Name = name, Description = about.Length > 700 ? about.Substring(0, 700) + "..." : about, Schema = AppJson.Write(schema) });
                        }
                    cursor = AppJson.Str(listed, "nextCursor");
                    if (string.IsNullOrEmpty(cursor)) break;
                }
                Tools = found;
                Used = DateTime.UtcNow;
            }
            catch (Exception error)
            {
                Problem = Explain(error);
                Stop();
            }
        }

        // Schema words some brains reject; the rest of the schema says the same thing.
        private static void Clean(Dictionary<string, object> schema)
        {
            foreach (string word in new[] { "$schema", "$id", "additionalProperties", "examples", "$comment" }) schema.Remove(word);
            foreach (object value in new List<object>(schema.Values))
            {
                Dictionary<string, object> inner = value as Dictionary<string, object>;
                if (inner != null) Clean(inner);
                List<object> list = value as List<object>;
                if (list != null) foreach (object item in list) { Dictionary<string, object> each = item as Dictionary<string, object>; if (each != null) Clean(each); }
            }
        }

        private string Explain(Exception error)
        {
            string said;
            lock (errors) said = errors.ToString();
            if (Regex.IsMatch(said, @"'(npx|npm|node)' is not recognized|(npx|node)(\.cmd)?: (command )?not found", RegexOptions.IgnoreCase)) return "it needs Node.js, which isn't installed. Ask me to install Node.js LTS, then try again.";
            if (Regex.IsMatch(said, @"'(uvx|uv)' is not recognized", RegexOptions.IgnoreCase)) return "it needs uv, which isn't installed. Ask me to install uv (astral-sh.uv), then try again.";
            if (Regex.IsMatch(said, @"'(docker)' is not recognized", RegexOptions.IgnoreCase)) return "it needs Docker, which isn't installed.";
            WebException web = error as WebException;
            HttpWebResponse response = web == null ? null : web.Response as HttpWebResponse;
            if (response != null && ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)) return "it wants a sign-in or a token. If it takes a token, save it in Keys and put ${ITS_NAME} in its headers in apps.json.";
            string tail = said.Trim();
            if (tail.Length > 300) tail = tail.Substring(tail.Length - 300);
            return error.Message + (tail.Length > 0 ? " (it said: " + tail.Replace("\r", " ").Replace("\n", " ") + ")" : "");
        }

        private void Launch()
        {
            if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("it has no command or url in apps.json.");
            string program = Filled(command);
            StringBuilder rest = new StringBuilder();
            foreach (string arg in args) rest.Append(' ').Append(Arg(Filled(arg)));
            string line = Arg(program) + rest;
            // A program starts by itself; anything else goes through cmd, so npx, uvx and other .cmd launchers start like they do in a terminal (through sh off Windows).
            bool windows = System.IO.Path.DirectorySeparatorChar == '\\';
            ProcessStartInfo start = windows && program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? new ProcessStartInfo(program, rest.ToString().TrimStart())
                : windows ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + line + "\"") : new ProcessStartInfo("/bin/sh", "-c '" + line.Replace("'", "'\\''") + "'");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = new UTF8Encoding(false);
            start.StandardErrorEncoding = new UTF8Encoding(false);
            start.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (KeyValuePair<string, string> pair in env) start.EnvironmentVariables[pair.Key] = Filled(pair.Value);
            Process started = new Process { StartInfo = start, EnableRaisingEvents = true };
            started.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data == null) return; lock (errors) { errors.AppendLine(e.Data); if (errors.Length > 8000) errors.Remove(0, errors.Length - 8000); } };
            started.Exited += delegate { Fail("it closed"); };
            started.Start();
            started.BeginErrorReadLine();
            lock (gate)
            {
                process = started;
                input = new StreamWriter(started.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            }
            Thread reader = new Thread(delegate() { Read(started); }) { IsBackground = true, Name = "Capri app " + Name };
            reader.Start();
        }

        private static string Arg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')' }) < 0) return arg;
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        private void Read(Process from)
        {
            try
            {
                string text;
                while ((text = from.StandardOutput.ReadLine()) != null)
                {
                    text = text.Trim();
                    Interlocked.Increment(ref heard);
                    if (text.Length == 0 || text[0] != '{') continue;
                    Dictionary<string, object> message;
                    try { message = AppJson.Object(text); } catch (FormatException) { continue; }
                    if (message != null) Take(message, true);
                }
            }
            catch (Exception) { }
            Fail("it stopped answering");
        }

        // A reply goes to whoever's waiting on it; a server asking something of us gets a short answer.
        private void Take(Dictionary<string, object> message, bool canAnswer)
        {
            object id;
            message.TryGetValue("id", out id);
            string method = AppJson.Str(message, "method");
            if (method == null && id != null)
            {
                AppReply reply;
                int number;
                if (!(id is double) || !int.TryParse(Convert.ToString(id, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return;
                lock (gate) { if (waiting.TryGetValue(number, out reply)) waiting.Remove(number); }
                if (reply != null) { reply.Message = message; reply.Done.Set(); }
                return;
            }
            if (method != null && id != null && canAnswer)
            {
                Dictionary<string, object> answer = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id } };
                if (method == "ping") answer["result"] = new Dictionary<string, object>();
                else if (method == "roots/list") answer["result"] = new Dictionary<string, object> { { "roots", new List<object>() } };
                else answer["error"] = new Dictionary<string, object> { { "code", -32601 }, { "message", "Capri doesn't do " + method + "." } };
                try { Send(answer); } catch (Exception) { }
            }
        }

        private void Fail(string why)
        {
            List<AppReply> stuck;
            lock (gate) { stuck = new List<AppReply>(waiting.Values); waiting.Clear(); }
            foreach (AppReply reply in stuck) reply.Done.Set();
            if (Problem == null && Tools == null) Problem = why;
        }

        private void Send(Dictionary<string, object> message)
        {
            string text = AppJson.Write(message);
            lock (gate)
            {
                if (input == null) throw new IOException("it isn't running");
                input.WriteLine(text);
            }
        }

        internal Dictionary<string, object> Request(string method, Dictionary<string, object> parameters, int wait)
        {
            int id;
            lock (gate) id = ++nextId;
            Dictionary<string, object> message = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "method", method }, { "params", parameters ?? new Dictionary<string, object>() } };
            Dictionary<string, object> reply;
            if (url != null) reply = Post(message, id, wait);
            else
            {
                AppReply pending = new AppReply();
                lock (gate) waiting[id] = pending;
                Send(message);
                if (!pending.Done.WaitOne(wait)) { lock (gate) waiting.Remove(id); throw new TimeoutException(method + " took longer than " + wait / 1000 + " seconds (it printed " + heard + " lines)"); }
                reply = pending.Message;
                if (reply == null) throw new IOException(Problem ?? "it stopped answering");
            }
            Dictionary<string, object> error = AppJson.Obj(reply, "error");
            if (error != null) throw new InvalidOperationException(AppJson.Str(error, "message") ?? "it sent back an error");
            Used = DateTime.UtcNow;
            return AppJson.Obj(reply, "result") ?? new Dictionary<string, object>();
        }

        private void Notify(string method)
        {
            Dictionary<string, object> message = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "method", method } };
            if (url != null) { try { Post(message, -1, 30000); } catch (Exception) { } return; }
            Send(message);
        }

        // The HTTP way: each message is a POST, and the answer comes back as JSON or as a stream of events.
        private Dictionary<string, object> Post(Dictionary<string, object> message, int id, int wait)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Filled(url));
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            request.Method = "POST";
            request.Proxy = null;
            request.Timeout = wait;
            request.ReadWriteTimeout = wait;
            request.UserAgent = "Capri-Desktop";
            request.ContentType = "application/json";
            request.Accept = "application/json, text/event-stream";
            if (session != null) request.Headers["Mcp-Session-Id"] = session;
            if (id < 0 || AppJson.Str(message, "method") != "initialize") request.Headers["MCP-Protocol-Version"] = Protocol;
            foreach (KeyValuePair<string, string> pair in headers) request.Headers[pair.Key] = Filled(pair.Value);
            byte[] body = Encoding.UTF8.GetBytes(AppJson.Write(message));
            using (Stream sending = request.GetRequestStream()) sending.Write(body, 0, body.Length);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                string given = response.Headers["Mcp-Session-Id"];
                if (!string.IsNullOrEmpty(given)) session = given;
                if (id < 0) return null;
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    if ((response.ContentType ?? "").IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) < 0) return AppJson.Object(reader.ReadToEnd());
                    StringBuilder data = new StringBuilder();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("data:", StringComparison.Ordinal)) { data.Append(line.Substring(5).TrimStart()); continue; }
                        if (line.Length > 0 || data.Length == 0) continue;
                        Dictionary<string, object> found = null;
                        try { found = AppJson.Object(data.ToString()); } catch (FormatException) { }
                        data.Length = 0;
                        if (found == null) continue;
                        object got;
                        if (found.TryGetValue("id", out got) && got is double && AppJson.Str(found, "method") == null && (int)(double)got == id) return found;
                    }
                    if (data.Length > 0) return AppJson.Object(data.ToString());
                    throw new IOException("its answer never came");
                }
            }
        }

        internal string Call(string tool, Dictionary<string, object> arguments, int wait)
        {
            Dictionary<string, object> result = Request("tools/call", new Dictionary<string, object> { { "name", tool }, { "arguments", arguments ?? new Dictionary<string, object>() } }, wait);
            StringBuilder text = new StringBuilder();
            List<object> content = AppJson.List(result, "content");
            if (content != null)
                foreach (object item in content)
                {
                    Dictionary<string, object> part = item as Dictionary<string, object>;
                    string kind = AppJson.Str(part, "type");
                    if (kind == "text") text.AppendLine(AppJson.Str(part, "text"));
                    else if (kind == "image") text.AppendLine("(a picture)");
                    else if (kind == "resource") { Dictionary<string, object> resource = AppJson.Obj(part, "resource"); text.AppendLine(AppJson.Str(resource, "text") ?? "(a file: " + (AppJson.Str(resource, "uri") ?? "?") + ")"); }
                    else if (kind == "resource_link") text.AppendLine("(a link: " + (AppJson.Str(part, "uri") ?? "?") + ")");
                }
            Dictionary<string, object> structured = AppJson.Obj(result, "structuredContent");
            if (text.Length == 0 && structured != null) text.Append(AppJson.Write(structured));
            object failed;
            bool error = result.TryGetValue("isError", out failed) && failed is bool && (bool)failed;
            string said = text.ToString().Trim();
            return (error ? "The tool reported an error: " : "") + (said.Length == 0 ? "(no output)" : said);
        }

        internal void Stop()
        {
            Process running;
            lock (gate) { running = process; process = null; input = null; session = null; }
            if (running == null) return;
            try
            {
                if (!running.HasExited && System.IO.Path.DirectorySeparatorChar != '\\') running.Kill();
                else if (!running.HasExited)
                    using (Process killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/T /F /PID " + running.Id) { UseShellExecute = false, CreateNoWindow = true })) killer.WaitForExit(5000);
            }
            catch (Exception) { try { running.Kill(); } catch (Exception) { } }
            finally { try { running.Dispose(); } catch (Exception) { } }
        }
    }

    // All their apps: read from apps.json, started on demand, and stopped after a quiet spell or when she quits.
    internal sealed class Apps
    {
        internal readonly string Path;
        internal Func<string, string> Fill;
        private readonly object gate = new object();
        private Dictionary<string, AppServer> servers = new Dictionary<string, AppServer>();
        private DateTime loadedStamp = DateTime.MinValue;
        private readonly Timer idle;
        internal const int IdleMinutes = 15;

        // Ready-made apps they can add with one click from the jobs board.
        internal static readonly string[,] Presets =
        {
            { "browser", "A real web browser (Playwright): she can open sites, click, type and fill in forms. Needs Node.js.", "{\"command\":\"npx\",\"args\":[\"-y\",\"@playwright/mcp@latest\"]}" },
            { "github", "GitHub: her crew reads and works in your repos, issues and pull requests. Save a GitHub token in Keys as GITHUB_TOKEN first.", "{\"url\":\"https://api.githubcopilot.com/mcp/\",\"headers\":{\"Authorization\":\"Bearer ${GITHUB_TOKEN}\"}}" },
            { "memory", "A knowledge graph her crew can keep notes in between jobs. Needs Node.js.", "{\"command\":\"npx\",\"args\":[\"-y\",\"@modelcontextprotocol/server-memory\"]}" }
        };

        internal Apps(string data)
        {
            Path = System.IO.Path.Combine(data, "apps.json");
            idle = new Timer(delegate { StopIdle(); }, null, 60000, 60000);
        }

        private const string Starter = "{\n  \"mcpServers\": {\n  }\n}\n";

        // apps.json in Claude Desktop's format: {"mcpServers": {"name": {"command": ..., "args": [...], "env": {...}}, "other": {"url": ..., "headers": {...}}}}
        private Dictionary<string, AppServer> Load(out string problem)
        {
            problem = null;
            lock (gate)
            {
                DateTime stamp;
                try { stamp = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue; }
                catch (Exception) { stamp = DateTime.MinValue; }
                if (stamp == loadedStamp) return servers;
                foreach (AppServer old in servers.Values) old.Stop();
                servers = new Dictionary<string, AppServer>();
                loadedStamp = stamp;
                if (stamp == DateTime.MinValue) return servers;
                try
                {
                    Dictionary<string, object> file = AppJson.Object(File.ReadAllText(Path));
                    Dictionary<string, object> listed = AppJson.Obj(file, "mcpServers") ?? AppJson.Obj(file, "servers") ?? new Dictionary<string, object>();
                    foreach (KeyValuePair<string, object> pair in listed)
                    {
                        Dictionary<string, object> spec = pair.Value as Dictionary<string, object>;
                        if (spec == null) continue;
                        object off;
                        if ((spec.TryGetValue("disabled", out off) && off is bool && (bool)off) || (spec.TryGetValue("enabled", out off) && off is bool && !(bool)off)) continue;
                        servers[Safe(pair.Key)] = new AppServer(Safe(pair.Key), spec, Fill);
                    }
                }
                catch (Exception error) { problem = "apps.json has a mistake in it: " + error.Message; }
                return servers;
            }
        }

        internal static string Safe(string name)
        {
            string safe = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');
            return safe.Length == 0 ? "app" : safe.Length > 24 ? safe.Substring(0, 24) : safe;
        }

        internal List<string> Names
        {
            get { string problem; return new List<string>(Load(out problem).Keys); }
        }

        internal bool Any { get { return Names.Count > 0; } }

        // Starts every app (the first npx start can take a minute while it downloads), and says which ones didn't come up.
        internal List<AppTool> Ready(Action<string> note)
        {
            string problem;
            Dictionary<string, AppServer> all = Load(out problem);
            if (problem != null && note != null) note(problem);
            List<AppTool> tools = new List<AppTool>();
            HashSet<string> taken = new HashSet<string>();
            foreach (AppServer server in all.Values)
            {
                if (!server.Running) { if (note != null) note("Starting " + server.Name + "..."); server.Start(150000); }
                if (server.Problem != null) { if (note != null) note(server.Name + " didn't start: " + server.Problem); continue; }
                if (server.Tools == null) continue;
                foreach (AppTool tool in server.Tools)
                {
                    string exposed = server.Name.Replace('-', '_') + "__" + Regex.Replace(tool.Name, @"[^A-Za-z0-9_-]", "_");
                    if (exposed.Length > 64) exposed = exposed.Substring(0, 64);
                    while (taken.Contains(exposed)) exposed = exposed.Substring(0, Math.Min(exposed.Length, 60)) + "_" + taken.Count;
                    taken.Add(exposed);
                    tool.Exposed = exposed;
                    tools.Add(tool);
                }
            }
            return tools;
        }

        internal string Call(AppTool tool, string raw, int wait)
        {
            string problem;
            AppServer server;
            if (!Load(out problem).TryGetValue(tool.App, out server)) return "That app isn't connected any more.";
            Dictionary<string, object> arguments;
            try { arguments = string.IsNullOrWhiteSpace(raw) ? new Dictionary<string, object>() : AppJson.Object(raw) ?? new Dictionary<string, object>(); }
            catch (FormatException error) { return "Those arguments aren't valid JSON: " + error.Message; }
            try
            {
                if (!server.Running) server.Start(150000);
                if (server.Problem != null) return server.Name + " isn't running: " + server.Problem;
                return server.Call(tool.Name, arguments, wait);
            }
            catch (Exception error) { return tool.Exposed + " failed: " + error.Message; }
        }

        // One click on the jobs board: put a ready-made app in apps.json (keeping whatever is there).
        internal string Add(string preset)
        {
            for (int i = 0; i < Presets.GetLength(0); i++)
            {
                if (Presets[i, 0] != preset) continue;
                lock (gate)
                {
                    try
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                        Dictionary<string, object> file = File.Exists(Path) ? AppJson.Object(File.ReadAllText(Path)) ?? new Dictionary<string, object>() : new Dictionary<string, object>();
                        Dictionary<string, object> listed = AppJson.Obj(file, "mcpServers");
                        if (listed == null) { listed = new Dictionary<string, object>(); file["mcpServers"] = listed; }
                        listed[preset] = AppJson.Object(Presets[i, 2]);
                        File.WriteAllText(Path, AppJson.Write(file), new UTF8Encoding(false));
                        return "Added " + preset + ". " + Presets[i, 1];
                    }
                    catch (Exception error) { return "Couldn't add " + preset + ": " + error.Message; }
                }
            }
            return "There's no ready-made app called " + preset + ".";
        }

        internal void Open()
        {
            try
            {
                if (!File.Exists(Path)) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)); File.WriteAllText(Path, Starter, new UTF8Encoding(false)); }
                Process.Start("notepad.exe", "\"" + Path + "\"");
            }
            catch (Exception) { }
        }

        private void StopIdle()
        {
            List<AppServer> all;
            lock (gate) all = new List<AppServer>(servers.Values);
            foreach (AppServer server in all) if (server.Running && DateTime.UtcNow - server.Used > TimeSpan.FromMinutes(IdleMinutes)) server.Stop();
        }

        internal void StopAll()
        {
            List<AppServer> all;
            lock (gate) all = new List<AppServer>(servers.Values);
            foreach (AppServer server in all) server.Stop();
        }
    }
}

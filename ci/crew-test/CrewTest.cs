// Runs a real terminal job through Windows PowerShell with a scripted brain: commands, output, blocking, timeouts, files, exit codes and stopping.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
namespace CapriDesktop
{
    internal static class CrewTest
    {
        static int fails;
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        static KeyValuePair<string, ToolArguments> Call(string tool, ToolArguments args) { return new KeyValuePair<string, ToolArguments>(tool, args); }

        static Gig Wait(Crew crew, int index, int seconds)
        {
            Gig gig = null;
            for (int i = 0; i < seconds * 10; i++) { List<Gig> all = crew.All; gig = all.Count > index ? all[index] : null; if (gig != null && gig.Finished) break; Thread.Sleep(100); }
            return gig;
        }

        // A stand-in MCP server: this same program, started with --fake-mcp, answering JSON-RPC lines on stdin. It pings the client once, like real servers can.
        static int FakeServer()
        {
            string line;
            Console.Error.WriteLine("fake: started");
            while ((line = Console.In.ReadLine()) != null)
            {
                Console.Error.WriteLine("fake: got " + line.Length + " chars");
                Dictionary<string, object> m;
                try { m = AppJson.Object(line); } catch (FormatException) { Console.Error.WriteLine("fake: skipped a line that isn't JSON (" + line.Length + " chars)"); continue; }
                object id;
                m.TryGetValue("id", out id);
                string method = AppJson.Str(m, "method");
                if (id == null || method == null) continue;
                Dictionary<string, object> r = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id } };
                if (method == "initialize")
                {
                    Console.Out.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":\"srv-1\",\"method\":\"ping\"}");
                    r["result"] = new Dictionary<string, object> { { "protocolVersion", "2025-06-18" }, { "capabilities", new Dictionary<string, object>() }, { "serverInfo", new Dictionary<string, object> { { "name", "fake" }, { "version", "1" } } } };
                }
                else if (method == "tools/list") r["result"] = AppJson.Object("{\"tools\":[{\"name\":\"echo\",\"description\":\"Echo text back\",\"inputSchema\":{\"$schema\":\"x\",\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"},\"times\":{\"type\":\"number\"}},\"required\":[\"text\"]}}]}");
                else if (method == "tools/call")
                {
                    Dictionary<string, object> args = AppJson.Obj(AppJson.Obj(m, "params"), "arguments");
                    string said = "echo: " + AppJson.Str(args, "text") + " x" + AppJson.Write(args["times"]) + " token " + Environment.GetEnvironmentVariable("FAKE_TOKEN");
                    r["result"] = new Dictionary<string, object> { { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", said } } } } };
                }
                else r["error"] = new Dictionary<string, object> { { "code", -32601 }, { "message", "nope" } };
                Console.Out.WriteLine(AppJson.Write(r));
                Console.Out.Flush();
                Console.Error.WriteLine("fake: answered " + method);
            }
            return 0;
        }

        // A stand-in coding brain on this PC: it answers OpenAI-style streams, and its first tool call has no id, the way some real servers send it.
        static void FakeCoder(System.Net.Sockets.TcpListener listener, string file)
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try { client = listener.AcceptTcpClient(); } catch (Exception) { return; }
                using (client)
                {
                    try
                    {
                        Stream stream = client.GetStream();
                        StringBuilder head = new StringBuilder();
                        while (!head.ToString().EndsWith("\r\n\r\n")) { int b = stream.ReadByte(); if (b < 0) break; head.Append((char)b); }
                        System.Text.RegularExpressions.Match length = System.Text.RegularExpressions.Regex.Match(head.ToString(), @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        byte[] body = new byte[length.Success ? int.Parse(length.Groups[1].Value) : 0];
                        int got = 0;
                        while (got < body.Length) { int n = stream.Read(body, got, body.Length - got); if (n <= 0) break; got += n; }
                        string asked = Encoding.UTF8.GetString(body);
                        List<string> events = new List<string>();
                        string chunk = "{\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fake\",\"choices\":[";
                        if (!asked.Contains("\"tools\"")) events.Add(chunk + "{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Fake title\"},\"finish_reason\":\"stop\"}]}");
                        else if (!asked.Contains("\"role\":\"tool\""))
                        {
                            events.Add(chunk + "{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":null,\"type\":\"function\",\"function\":{\"name\":\"write\",\"arguments\":\"\"}}]},\"finish_reason\":null}]}");
                            events.Add(chunk + "{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":null,\"function\":{\"arguments\":" + AppJson.Write(AppJson.Write(new Dictionary<string, object> { { "filePath", file }, { "content", "print('hello from capri')\n" } })) + "}}]},\"finish_reason\":null}]}");
                            events.Add(chunk + "{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}");
                        }
                        else events.Add(chunk + "{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Wrote hello.py, it prints a greeting.\"},\"finish_reason\":\"stop\"}]}");
                        StringBuilder reply = new StringBuilder("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");
                        foreach (string e in events) reply.Append("data: " + e + "\n\n");
                        reply.Append("data: [DONE]\n\n");
                        byte[] bytes = Encoding.UTF8.GetBytes(reply.ToString());
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    catch (Exception) { }
                }
            }
        }

        static string Run(string folder, string file, string args)
        {
            System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = folder };
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(start)) { string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); return output; }
        }

        static int Main(string[] given)
        {
            if (given.Length > 0 && given[0] == "--fake-mcp") return FakeServer();
            Console.Out.Flush();
            string data = Path.Combine(Path.GetTempPath(), "capri-crew-" + Guid.NewGuid().ToString("N"));
            Crew crew = new Crew(data);
            Brain worker = new Brain();
            worker.Consults.Enqueue("{\"title\":\"Shell test\",\"worker\":\"terminal\",\"plan\":\"Testing the terminal.\"}");
            worker.Consults.Enqueue("{\"verdict\":\"pass\",\"notes\":\"\",\"report\":\"Terminal works.\"}");
            worker.Steps.Enqueue("run_powershell{\"text\": \"'hi there' | Set-Content hello.txt; Get-Content hello.txt; 'ps' + $PSVersionTable.PSVersion.Major\"}");
            worker.Steps.Enqueue(Call("run_powershell", new ToolArguments { Text = "Format-Volume -DriveLetter Z" }));
            worker.Steps.Enqueue(Call("run_powershell", new ToolArguments { Text = "Start-Sleep 30; 'late'", Seconds = "10" }));
            worker.Steps.Enqueue(Call("read_file", new ToolArguments { Name = "hello.txt" }));
            worker.Steps.Enqueue(Call("write_file", new ToolArguments { Name = "sub\\out.txt", Text = "written" }));
            worker.Steps.Enqueue(Call("run_powershell", new ToolArguments { Text = "cmd /c exit 3" }));
            worker.Steps.Enqueue(Call("run_powershell", new ToolArguments { Text = "Write-Error 'oops'; 'after error'" }));
            worker.Steps.Enqueue("finish{\"action\":\"done\",\"text\":\"all good\"}");
            List<string> said = new List<string>();
            crew.Hire = delegate { return worker; };
            crew.Speak = delegate(string line) { lock (said) said.Add(line); };
            Check(crew.Handle("start_job", new ToolArguments { Task = "test the terminal", Kind = "terminal" }).StartsWith("Started job 1"), "job starts");
            Gig gig = Wait(crew, 0, 90);
            string[] log = crew.LogLines(gig);
            foreach (string line in log) Console.WriteLine("  | " + line);
            string all = string.Join("\n", log);
            Check(gig.Status == "done", "job done (" + gig.Status + ")");
            Check(all.Contains("exit 0: hi there") && all.Contains("ps5"), "runs PowerShell 5 in the job folder and reads its output");
            Check(all.Contains("Blocked: Format-Volume"), "blocks a disk wipe");
            Check(all.Contains("stopped after 10 seconds"), "stops a command that runs too long");
            Check(File.Exists(Path.Combine(gig.Folder, "sub\\out.txt")) && File.ReadAllText(Path.Combine(gig.Folder, "sub\\out.txt")) == "written", "writes files in the job folder");
            Check(all.Contains("exit 3"), "keeps exit codes");
            Check(all.Contains("oops") && all.Contains("after error"), "keeps errors and carries on");
            Check(worker.Seen.Exists(s => s.Contains("hi there")), "the worker sees command output");
            Check(said.Count == 2 && said[1] == "Terminal works.", "plan and report spoken");

            Brain slow = new Brain();
            slow.Consults.Enqueue("{\"title\":\"Long one\",\"worker\":\"terminal\",\"plan\":\"Waiting around.\"}");
            slow.Steps.Enqueue(Call("run_powershell", new ToolArguments { Text = "Start-Sleep 120", Seconds = "300" }));
            crew.Hire = delegate { return slow; };
            crew.Handle("start_job", new ToolArguments { Task = "wait a long time", Kind = "terminal" });
            Thread.Sleep(4000);
            Check(crew.Handle("jobs", new ToolArguments { Action = "stop", Name = "2" }).StartsWith("Stopping job 2"), "stop finds the running job");
            Gig stopped = Wait(crew, 1, 20);
            Check(stopped.Status == "stopped", "a stopped job ends at once (" + stopped.Status + ")");
            // The key vault: a saved key reaches the command as an environment variable, and never shows up in what the worker sees, the log or the result.
            Brain keyed = new Brain();
            keyed.Consults.Enqueue("{\"title\":\"Key test\",\"worker\":\"terminal\",\"plan\":\"Using the vault.\"}");
            keyed.Consults.Enqueue("{\"verdict\":\"pass\",\"notes\":\"\",\"report\":\"Keys work.\"}");
            keyed.Steps.Enqueue(Call("run_powershell", new ToolArguments { Text = "'len ' + $env:TEST_VAULT_KEY.Length; 'key ' + $env:TEST_VAULT_KEY" }));
            keyed.Steps.Enqueue("finish{\"action\":\"done\",\"text\":\"printed it\"}");
            crew.Hire = delegate { return keyed; };
            crew.Keys = delegate { return new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("TEST_VAULT_KEY", "sk-supersecret-123456") }; };
            crew.Handle("start_job", new ToolArguments { Task = "use my key", Kind = "terminal" });
            Gig keyedGig = Wait(crew, 2, 60);
            string keyedLog = string.Join("\n", crew.LogLines(keyedGig));
            string keyedSeen = string.Join("\n", keyed.Seen.ToArray());
            Check(keyedLog.Contains("len 21"), "a saved key reaches the command");
            Check(!keyedLog.Contains("supersecret") && !keyedSeen.Contains("supersecret") && keyedSeen.Contains("[TEST_VAULT_KEY hidden]"), "a saved key never shows in the log or to the worker");
            Check(keyedSeen.Contains("$env:TEST_VAULT_KEY"), "the worker is told the key's name");
            // The app plug: a stand-in MCP server (PowerShell speaking JSON-RPC over stdin and stdout) is listed in apps.json,
            // started by an apps job, asked for its tools, and called with arguments that aren't plain strings; a ${KEY} in its settings comes from the vault and stays hidden.
            File.WriteAllText(Path.Combine(data, "apps.json"), "{\"mcpServers\":{\"fake\":{\"command\":" + Voice.Quote(System.Reflection.Assembly.GetExecutingAssembly().Location) + ",\"args\":[\"--fake-mcp\"],\"env\":{\"FAKE_TOKEN\":\"${TEST_VAULT_KEY}\"}},\"viacmd\":{\"command\":\"cmd\",\"args\":[\"/c\"," + Voice.Quote(System.Reflection.Assembly.GetExecutingAssembly().Location) + ",\"--fake-mcp\"]},\"off\":{\"command\":\"nothing\",\"disabled\":true}}}");
            Check(crew.Apps.Names.Count == 2 && crew.Apps.Names[0] == "fake", "apps.json lists the apps and skips a disabled one");
            Brain appsBrain = new Brain();
            appsBrain.Consults.Enqueue("{\"title\":\"App test\",\"worker\":\"apps\",\"plan\":\"Using the fake app.\"}");
            appsBrain.Consults.Enqueue("{\"verdict\":\"pass\",\"notes\":\"\",\"report\":\"Apps work.\"}");
            appsBrain.Steps.Enqueue(Call("fake__echo", new ToolArguments { Raw = "{\"text\":\"hi\",\"times\":2}" }));
            appsBrain.Steps.Enqueue("finish{\"action\":\"done\",\"text\":\"echoed\"}");
            crew.Hire = delegate { return appsBrain; };
            crew.Handle("start_job", new ToolArguments { Task = "echo something with the fake app", Kind = "apps" });
            Gig appsGig = Wait(crew, 3, 200);
            string appsLog = string.Join("\n", crew.LogLines(appsGig));
            foreach (string line in crew.LogLines(appsGig)) Console.WriteLine("  | " + line);
            Check(appsGig.Worker == "apps" && appsGig.Status == "done", "an apps job runs on the apps worker (" + appsGig.Worker + ", " + appsGig.Status + ")");
            Check(appsLog.Contains("Apps ready: 2 tools from fake, viacmd"), "both apps start, by themselves and through cmd, and list their tools: " + appsLog.Replace("\n", " / "));
            Check(appsLog.Contains("echo: hi x2"), "an app tool gets its arguments whole, numbers included, and its answer comes back");
            Check(appsLog.Contains("token [TEST_VAULT_KEY hidden]") && !appsLog.Contains("supersecret"), "a ${KEY} in apps.json comes from the vault and its value stays hidden");
            // Her coding crew: a coding job in a real repo goes on its own branch, OpenCode codes through her switchboard (which repairs the missing tool id),
            // the reviewer sees the real diff, a pass becomes one commit, and OpenCode's session is kept for the next job there.
            Check(Crew.HasOpenCode, "OpenCode is installed for the coding test");
            if (Crew.HasOpenCode)
            {
                string repo = Path.Combine(data, "repo");
                Directory.CreateDirectory(repo);
                File.WriteAllText(Path.Combine(repo, "README.md"), "test repo\n");
                Run(repo, "git", "init -q");
                Run(repo, "git", "add -A");
                Run(repo, "git", "-c user.name=t -c user.email=t@t commit -q -m start");
                System.Net.Sockets.TcpListener coderPort = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                coderPort.Start();
                int port = ((System.Net.IPEndPoint)coderPort.LocalEndpoint).Port;
                string hello = Path.Combine(repo, "hello.py");
                Thread coder = new Thread(delegate() { FakeCoder(coderPort, hello); }) { IsBackground = true };
                coder.Start();
                crew.HasCodeBrains = delegate { return true; };
                crew.Relay.Brains = delegate { return new List<RelayBrain> { new RelayBrain { Name = "fake", Api = "http://127.0.0.1:" + port + "/v1", Token = "t", Model = "fake-coder" } }; };
                Brain codeBrain = new Brain();
                codeBrain.Consults.Enqueue("{\"title\":\"Hello script\",\"worker\":\"builder\",\"plan\":\"Coding it.\"}");
                codeBrain.Consults.Enqueue("Goal: add hello.py that prints a greeting.");
                codeBrain.Consults.Enqueue("{\"verdict\":\"pass\",\"notes\":\"\",\"report\":\"Wrote it.\"}");
                crew.Hire = delegate { return codeBrain; };
                crew.Handle("start_job", new ToolArguments { Task = "write a hello script in my repo", Kind = "builder", Target = repo });
                Gig codeGig = Wait(crew, 4, 300);
                string codeLog = string.Join("\n", crew.LogLines(codeGig));
                foreach (string line in crew.LogLines(codeGig)) Console.WriteLine("  | " + line);
                Check(codeGig.Worker == "builder" && codeGig.Status == "done", "a coding job runs and passes (" + codeGig.Worker + ", " + codeGig.Status + ")");
                Check(codeLog.Contains("Working on a new branch, capri/"), "it works on its own branch");
                Check(File.Exists(hello) && File.ReadAllText(hello).Contains("hello from capri"), "OpenCode wrote the file through her switchboard, with the tool id repaired: " + codeLog.Replace("\n", " / "));
                Check(codeBrain.Seen.Exists(x => x.Contains("The code changes (git diff)") && x.Contains("hello.py")), "the reviewer sees the real diff");
                Check(Run(repo, "git", "log --oneline -1").Contains("capri:") && Run(repo, "git", "branch --show-current").Trim().StartsWith("capri/"), "a passed job is one commit on her branch");
                Check(codeGig.Session != null && File.Exists(Path.Combine(data, "code-sessions.txt")) && File.ReadAllText(Path.Combine(data, "code-sessions.txt")).Contains(codeGig.Session), "OpenCode's session is kept for the next job in that repo");
                coderPort.Stop();
            }
            crew.Shutdown();
            Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
            return fails;
        }
    }
}

// Runs a real terminal job through Windows PowerShell with a scripted brain: commands, output, blocking, timeouts, files, exit codes and stopping.
using System;
using System.Collections.Generic;
using System.IO;
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
                try { m = AppJson.Object(line); } catch (FormatException) { continue; }
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
            }
            return 0;
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
            crew.Shutdown();
            Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
            return fails;
        }
    }
}

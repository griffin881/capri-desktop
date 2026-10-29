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

        static int Main()
        {
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
            Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
            return fails;
        }
    }
}

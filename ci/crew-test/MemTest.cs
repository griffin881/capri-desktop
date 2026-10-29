// Her journal, diary digest and orbits, with a scripted brain.
using System;
using System.Collections.Generic;
using System.IO;
namespace CapriDesktop
{
    internal static class MemTest
    {
        static int fails;
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        static int Main()
        {
            string data = Path.Combine(Path.GetTempPath(), "capri-mem-" + Guid.NewGuid().ToString("N"));
            Journal.Folder = data;
            Orbits.FilePath = Path.Combine(data, "orbits.json");
            Check(Brain_Lately() == "", "nothing saved, nothing in her prompt");
            Journal.Add("them", "yo i finally got deepseek running on kaggle");
            Journal.Add("Capri", "Look at you, flatlining Arasaka's GPU budget.");
            Journal.Add("them", "next i gotta automate the tunnel link");
            Check(Journal.Pending == 3, "three journal lines pending");
            Brain b = new Brain();
            b.Consults.Enqueue("```json\n{\"summary\": \"Got DeepSeek running on Kaggle; wants to automate the tunnel link next.\", \"facts\": [\"Runs DeepSeek on a Kaggle notebook\", \"\", \"Wants the Kaggle tunnel link automated\"]}\n```");
            string sum = Journal.Digest(b);
            Check(sum != null && sum.StartsWith("Got DeepSeek"), "digest summarizes");
            Check(Journal.Pending == 0, "digested lines aren't digested again");
            Check(File.ReadAllText(Journal.DaysPath).Contains("- " + DateTime.Now.ToString("yyyy-MM-dd") + ": Got DeepSeek running"), "diary line written");
            Check(Brain.Notes.Count == 2, "lasting facts go to me.md (" + Brain.Notes.Count + ")");
            Check(Journal.Lately().Contains("Got DeepSeek running on Kaggle"), "diary reaches her prompt");
            Journal.Add("them", "one more");
            Check(Journal.Pending == 1, "new lines after a digest are pending");

            Check(Orbits.Handle(new ToolArguments { Action = "add", Name = "Kaggle DeepSeek", Text = "notebook runs" }).StartsWith("Put Kaggle DeepSeek in orbit"), "add an orbit");
            Orbits.Handle(new ToolArguments { Action = "add", Name = "aid verification" });
            Orbits.Handle(new ToolArguments { Action = "add", Name = "Capri jobs system" });
            Check(Orbits.Handle(new ToolArguments { Action = "note", Name = "kaggle", Text = "next: automate the tunnel link" }).StartsWith("Logged on Kaggle DeepSeek"), "check-in finds the orbit by a loose name");
            Check(Orbits.Handle(new ToolArguments { Action = "note", Text = "installed 0.9.0" }).StartsWith("Logged on Kaggle DeepSeek"), "nameless check-in goes to the latest orbit");
            Check(Orbits.Handle(new ToolArguments { Action = "done", Name = "aid verification", Text = "mom finished her part" }).StartsWith("Aid verification landed"), "land an orbit");
            string list = Orbits.List();
            Console.WriteLine(list);
            Check(list.Contains("Capri jobs system") && list.Contains("Next: automate the tunnel link") && !list.Contains("Aid verification"), "list shows active orbits with next steps");
            Check(Orbits.Context().Contains("Kaggle DeepSeek") && Orbits.Context().Contains("on purpose"), "orbits reach her prompt");
            Check(Orbits.Handle(new ToolArguments { Action = "note", Name = "a new thing", Text = "started it" }).StartsWith("Logged on A new thing"), "checking in on a new project starts it");
            // Long memory: an old day's talk comes back when they bring it up again; today's newest lines are left to the chat itself.
            Directory.CreateDirectory(Path.Combine(data, "journal"));
            File.WriteAllText(Path.Combine(data, "journal", "2026-09-02.md"), "14:00 them: rio and i settled the vanguard piece of the framework\n14:01 Capri: Finally. Now make it predict something.\n14:05 them: gonna eat\n");
            string old = Journal.Recall("what did rio and i decide about the vanguard", 5, 0);
            Console.WriteLine(old);
            Check(old.Contains("2026-09-02 14:00 them: rio and i settled the vanguard") && old.Contains("2026-09-02 14:01 Capri: Finally."), "an old exchange comes back with its date and her answer");
            Check(!old.Contains("gonna eat"), "unrelated old lines stay out");
            Check(Journal.Recall("the tunnel link on kaggle", 5, 0).Contains("automate the tunnel link") && Journal.Recall("the tunnel link on kaggle", 5, 40) == "", "today's newest lines are skipped when the chat already has them");
            Check(Journal.Recall("yeah okay lol", 5, 0) == "", "small talk recalls nothing");
            Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
            return fails;
        }
        static string Brain_Lately() { return Orbits.Context() + Journal.Lately(); }
    }
}

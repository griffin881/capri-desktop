// Stand-ins for the parts of Capri her crew and memory talk to, so they can be tested on their own.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
namespace CapriDesktop
{
    [DataContract]
    internal sealed class ToolArguments
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "site")] public string Site;
        [DataMember(Name = "search")] public string Search;
        [DataMember(Name = "action")] public string Action;
        [DataMember(Name = "kind")] public string Kind;
        [DataMember(Name = "task")] public string Task;
        [DataMember(Name = "target")] public string Target;
        [DataMember(Name = "text")] public string Text;
        [DataMember(Name = "seconds")] public string Seconds;
    }
    internal sealed class Cloud { internal string Model = "m", Kind = "custom", Company = "host", Name = "Your server"; }
    internal static class Voice
    {
        internal static string Quote(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\""; }
        internal static string Short(string s) { return s; }
    }
    internal static class Safety { internal static string Stops(string s) { return s.Contains("bomb") ? "Not doing that." : null; } }
    internal static class Hands
    {
        internal static string Hush(string t) { return t; }
        internal static ToolArguments Read(string json)
        {
            try { using (MemoryStream m = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (ToolArguments)new DataContractJsonSerializer(typeof(ToolArguments)).ReadObject(m); }
            catch (Exception) { return null; }
        }
    }
    internal sealed class Brain
    {
        internal volatile string Model = "qwen3.5-abliterated:9b";
        internal volatile Cloud Remote;
        internal Queue<string> Consults = new Queue<string>();
        internal Queue<object> Steps = new Queue<object>();
        internal List<string> Seen = new List<string>();
        internal static List<string> Notes = new List<string>();
        internal static string Note(string f) { if (Notes.Contains(f)) return "Already"; Notes.Add(f); return "Saved to my notes: " + f; }
        internal static void Log(string a, long b, string c) { Console.WriteLine("LOG " + a + " " + c); }
        internal static string Visible(string t) { return Regex.Replace(t ?? "", "<think>.*?</think>", "", RegexOptions.Singleline).Trim(); }
        internal string Consult(string system, string user, int limit) { Seen.Add("CONSULT " + user); return Consults.Count > 0 ? Consults.Dequeue() : ""; }
        internal string Step(string system, string user, string tools, string claudeTools, string listing, List<KeyValuePair<string, ToolArguments>> calls, int limit = 400)
        {
            Seen.Add("STEP " + user);
            object next = Steps.Count > 0 ? Steps.Dequeue() : "finish{\"action\":\"done\",\"text\":\"fallback\"}";
            if (next is KeyValuePair<string, ToolArguments>) { calls.Add((KeyValuePair<string, ToolArguments>)next); return ""; }
            return (string)next;
        }
    }
}

// Her switchboard for coding tools: OpenCode (or anything that talks the OpenAI chat format) talks to Capri on this PC,
// and she passes each request to the first coding brain that's up, falling through to the next when one is slow, busy or down.
// On the way back she repairs replies some servers get slightly wrong, like a tool call with no id, which OpenCode rejects
// ("Expected 'id' to be a string").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CapriDesktop
{
    // One brain the switchboard can send to: its API root, its key, and the model to ask for.
    internal sealed class RelayBrain
    {
        internal string Name, Api, Token, Model;
        internal bool Google;
    }

    internal sealed class Relay
    {
        private TcpListener listener;
        private Thread loop;
        internal int Port;
        internal readonly string Secret = Guid.NewGuid().ToString("N");
        // The brains, best first, asked fresh on every request so a saved key takes effect at once.
        internal Func<List<RelayBrain>> Brains;
        internal Action<string> Log;
        private readonly Dictionary<string, DateTime> resting = new Dictionary<string, DateTime>();
        internal string LastUsed;
        internal const string ModelName = "capri-code";

        internal string Address { get { return "http://127.0.0.1:" + Port + "/v1"; } }

        internal void Start()
        {
            lock (this)
            {
                if (listener != null) return;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                loop = new Thread(Accept) { IsBackground = true, Name = "Capri relay" };
                loop.Start();
            }
        }

        internal void Stop()
        {
            lock (this)
            {
                if (listener == null) return;
                try { listener.Stop(); } catch (Exception) { }
                listener = null;
            }
        }

        // OpenCode's settings for the switchboard, passed to it in OPENCODE_CONFIG_CONTENT.
        internal string OpenCodeConfig()
        {
            return "{\"$schema\":\"https://opencode.ai/config.json\",\"provider\":{\"capri\":{\"npm\":\"@ai-sdk/openai-compatible\",\"name\":\"Capri\",\"options\":{\"baseURL\":\"" + Address + "\",\"apiKey\":\"" + Secret + "\"},\"models\":{\"" + ModelName + "\":{\"name\":\"Capri's coding brain\",\"tool_call\":true,\"limit\":{\"context\":128000,\"output\":16000}}}}}}";
        }

        private void Accept()
        {
            while (true)
            {
                TcpListener here;
                lock (this) here = listener;
                if (here == null) return;
                TcpClient client;
                try { client = here.AcceptTcpClient(); }
                catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(delegate { Serve(client); });
            }
        }

        private void Note(string line) { Action<string> log = Log; if (log != null) try { log(line); } catch (Exception) { } }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    NetworkStream stream = client.GetStream();
                    string method, path;
                    Dictionary<string, string> headers;
                    byte[] body;
                    if (!ReadRequest(stream, out method, out path, out headers, out body)) return;
                    string auth;
                    if (!headers.TryGetValue("authorization", out auth) || auth.Trim() != "Bearer " + Secret) { Write(stream, 401, "application/json", "{\"error\":{\"message\":\"Not Capri's key.\"}}"); return; }
                    if (method == "GET" && path.EndsWith("/models", StringComparison.Ordinal)) { Write(stream, 200, "application/json", "{\"object\":\"list\",\"data\":[{\"id\":\"" + ModelName + "\",\"object\":\"model\",\"owned_by\":\"capri\"}]}"); return; }
                    if (method != "POST" || !path.EndsWith("/chat/completions", StringComparison.Ordinal)) { Write(stream, 404, "application/json", "{\"error\":{\"message\":\"Capri only relays chat completions.\"}}"); return; }
                    Forward(stream, Encoding.UTF8.GetString(body));
                }
                catch (Exception error) { Note("relay: " + error.Message); }
            }
        }

        private static bool ReadRequest(Stream stream, out string method, out string path, out Dictionary<string, string> headers, out byte[] body)
        {
            method = path = null;
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            body = new byte[0];
            string first = ReadLine(stream);
            if (string.IsNullOrEmpty(first)) return false;
            string[] parts = first.Split(' ');
            if (parts.Length < 2) return false;
            method = parts[0].ToUpperInvariant();
            path = parts[1].Split('?')[0];
            string line;
            while (!string.IsNullOrEmpty(line = ReadLine(stream)))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            string length, coding;
            MemoryStream all = new MemoryStream();
            if (headers.TryGetValue("transfer-encoding", out coding) && coding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                while (true)
                {
                    string size = ReadLine(stream);
                    if (size == null) break;
                    int n = int.Parse(size.Split(';')[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (n == 0) { ReadLine(stream); break; }
                    Copy(stream, all, n);
                    ReadLine(stream);
                }
            }
            else if (headers.TryGetValue("content-length", out length)) Copy(stream, all, int.Parse(length, CultureInfo.InvariantCulture));
            body = all.ToArray();
            return true;
        }

        private static void Copy(Stream from, Stream to, int count)
        {
            byte[] buffer = new byte[8192];
            while (count > 0)
            {
                int got = from.Read(buffer, 0, Math.Min(buffer.Length, count));
                if (got <= 0) throw new IOException("The request ended early.");
                to.Write(buffer, 0, got);
                count -= got;
            }
        }

        private static string ReadLine(Stream stream)
        {
            StringBuilder line = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return line.Length == 0 ? null : line.ToString();
                if (b == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)b);
            }
        }

        private static void Write(Stream stream, int status, string type, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "Error") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        // Free tiers cap requests per minute, so OpenCode's requests to each one are spaced out.
        private static readonly Dictionary<string, DateTime> nextSlot = new Dictionary<string, DateTime>();
        private static void Pace(string api)
        {
            Uri uri;
            string host = Uri.TryCreate(api, UriKind.Absolute, out uri) ? uri.Host.ToLowerInvariant() : "";
            int gap = host.EndsWith("cerebras.ai") || host.EndsWith("groq.com") ? 2200 : host.EndsWith("googleapis.com") ? 6500 : host.EndsWith("openrouter.ai") ? 3200 : host.EndsWith("nvidia.com") ? 1600 : 0;
            if (gap == 0) return;
            TimeSpan wait;
            lock (nextSlot)
            {
                DateTime now = DateTime.UtcNow, next;
                if (!nextSlot.TryGetValue(host, out next) || next < now) next = now;
                wait = next - now;
                nextSlot[host] = next.AddMilliseconds(gap);
            }
            if (wait > TimeSpan.Zero) Thread.Sleep(wait > TimeSpan.FromSeconds(20) ? TimeSpan.FromSeconds(20) : wait);
        }

        private bool Cold(RelayBrain brain)
        {
            lock (resting) { DateTime until; return resting.TryGetValue(brain.Name + "|" + brain.Model, out until) && until > DateTime.UtcNow; }
        }

        private void Rest(RelayBrain brain, double minutes) { lock (resting) resting[brain.Name + "|" + brain.Model] = DateTime.UtcNow.AddMinutes(minutes); }

        // Tries each brain in order; the first one that answers streams straight back, repaired on the way.
        private void Forward(Stream client, string request)
        {
            Dictionary<string, object> asked;
            try { asked = AppJson.Object(request); }
            catch (FormatException) { Write(client, 400, "application/json", "{\"error\":{\"message\":\"That request isn't JSON.\"}}"); return; }
            List<RelayBrain> brains = Brains == null ? new List<RelayBrain>() : Brains();
            List<RelayBrain> order = new List<RelayBrain>();
            foreach (RelayBrain brain in brains) if (!Cold(brain)) order.Add(brain);
            if (order.Count == 0) order.AddRange(brains);
            string problem = "No coding brain is set up.";
            foreach (RelayBrain brain in order)
            {
                bool trimmed = false;
            again:
                asked["model"] = brain.Model;
                // Fields these servers trip over: the newer token limit name and stream options some don't take.
                object limit;
                if (asked.TryGetValue("max_completion_tokens", out limit) && !asked.ContainsKey("max_tokens")) { asked["max_tokens"] = limit; asked.Remove("max_completion_tokens"); }
                Pace(brain.Api);
                HttpWebRequest outgoing = (HttpWebRequest)WebRequest.Create(brain.Api + "/chat/completions");
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                outgoing.Method = "POST";
                outgoing.Proxy = null;
                outgoing.Timeout = 120000;
                outgoing.ReadWriteTimeout = 300000;
                outgoing.UserAgent = "Capri-Desktop";
                outgoing.ContentType = "application/json";
                if (!string.IsNullOrEmpty(brain.Token)) { if (brain.Google) outgoing.Headers["x-goog-api-key"] = brain.Token; else outgoing.Headers["Authorization"] = "Bearer " + brain.Token; }
                byte[] body = Encoding.UTF8.GetBytes(AppJson.Write(asked));
                HttpWebResponse response;
                try
                {
                    using (Stream sending = outgoing.GetRequestStream()) sending.Write(body, 0, body.Length);
                    response = (HttpWebResponse)outgoing.GetResponse();
                }
                catch (WebException error)
                {
                    HttpWebResponse failed = error.Response as HttpWebResponse;
                    int status = failed == null ? 0 : (int)failed.StatusCode;
                    string said = "";
                    if (failed != null) { try { using (StreamReader reader = new StreamReader(failed.GetResponseStream())) said = reader.ReadToEnd(); } catch (Exception) { } failed.Close(); }
                    problem = brain.Name + " " + brain.Model + ": " + (status == 0 ? error.Message : status + " " + Short(said));
                    // A request the brain itself rejects (400) would fail everywhere, so it goes back to OpenCode; anything else falls through.
                    if (status == 400 && !trimmed)
                    {
                        // Some servers reject optional fields they don't know: ask the same brain once more without them.
                        bool removed = false;
                        foreach (string extra in new[] { "stream_options", "parallel_tool_calls", "user", "store", "service_tier", "reasoning_effort", "metadata" }) if (asked.Remove(extra)) removed = true;
                        if (removed) { trimmed = true; Note(problem + ", asking again without the extras"); goto again; }
                    }
                    if (status == 400 || status == 413) { Note(problem); Write(client, status, "application/json", said.Length > 0 ? said : "{\"error\":{\"message\":" + AppJson.Write(problem) + "}}"); return; }
                    Rest(brain, status == 429 ? 1 : status == 401 || status == 403 || status == 404 ? 30 : 3);
                    Note(problem + ", trying the next brain");
                    continue;
                }
                bool last = brain == order[order.Count - 1];
                bool passed;
                using (response) passed = Pass(client, response, last);
                if (passed) { LastUsed = brain.Name + " " + brain.Model; return; }
                // It refused before doing anything: the next brain gets the same request, and this one sits out a while.
                problem = brain.Name + " " + brain.Model + " refused";
                Rest(brain, 15);
                Note(problem + ", trying the next brain");
            }
            Write(client, 503, "application/json", "{\"error\":{\"message\":" + AppJson.Write("Every coding brain failed. Last: " + problem) + "}}");
        }

        private static string Short(string text) { text = (text ?? "").Replace("\r", " ").Replace("\n", " "); return text.Length > 300 ? text.Substring(0, 300) : text; }

        // A reply that opens by turning the job down, with no tool call, is a refusal.
        private static readonly Regex Refusal = new Regex(@"^\W*(I('m| am) (sorry|unable|not able)|I can(no|')?t (help|assist|do|provide|create|write|comply|support)|I won'?t|Sorry,? (but )?I|I must (decline|refuse)|I('m| am) not (comfortable|going to)|As an AI|I apologi[sz]e, but)", RegexOptions.IgnoreCase);

        // Passes the reply back, repaired. When it isn't the last brain, the start is held back until it shows a tool call or enough words,
        // so a refusal can be dropped (false) before OpenCode sees any of it.
        private bool Pass(Stream client, HttpWebResponse response, bool last)
        {
            bool events = (response.ContentType ?? "").IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                if (!events)
                {
                    string text = reader.ReadToEnd();
                    if (!last && Refused(text)) return false;
                    Write(client, 200, "application/json", Repair(text, null));
                    return true;
                }
                Dictionary<string, string> ids = new Dictionary<string, string>();
                List<string> held = new List<string>();
                StringBuilder said = new StringBuilder();
                bool open = last;
                if (open) Head(client);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string output = line;
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        string data = line.Substring(5).Trim();
                        if (data.Length > 0 && data != "[DONE]")
                        {
                            output = "data: " + Repair(data, ids);
                            if (!open)
                            {
                                bool tools;
                                said.Append(Content(data, out tools));
                                if (tools || said.Length >= 240) { if (!tools && Refusal.IsMatch(said.ToString().Trim())) return false; open = true; Head(client); foreach (string kept in held) Send(client, kept); held.Clear(); }
                            }
                        }
                    }
                    if (open) { Send(client, output); if (line.Length == 0) client.Flush(); }
                    else held.Add(output);
                }
                if (!open)
                {
                    if (Refusal.IsMatch(said.ToString().Trim())) return false;
                    Head(client);
                    foreach (string kept in held) Send(client, kept);
                }
                client.Flush();
                return true;
            }
        }

        private static void Head(Stream client) { byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"); client.Write(head, 0, head.Length); }

        private static void Send(Stream client, string line) { byte[] bytes = Encoding.UTF8.GetBytes(line + "\n"); client.Write(bytes, 0, bytes.Length); }

        // The words in one streamed piece, and whether it carries a tool call.
        private static string Content(string data, out bool tools)
        {
            tools = false;
            try
            {
                Dictionary<string, object> piece = AppJson.Object(data);
                List<object> choices = AppJson.List(piece, "choices");
                if (choices == null || choices.Count == 0) return "";
                Dictionary<string, object> delta = AppJson.Obj(choices[0] as Dictionary<string, object>, "delta");
                tools = AppJson.List(delta, "tool_calls") != null;
                return AppJson.Str(delta, "content") ?? "";
            }
            catch (FormatException) { return ""; }
        }

        internal static bool Refused(string json)
        {
            try
            {
                Dictionary<string, object> reply = AppJson.Object(json);
                List<object> choices = AppJson.List(reply, "choices");
                Dictionary<string, object> message = choices == null || choices.Count == 0 ? null : AppJson.Obj(choices[0] as Dictionary<string, object>, "message");
                if (message == null || AppJson.List(message, "tool_calls") != null) return false;
                return Refusal.IsMatch((AppJson.Str(message, "content") ?? "").Trim());
            }
            catch (FormatException) { return false; }
        }

        // Every tool call gets a string id and the function type, the first time it shows up; reasoning some servers send as text in its own field stays where it is.
        internal static string Repair(string json, Dictionary<string, string> ids)
        {
            Dictionary<string, object> reply;
            try { reply = AppJson.Object(json); }
            catch (FormatException) { return json; }
            if (reply == null) return json;
            bool changed = false;
            List<object> choices = AppJson.List(reply, "choices");
            if (choices == null) return json;
            for (int c = 0; c < choices.Count; c++)
            {
                Dictionary<string, object> choice = choices[c] as Dictionary<string, object>;
                if (choice == null) continue;
                Dictionary<string, object> part = AppJson.Obj(choice, "delta") ?? AppJson.Obj(choice, "message");
                List<object> calls = AppJson.List(part, "tool_calls");
                if (calls == null) continue;
                for (int t = 0; t < calls.Count; t++)
                {
                    Dictionary<string, object> call = calls[t] as Dictionary<string, object>;
                    if (call == null) continue;
                    object index;
                    if (!call.TryGetValue("index", out index) || !(index is double)) { call["index"] = (double)t; index = (double)t; changed = true; }
                    string key = c + ":" + Convert.ToString(index, CultureInfo.InvariantCulture);
                    object id;
                    call.TryGetValue("id", out id);
                    string given = id as string;
                    if (id != null && given == null) { given = "call_" + Convert.ToString(id, CultureInfo.InvariantCulture); call["id"] = given; changed = true; }
                    if (ids == null)
                    {
                        // A whole reply: every call needs its id and type.
                        if (string.IsNullOrEmpty(given)) { call["id"] = "call_" + Guid.NewGuid().ToString("N").Substring(0, 12); changed = true; }
                        if (!call.ContainsKey("type")) { call["type"] = "function"; changed = true; }
                        continue;
                    }
                    // A stream: the first piece of each call needs its id and type, later pieces may leave them out.
                    if (!ids.ContainsKey(key))
                    {
                        if (string.IsNullOrEmpty(given)) { given = "call_" + Guid.NewGuid().ToString("N").Substring(0, 12); call["id"] = given; changed = true; }
                        ids[key] = given;
                        if (!call.ContainsKey("type") || call["type"] == null) { call["type"] = "function"; changed = true; }
                    }
                    else if (id == null && call.ContainsKey("id")) { call.Remove("id"); changed = true; }
                }
            }
            return changed ? AppJson.Write(reply) : json;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CapriDesktop
{
    // Real phone calls and texts. Calls go through her ElevenLabs agent and their Twilio number; texts go out from their own
    // number through the SMS Gateway app on their Android phone. She reads back who and what and waits for their yes before
    // anything is sent, says up front on a call that she's an AI calling for them, and reports back how a call went.
    // Their details and contacts live in phone.txt on this PC, filled in by them, never sent through a chat brain.
    internal static class Phone
    {
        internal static string SettingsPath;
        internal static string CachePath;
        internal static Func<string> Key = delegate { return null; };
        internal static Func<string> AgentId = delegate { return null; };
        internal static Action<string> Open = delegate { };
        internal static Action<string> Reported = delegate { };
        // method, path, JSON body (or null) -> response text. Tests swap it so nothing really dials.
        internal static Func<string, string, string, string> Http = Send;
        // url, JSON body, basic-auth user, password -> response text, for texts. Tests swap it so nothing really sends.
        internal static Func<string, string, string, string, string> Sms = Post;
        // Seconds a yes must come after she reads the call back, so a call is never placed in the same breath it's planned.
        internal static double ConfirmGap = 3;
        internal static int PollSeconds = 10;

        private static readonly object gate = new object();
        private static Pending pending;
        private static string lastConversation;
        private static string lastWho;

        private sealed class Pending
        {
            internal string To;
            internal string Who;
            internal string Why;
            internal bool Texting;
            internal DateTime At;
        }

        internal const string Template =
            "# Capri phone calls. Fill this in, save it, and close it. It stays on this PC.\r\n" +
            "# From your Twilio console (console.twilio.com): Account SID and Auth Token.\r\n" +
            "twilio_sid=\r\n" +
            "twilio_token=\r\n" +
            "# The number calls come from: a Twilio number, or your own cell if you verified it in Twilio as a caller ID. +1 format.\r\n" +
            "caller_number=\r\n" +
            "# Your name, so she can say who she's calling for.\r\n" +
            "my_name=\r\n" +
            "# Contacts, so you can say \"call Toby\". One per line: contact Name=+1XXXXXXXXXX\r\n" +
            "# On a Twilio free trial, each number you call has to be verified in Twilio first.\r\n" +
            "contact Toby=\r\n" +
            "# Texting from your own number: install SMS Gateway for Android on your phone (sms-gate.app), turn on its Cloud server,\r\n" +
            "# and copy the username and password from its Home tab here.\r\n" +
            "sms_username=\r\n" +
            "sms_password=\r\n" +
            "# Leave this for the cloud relay (works anywhere), or use http://<your phone's IP>:8080 for local mode on the same Wi-Fi.\r\n" +
            "sms_server=https://api.sms-gate.app\r\n";

        internal const string NotSetUp = "Calling isn't set up yet. I opened phone.txt: put in your Twilio SID, token and caller number, add your contacts, save it, and ask me again.";

        private sealed class Settings
        {
            internal string Sid, Token, Caller, Name, SmsUser, SmsPassword, SmsServer;
            internal readonly Dictionary<string, string> Contacts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal bool Ready { get { return !string.IsNullOrEmpty(Sid) && !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(Caller); } }
            internal bool Texts { get { return !string.IsNullOrEmpty(SmsUser) && !string.IsNullOrEmpty(SmsPassword); } }
        }

        private static Settings Read()
        {
            Settings found = new Settings();
            string path = SettingsPath;
            if (path == null || !File.Exists(path)) return found;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string name = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim().Trim('"');
                if (value.Length == 0) continue;
                Match contact = Regex.Match(name, @"^contact\s+(.+)$", RegexOptions.IgnoreCase);
                if (contact.Success) { string number = Number(value); if (number != null) found.Contacts[contact.Groups[1].Value.Trim()] = number; continue; }
                string key = name.ToLowerInvariant();
                if (key == "twilio_sid") found.Sid = value;
                else if (key == "twilio_token") found.Token = value;
                else if (key == "caller_number") found.Caller = Number(value);
                else if (key == "my_name") found.Name = value;
                else if (key == "sms_username") found.SmsUser = value;
                else if (key == "sms_password") found.SmsPassword = value;
                else if (key == "sms_server") found.SmsServer = value.TrimEnd('/');
            }
            return found;
        }

        // A phone number in the +15551234567 form calls need, or null if it isn't one.
        internal static string Number(string text)
        {
            string raw = (text ?? "").Trim();
            string digits = Regex.Replace(raw, @"\D", "");
            if (raw.StartsWith("+", StringComparison.Ordinal)) return digits.Length >= 8 && digits.Length <= 15 ? "+" + digits : null;
            if (digits.Length == 10) return "+1" + digits;
            if (digits.Length == 11 && digits[0] == '1') return "+" + digits;
            return null;
        }

        // How a number reads back: +1 (469) 555-0123.
        internal static string Shown(string number)
        {
            Match us = Regex.Match(number ?? "", @"^\+1(\d{3})(\d{3})(\d{4})$");
            return us.Success ? "+1 (" + us.Groups[1].Value + ") " + us.Groups[2].Value + "-" + us.Groups[3].Value : number;
        }

        internal static string Handle(ToolArguments args)
        {
            if (args == null) args = new ToolArguments();
            string action = (args.Action ?? "").Trim().ToLowerInvariant();
            try
            {
                if (action == "setup" || action == "set up" || action == "settings") return Setup();
                if (action == "confirm" || action == "yes" || action == "dial") return Confirm();
                if (action == "cancel" || action == "no") { lock (gate) pending = null; return "Okay, I won't call."; }
                if (action == "status" || action == "report") return Status();
                return Prepare(args.Name, args.Target, args.Text);
            }
            catch (Exception error) { return "Couldn't place that call: " + error.Message; }
        }

        private static string Setup()
        {
            string path = SettingsPath;
            if (path == null) return "I can't set up calling right now.";
            if (!File.Exists(path)) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, Template); }
            Open(path);
            return "I opened phone.txt. Put in your Twilio SID, token and caller number, add your contacts, save it, and you're set.";
        }

        // Who they mean and the number to reach them, from a number they gave or their contacts.
        private static string Reach(Settings settings, ref string name, string target)
        {
            string to = Number(target);
            if (to == null && name.Length > 0)
            {
                string saved;
                if (settings.Contacts.TryGetValue(name, out saved)) to = saved;
                else foreach (KeyValuePair<string, string> contact in settings.Contacts) if (contact.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.IndexOf(contact.Key, StringComparison.OrdinalIgnoreCase) >= 0) { to = contact.Value; name = contact.Key; break; }
            }
            if (to == null && Number(name) != null) { to = Number(name); name = ""; }
            return to;
        }

        internal const string NoTexts = "Texting isn't set up yet. I opened phone.txt: install SMS Gateway for Android on your phone, turn on its Cloud server, copy the username and password from its Home tab into phone.txt, save it, and ask me again.";

        internal static string Texting(ToolArguments args)
        {
            if (args == null) args = new ToolArguments();
            string action = (args.Action ?? "").Trim().ToLowerInvariant();
            try
            {
                if (action == "setup" || action == "set up" || action == "settings") return Setup();
                if (action == "confirm" || action == "yes" || action == "send") return Confirm();
                if (action == "cancel" || action == "no") { lock (gate) pending = null; return "Okay, I won't send it."; }
                Settings settings = Read();
                if (!settings.Texts) { Setup(); return NoTexts; }
                string name = (args.Name ?? "").Trim();
                string to = Reach(settings, ref name, args.Target);
                if (to == null) return (name.Length > 0 ? "I don't have a number for " + name + "." : "Who should I text?") + " Give me the number, or add them to phone.txt as contact " + (name.Length > 0 ? name : "Name") + "=+1XXXXXXXXXX.";
                string message = (args.Text ?? "").Trim();
                if (message.Length == 0) return "What should the text to " + (name.Length > 0 ? name : Shown(to)) + " say?";
                if (message.Length > 1500) return "That's too long for a text. Make it shorter.";
                lock (gate) pending = new Pending { To = to, Who = name.Length > 0 ? name : Shown(to), Why = message, Texting = true, At = DateTime.UtcNow };
                return "Ready to text " + (name.Length > 0 ? name + " at " : "") + Shown(to) + ": \"" + message + "\". Say yes and I'll send it.";
            }
            catch (Exception error) { return "Couldn't send that text: " + error.Message; }
        }

        private static string Text(Pending text)
        {
            Settings settings = Read();
            if (!settings.Texts) { Setup(); return NoTexts; }
            string server = string.IsNullOrEmpty(settings.SmsServer) ? "https://api.sms-gate.app" : settings.SmsServer;
            string body = "{\"textMessage\":{\"text\":" + Voice.Quote(text.Why) + "},\"phoneNumbers\":[" + Voice.Quote(text.To) + "]}";
            string reply = Sms(server + "/3rdparty/v1/messages", body, settings.SmsUser, settings.SmsPassword);
            if (Regex.IsMatch(reply ?? "", "\"(status|state)\"\\s*:\\s*\"failed\"", RegexOptions.IgnoreCase)) return "Your phone couldn't send the text to " + text.Who + ".";
            return "Sent to " + text.Who + ".";
        }

        private static string Post(string url, string body, string user, string password)
        {
            System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072;
            System.Net.HttpWebRequest request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            request.Method = "POST";
            request.UserAgent = "Capri-Desktop";
            request.ContentType = "application/json";
            request.Timeout = 20000;
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
            byte[] data = Encoding.UTF8.GetBytes(body);
            try
            {
                using (Stream upload = request.GetRequestStream()) upload.Write(data, 0, data.Length);
                using (System.Net.WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
            }
            catch (System.Net.WebException error)
            {
                System.Net.HttpWebResponse response = error.Response as System.Net.HttpWebResponse;
                if (response == null) throw new VoiceException("Can't reach your phone's SMS Gateway. Check that the app is running and online.");
                int status = (int)response.StatusCode;
                response.Close();
                if (status == 401) throw new VoiceException("SMS Gateway rejected the username or password in phone.txt.");
                throw new VoiceException("SMS Gateway error " + status + ".");
            }
        }

        private static string Prepare(string who, string target, string why)
        {
            Settings settings = Read();
            if (!settings.Ready) { Setup(); return NotSetUp; }
            string name = (who ?? "").Trim();
            string to = Reach(settings, ref name, target);
            if (to == null) return (name.Length > 0 ? "I don't have a number for " + name + "." : "Who should I call?") + " Give me the number, or add them to phone.txt as contact " + (name.Length > 0 ? name : "Name") + "=+1XXXXXXXXXX.";
            string purpose = (why ?? "").Trim();
            if (purpose.Length == 0) return "What should I call " + (name.Length > 0 ? name : Shown(to)) + " about?";
            lock (gate) pending = new Pending { To = to, Who = name.Length > 0 ? name : Shown(to), Why = purpose, At = DateTime.UtcNow };
            return "Ready to call " + (name.Length > 0 ? name + " at " : "") + Shown(to) + " about: " + purpose + ". I'll open by saying I'm an AI assistant calling for " + Owner(settings) + ". Say yes and I'll dial.";
        }

        private static string Owner(Settings settings) { return string.IsNullOrEmpty(settings.Name) ? "the person I work for" : settings.Name; }

        // A yes typed or said after she read the call back. Anything else leaves it waiting.
        internal static string Resume(string text)
        {
            Pending waiting;
            lock (gate) waiting = pending;
            if (waiting == null) return null;
            string said = (text ?? "").Trim();
            if (Regex.IsMatch(said, @"^(yes|yeah|yep|yup|ya|yea|sure|ok|okay|do it|go ahead|go for it|call (him|her|them)|dial( it)?|make the call|send( it)?|text (him|her|them))\b[\s.!,]*(please|pls|plz|choom|chewm)?[\s.!]*$", RegexOptions.IgnoreCase)) return Confirm();
            if (Regex.IsMatch(said, @"^(no|nah|nope|don't|do not|stop|cancel|never mind|nevermind|wait|hold on)\b", RegexOptions.IgnoreCase)) { lock (gate) pending = null; return waiting.Texting ? "Okay, I won't send it." : "Okay, I won't call."; }
            return null;
        }

        private static string Confirm()
        {
            Pending call;
            lock (gate) call = pending;
            if (call == null) return "There's nothing waiting for a yes. Tell me who to call or text first.";
            if ((DateTime.UtcNow - call.At).TotalSeconds < ConfirmGap) return "Read it back to them and wait for their yes before you dial.";
            lock (gate) pending = null;
            return call.Texting ? Text(call) : Place(call);
        }

        private static string Place(Pending call)
        {
            Settings settings = Read();
            if (!settings.Ready) { Setup(); return NotSetUp; }
            string agent = AgentId();
            if (string.IsNullOrEmpty(Key()) || string.IsNullOrEmpty(agent)) return "My phone voice isn't set up yet. Start one voice call with me first, then I can dial out.";
            string numberId = NumberId(settings);
            string owner = Owner(settings);
            string prompt = "You are Capri, " + owner + "'s AI assistant, on a real phone call you placed for them to " + call.Who + ". " +
                "Start by saying you're Capri, an AI assistant calling for " + owner + ", then say why you're calling. Why you're calling: " + call.Why + "\n" +
                "Be warm, natural and brief, like a person on the phone. Keep your personality, but be polite with the person you called and keep politics out unless they bring it up.\n" +
                "Never claim to be human or to be " + owner + ". Don't share anything private about " + owner + " beyond what this call needs, don't agree to pay for anything or commit " + owner + " to anything they didn't ask for, and if they ask something you can't answer, say you'll pass it on.\n" +
                "You have none of your computer tools on this call, so don't try to use them; just talk. When you've done what you called for, thank them and say goodbye.";
            string first = "Hi, this is Capri, an AI assistant calling for " + owner + ".";
            string head = "{\"agent_id\":" + Voice.Quote(agent) + ",\"agent_phone_number_id\":" + Voice.Quote(numberId) + ",\"to_number\":" + Voice.Quote(call.To) + ",\"conversation_initiation_client_data\":{\"conversation_config_override\":{\"agent\":{\"prompt\":{\"prompt\":" + Voice.Quote(prompt) + "}";
            string reply;
            try { reply = Http("POST", "/v1/convai/twilio/outbound-call", head + ",\"first_message\":" + Voice.Quote(first) + "}}}}"); }
            catch (VoiceException error)
            {
                // An agent that doesn't allow its opening line to be changed still gets the prompt, which opens the same way.
                if (!Regex.IsMatch(error.Message, @"first.?message|override|not allowed", RegexOptions.IgnoreCase)) throw;
                reply = Http("POST", "/v1/convai/twilio/outbound-call", head + "}}}}");
            }
            Match conversation = Regex.Match(reply ?? "", "\"conversation_id\"\\s*:\\s*\"([^\"]+)\"");
            if (Regex.IsMatch(reply ?? "", "\"success\"\\s*:\\s*false"))
            {
                Match message = Regex.Match(reply, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                return "The call didn't go through" + (message.Success ? ": " + Regex.Unescape(message.Groups[1].Value) : ".");
            }
            lock (gate) { lastConversation = conversation.Success ? conversation.Groups[1].Value : null; lastWho = call.Who; }
            if (conversation.Success) Follow(conversation.Groups[1].Value, call.Who);
            return "Calling " + call.Who + " now. I'll tell you how it goes.";
        }

        // The ElevenLabs id for their caller number, imported once and remembered.
        private static string NumberId(Settings settings)
        {
            string cache = CachePath;
            try
            {
                if (cache != null && File.Exists(cache))
                {
                    string[] saved = File.ReadAllText(cache).Trim().Split('|');
                    if (saved.Length == 2 && saved[0] == settings.Caller && saved[1].Length > 0) return saved[1];
                }
            }
            catch (Exception) { }
            string body = "{\"phone_number\":" + Voice.Quote(settings.Caller) + ",\"label\":\"Capri\",\"sid\":" + Voice.Quote(settings.Sid) + ",\"token\":" + Voice.Quote(settings.Token) + ",\"enable_sms\":false}";
            string reply = Http("POST", "/v1/convai/phone-numbers", body);
            Match id = Regex.Match(reply ?? "", "\"phone_number_id\"\\s*:\\s*\"([^\"]+)\"");
            if (!id.Success) throw new VoiceException("ElevenLabs didn't take your Twilio number. Check the SID, token and caller number in phone.txt.");
            if (cache != null) { Directory.CreateDirectory(Path.GetDirectoryName(cache)); File.WriteAllText(cache, settings.Caller + "|" + id.Groups[1].Value); }
            return id.Groups[1].Value;
        }

        // Watches the call in the background and reports back once it's over, for up to fifteen minutes.
        private static void Follow(string conversation, string who)
        {
            Thread watch = new Thread(delegate()
            {
                int waited = 0;
                while (waited < 15 * 60)
                {
                    Thread.Sleep(PollSeconds * 1000);
                    waited += Math.Max(1, PollSeconds);
                    string said = Outcome(conversation, who);
                    if (said != null) { Reported(said); return; }
                }
                Reported("I lost track of the call with " + who + ". Ask me how it went and I'll check.");
            });
            watch.IsBackground = true;
            watch.Name = "Capri call watch";
            watch.Start();
        }

        // How a finished call went, or null while it's still going.
        internal static string Outcome(string conversation, string who)
        {
            string json;
            try { json = Http("GET", "/v1/convai/conversations/" + Uri.EscapeDataString(conversation), null); }
            catch (Exception) { return null; }
            if (Regex.IsMatch(json ?? "", "\"status\"\\s*:\\s*\"failed\"")) return "The call with " + who + " didn't connect.";
            if (!Regex.IsMatch(json ?? "", "\"status\"\\s*:\\s*\"done\"")) return null;
            Match summary = Regex.Match(json, "\"transcript_summary\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (summary.Success && summary.Groups[1].Value.Trim().Length > 0) return "Call with " + who + " is done. " + Regex.Unescape(summary.Groups[1].Value).Trim();
            List<string> lines = new List<string>();
            foreach (Match turn in Regex.Matches(json, "\"role\"\\s*:\\s*\"(user|agent)\"[^{}]*?\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                lines.Add((turn.Groups[1].Value == "agent" ? "me" : who) + ": " + Regex.Unescape(turn.Groups[2].Value));
            if (lines.Count == 0) return "Call with " + who + " is done, but nobody said anything I could catch.";
            if (lines.Count > 6) lines = lines.GetRange(lines.Count - 6, 6);
            return "Call with " + who + " is done. How it ended: " + string.Join(" / ", lines.ToArray());
        }

        private static string Status()
        {
            string conversation, who;
            lock (gate) { conversation = lastConversation; who = lastWho; }
            if (conversation == null) return "I haven't placed a call yet.";
            return Outcome(conversation, who) ?? "The call with " + who + " is still going.";
        }

        private static string Send(string method, string path, string body)
        {
            string key = Key();
            if (string.IsNullOrEmpty(key)) throw new VoiceException("My ElevenLabs key isn't set, so I can't place calls.");
            System.Net.HttpWebRequest request = Voice.Open(path, key, method);
            request.Timeout = 30000;
            byte[] reply = Voice.Send(request, body == null ? null : Encoding.UTF8.GetBytes(body));
            return Encoding.UTF8.GetString(reply ?? new byte[0]);
        }

        // What she brings into a chat when a call is waiting on their yes.
        internal static string Waiting()
        {
            Pending call;
            lock (gate) call = pending;
            if (call == null) return "";
            if (call.Texting) return "\nA text is waiting on their yes: to " + call.Who + " (" + Shown(call.To) + "): \"" + call.Why + "\". If they say yes, call send_text with action confirm; if they say no, cancel.";
            return "\nA phone call is waiting on their yes: " + call.Who + " (" + Shown(call.To) + ") about " + call.Why + ". If they say yes, call the call tool with action confirm; if they say no, cancel.";
        }
    }
}

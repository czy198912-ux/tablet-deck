// Voice commands: recognises a fixed list of phrases from the default microphone with
// Windows' built-in, offline speech recognition (System.Speech).
// Commands on stdin, one per line:
//   phrase <id> <text>   add a phrase for button <id> (before "ready")
//   ready                build the grammar; replies "ready <culture>" or "err <message>"
//   listen               start listening (hold-to-talk pressed)
//   stop                 stop listening; a phrase already being spoken still finishes
// Replies on stdout:
//   heard <id> <confidence> <text>
//   none                 speech that matched no phrase
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;

static class Voice
{
    static SpeechRecognitionEngine engine;
    static volatile bool wanted; // hold-to-talk is pressed
    static readonly object outLock = new object();

    static void Say(string line)
    {
        lock (outLock)
        {
            Console.WriteLine(line);
            Console.Out.Flush();
        }
    }

    // An installed recognizer for the culture ("en-US"), else any for its language ("en").
    static RecognizerInfo FindRecognizer(string culture)
    {
        var all = SpeechRecognitionEngine.InstalledRecognizers();
        var lang = culture.Split('-')[0];
        return all.FirstOrDefault(r => string.Equals(r.Culture.Name, culture, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(r => string.Equals(r.Culture.TwoLetterISOLanguageName, lang, StringComparison.OrdinalIgnoreCase));
    }

    static void Start()
    {
        // Throws while a previous stop is still finishing; RecognizeCompleted restarts it then.
        try { engine.RecognizeAsync(RecognizeMode.Multiple); }
        catch (InvalidOperationException) { }
    }

    static void Build(string culture, Dictionary<string, string> phrases)
    {
        var info = FindRecognizer(culture);
        if (info == null)
        {
            var installed = string.Join(", ", SpeechRecognitionEngine.InstalledRecognizers().Select(r => r.Culture.Name));
            throw new Exception("no " + culture + " speech recognizer installed (installed: " + (installed == "" ? "none" : installed) + ")");
        }
        engine = new SpeechRecognitionEngine(info);
        var choices = new Choices();
        foreach (var text in phrases.Keys) choices.Add(text);
        engine.LoadGrammar(new Grammar(new GrammarBuilder(choices) { Culture = info.Culture }));
        engine.SetInputToDefaultAudioDevice();
        engine.SpeechRecognized += (s, e) =>
        {
            string id;
            if (!phrases.TryGetValue(e.Result.Text, out id)) { Say("none"); return; }
            Say("heard " + id + " " + e.Result.Confidence.ToString("0.00", CultureInfo.InvariantCulture) + " " + e.Result.Text);
        };
        engine.SpeechRecognitionRejected += (s, e) => Say("none");
        engine.RecognizeCompleted += (s, e) => { if (wanted) Start(); };
        Say("ready " + info.Culture.Name);
    }

    static void Main(string[] args)
    {
        string culture = args.Length > 0 ? args[0] : "en-US";
        var phrases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // text -> button id
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith("phrase "))
            {
                var parts = line.Split(new[] { ' ' }, 3);
                if (parts.Length == 3) phrases[parts[2]] = parts[1];
            }
            else if (line == "ready")
            {
                try { Build(culture, phrases); }
                catch (Exception ex) { Say("err " + ex.Message.Replace('\r', ' ').Replace('\n', ' ')); }
            }
            else if (line == "listen" && engine != null)
            {
                wanted = true;
                Start();
            }
            else if (line == "stop" && engine != null)
            {
                wanted = false;
                engine.RecognizeAsyncStop();
            }
        }
    }
}

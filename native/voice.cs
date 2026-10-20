// Voice commands: recognises a fixed list of phrases from the default microphone with
// Windows' built-in, offline speech recognition (System.Speech).
// Commands on stdin, one per line:
//   phrase <id> <text>   add a phrase for button <id> (before "ready")
//   ready                build the grammar; replies "ready <culture>" or "err <message>"
//   listen               start listening (hold-to-talk pressed)
//   stop                 stop listening; a phrase already being spoken still finishes
//   ptt <vk> <mode>      push-to-talk from a PC key or mouse button: Windows virtual-key code
//                        <vk> (0 = off); mode "hold" (listen while held) or "toggle"
//                        (press to start, press again to stop)
//   chime on|off         a short sound when push-to-talk starts and stops listening
// Replies on stdout:
//   heard <id> <confidence> <text>
//   none                 speech that matched no phrase
//   ptt 1 / ptt 0        push-to-talk started / stopped listening
//
// Push-to-talk reads only the chosen key's up/down state a few dozen times a second with
// GetAsyncKeyState. It doesn't hook the keyboard or see any other key.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Threading;

static class Voice
{
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    static SpeechRecognitionEngine engine;
    static volatile bool wanted; // hold-to-talk is pressed
    static readonly object outLock = new object();
    static readonly object engineLock = new object();

    static volatile int pttKey;      // virtual-key code, 0 = off
    static volatile bool pttToggle;  // press to start, press again to stop
    static volatile bool chime = true;
    static bool pttListening;        // only touched by the polling thread
    static SoundPlayer startSound, stopSound;

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
        lock (engineLock)
        {
            try { engine.RecognizeAsync(RecognizeMode.Multiple); }
            catch (InvalidOperationException) { }
        }
    }

    static void Listen(bool on)
    {
        if (engine == null) return;
        wanted = on;
        if (on) Start();
        else lock (engineLock) engine.RecognizeAsyncStop();
    }

    // ---------- push-to-talk ----------

    // Watches the push-to-talk key and starts or stops listening on its edges.
    static void PollKey()
    {
        bool wasDown = false;
        while (true)
        {
            Thread.Sleep(30);
            int vk = pttKey;
            bool down = vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (vk == 0 && pttListening) PttListen(false); // turned off while listening
            if (down == wasDown) continue;
            wasDown = down;
            if (pttToggle) { if (down) PttListen(!pttListening); }
            else PttListen(down);
        }
    }

    static void PttListen(bool on)
    {
        if (on == pttListening || engine == null) return;
        pttListening = on;
        Listen(on);
        Say(on ? "ptt 1" : "ptt 0");
        if (chime) (on ? startSound : stopSound).Play(); // plays on its own thread
    }

    // A short two-note chime as an in-memory WAV: rising to start, falling to stop.
    static SoundPlayer Chime(double first, double second)
    {
        const int rate = 22050;
        int perNote = rate * 70 / 1000, gap = rate * 15 / 1000;
        var samples = new List<short>();
        foreach (var freq in new[] { first, second })
        {
            for (int i = 0; i < perNote; i++)
            {
                double fade = Math.Min(1.0, Math.Min(i, perNote - i) / (rate * 0.008)); // soft edges, no clicks
                samples.Add((short)(Math.Sin(2 * Math.PI * freq * i / rate) * fade * 6500));
            }
            for (int i = 0; i < gap; i++) samples.Add(0);
        }
        var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + samples.Count * 2); w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(samples.Count * 2);
        foreach (var s in samples) w.Write(s);
        wav.Position = 0;
        var player = new SoundPlayer(wav);
        player.Load();
        return player;
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
        startSound = Chime(660, 990);
        stopSound = Chime(880, 550);
        new Thread(PollKey) { IsBackground = true }.Start();
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
            else if (line == "listen") Listen(true);
            else if (line == "stop") Listen(false);
            else if (line.StartsWith("ptt "))
            {
                var parts = line.Split(' ');
                int vk;
                if (parts.Length >= 2 && int.TryParse(parts[1], out vk) && vk >= 0 && vk < 256)
                {
                    pttToggle = parts.Length >= 3 && parts[2] == "toggle";
                    pttKey = vk;
                }
            }
            else if (line == "chime on") chime = true;
            else if (line == "chime off") chime = false;
        }
    }
}

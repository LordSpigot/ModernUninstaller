using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModernUninstaller.Services.Ml;

// Lokales, lernendes Modell, das Dialoge fremder Uninstaller bedient.
//
// Aufbau (zweistufig):
//   1. Auswahlelemente (Checkbox/Radio): logistische Regression je Element – "jetzt umschalten?"
//   2. Schaltflächen: Softmax-Ranking über alle Buttons plus eine Option "Warten" (z. B. Fortschrittsanzeige).
// Merkmale: Beschriftung (Wörter, Zeichen-Trigramme), sprachübergreifende Begriffsgruppen (lexicon.json),
// Fenstertext (Wortstämme und Begriffsgruppen) und deren Kreuzungen. Feature-Hashing hält das Modell klein (1 MB).
// Startwissen: seed-dialogs.json. Korrekturen des Benutzers werden gespeichert und mit höherem Gewicht nachtrainiert.
// Unabhängig vom Modell gilt eine feste Sicherheitsschicht (SafetyRules), die riskante Klicks in Rückfragen verwandelt.

public enum ControlKind { Button, CheckBox, Radio }

public sealed record DialogControl(ControlKind Kind, string Label, bool IsOn = false);

public sealed record DialogSnapshot(string Title, IReadOnlyList<string> Texts, IReadOnlyList<DialogControl> Controls)
{
    /// <summary>Kennung, die sich ändert, sobald sich am Fenster etwas Sichtbares ändert.</summary>
    public string Signature()
    {
        var sb = new StringBuilder(Title).Append('\u001f');
        foreach (var t in Texts) sb.Append(t).Append('\u001f');
        foreach (var c in Controls) sb.Append((int)c.Kind).Append(c.Label).Append(c.IsOn ? '1' : '0').Append('\u001f');
        return DialogModel.Fnv(sb.ToString()).ToString("x8");
    }
}

public enum AssistAction { Click, Wait, Ask }

public sealed record AssistDecision(AssistAction Action, int Index, double Confidence, string Reason, IReadOnlyList<(int Index, double P)> Ranking);

public sealed record TrainingSample(DialogSnapshot Window, int Target, double Weight = 1.0);

public sealed class Lexicon
{
    readonly Dictionary<string, List<string>> _labelExact = new(), _ctxExact = new();
    readonly List<(string prefix, string concept)> _labelPrefix = new(), _ctxPrefix = new();

    public static Lexicon FromJson(string json)
    {
        var lx = new Lexicon();
        using var doc = JsonDocument.Parse(json);
        Fill(doc.RootElement.GetProperty("label"), lx._labelExact, lx._labelPrefix);
        Fill(doc.RootElement.GetProperty("context"), lx._ctxExact, lx._ctxPrefix);
        return lx;
    }

    static void Fill(JsonElement e, Dictionary<string, List<string>> exact, List<(string, string)> prefix)
    {
        foreach (var concept in e.EnumerateObject())
            foreach (var w in concept.Value.EnumerateArray())
            {
                var word = w.GetString() ?? "";
                if (word.EndsWith('*')) prefix.Add((word[..^1], concept.Name));
                else
                {
                    if (!exact.TryGetValue(word, out var l)) exact[word] = l = new List<string>();
                    l.Add(concept.Name);
                }
            }
    }

    public SortedSet<string> LabelConcepts(IEnumerable<string> tokens) => Lookup(tokens, _labelExact, _labelPrefix);
    public SortedSet<string> ContextConcepts(IEnumerable<string> tokens) => Lookup(tokens, _ctxExact, _ctxPrefix);

    static SortedSet<string> Lookup(IEnumerable<string> tokens, Dictionary<string, List<string>> exact, List<(string prefix, string concept)> prefix)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in tokens)
        {
            if (exact.TryGetValue(t, out var l)) set.UnionWith(l);
            foreach (var (p, c) in prefix) if (t.StartsWith(p, StringComparison.Ordinal)) set.Add(c);
        }
        return set;
    }
}

public sealed class DialogModel
{
    const int Bits = 18, Dim = 1 << Bits;
    public const double High = 0.70, Margin = 0.30, ToggleLow = 0.35;

    readonly float[] _w = new float[Dim];
    readonly Lexicon _lex;

    public DialogModel(Lexicon lexicon) => _lex = lexicon;

    // ---------- Text ----------

    static readonly Regex TokenRx = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    static readonly Regex Dots = new(@"\.\.\.|…|[<>]", RegexOptions.Compiled);
    static readonly Regex Punct = new(@"[^\p{L}\p{N}_\s'-]", RegexOptions.Compiled);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static string Norm(string s)
    {
        s = s.ToLowerInvariant().Replace("&", "");
        s = Dots.Replace(s, " ");
        s = Punct.Replace(s, " ");
        return Spaces.Replace(s, " ").Trim();
    }

    public static List<string> Tokens(string s) =>
        TokenRx.Matches(s.ToLowerInvariant().Replace("&", "")).Select(m => m.Value).ToList();

    static string Stem(string t) => t.Length <= 5 ? t : t[..5];

    public static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s) { h ^= ch; h *= 16777619; }
        return h;
    }

    // ---------- Merkmale ----------

    sealed record Context(List<string> Stems, SortedSet<string> Concepts);

    Context ContextOf(DialogSnapshot w)
    {
        var tokens = Tokens(w.Title + " " + string.Join(" ", w.Texts));
        var stems = new List<string>();
        foreach (var t in tokens)
        {
            var s = Stem(t);
            if (!stems.Contains(s)) stems.Add(s);
            if (stems.Count >= 60) break;
        }
        return new Context(stems, _lex.ContextConcepts(tokens));
    }

    public SortedSet<string> LabelConcepts(DialogControl c) => _lex.LabelConcepts(Tokens(Norm(c.Label)));
    public SortedSet<string> ContextConcepts(DialogSnapshot w) => ContextOf(w).Concepts;

    static string KindState(DialogControl c) => c.Kind switch
    {
        ControlKind.Button => "button-",
        ControlKind.CheckBox => c.IsOn ? "checkboxon" : "checkboxoff",
        _ => c.IsOn ? "radioon" : "radiooff",
    };

    int[] ControlFeatures(DialogSnapshot w, int i, Context ctx)
    {
        var c = w.Controls[i];
        var ts = KindState(c);
        var l = Norm(c.Label);
        var lt = Tokens(l);
        var lc = _lex.LabelConcepts(lt);
        var f = new List<string> { "bias:" + ts, "l:" + l };
        foreach (var t in lt) f.Add("w:" + ts + ":" + t);
        var g = "^" + l.Replace(' ', '_') + "$";
        for (int k = 0; k + 3 <= g.Length; k++) f.Add("g:" + g.Substring(k, 3));
        foreach (var a in lc)
        {
            f.Add("lc:" + ts + ":" + a);
            foreach (var b in ctx.Concepts) f.Add("lx:" + ts + ":" + a + "|" + b);
        }
        if (lc.Count == 0) f.Add("lc:" + ts + ":none");
        foreach (var b in ctx.Concepts) f.Add("cc:" + ts + "|" + b);
        foreach (var t in lt.Take(4))
            foreach (var s in ctx.Stems) f.Add("x:" + ts + ":" + Stem(t) + "|" + s);
        if (c.Kind == ControlKind.Button)
        {
            var btn = ButtonCandidates(w);
            if (btn.Count == 1) f.Add("p:only");
            else if (i == btn[^1]) f.Add("p:last");
            else if (i == btn[0]) f.Add("p:first");
        }
        return Hash(f);
    }

    int[] WaitFeatures(DialogSnapshot w, Context ctx)
    {
        var f = new List<string> { "wait" };
        foreach (var b in ctx.Concepts) f.Add("wc:" + b);
        foreach (var s in ctx.Stems) f.Add("ws:" + s);
        var buttons = w.Controls.Where(c => c.Kind == ControlKind.Button).ToList();
        var lcs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in buttons) lcs.UnionWith(LabelConcepts(c));
        foreach (var a in lcs) f.Add("wl:" + a);
        f.Add("wn:" + Math.Min(buttons.Count, 4));
        return Hash(f);
    }

    static int[] Hash(List<string> f)
    {
        var r = new int[f.Count];
        for (int k = 0; k < f.Count; k++) r[k] = (int)(Fnv(f[k]) & (Dim - 1));
        return r;
    }

    static List<int> ToggleCandidates(DialogSnapshot w) =>
        Enumerable.Range(0, w.Controls.Count).Where(i => w.Controls[i].Kind == ControlKind.CheckBox || (w.Controls[i].Kind == ControlKind.Radio && !w.Controls[i].IsOn)).ToList();

    static List<int> ButtonCandidates(DialogSnapshot w) =>
        Enumerable.Range(0, w.Controls.Count).Where(i => w.Controls[i].Kind == ControlKind.Button).ToList();

    // ---------- Lernen ----------

    float Dot(int[] fs) { float z = 0; foreach (var k in fs) z += _w[k]; return z; }
    static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-Math.Clamp(z, -30, 30)));

    abstract record Example(double Weight);
    sealed record ToggleExample(int[] Features, double Y, double Weight) : Example(Weight);
    sealed record ButtonExample(List<int[]> Candidates, int Y, double Weight) : Example(Weight);

    public void Train(IEnumerable<TrainingSample> samples, int epochs = 30, double lr = 0.3, int seed = 7)
    {
        Array.Clear(_w);
        var data = new List<Example>();
        foreach (var s in samples)
        {
            var w = s.Window;
            if (s.Target >= w.Controls.Count) continue;
            var ctx = ContextOf(w);
            foreach (var i in ToggleCandidates(w))
                data.Add(new ToggleExample(ControlFeatures(w, i, ctx), i == s.Target ? 1 : 0, s.Weight));
            if (s.Target == -1 || w.Controls[s.Target].Kind == ControlKind.Button)
            {
                var bc = ButtonCandidates(w);
                var cands = bc.Select(i => ControlFeatures(w, i, ctx)).ToList();
                cands.Add(WaitFeatures(w, ctx));
                data.Add(new ButtonExample(cands, s.Target >= 0 ? bc.IndexOf(s.Target) : cands.Count - 1, s.Weight));
            }
        }

        var rnd = new Random(seed);
        var order = Enumerable.Range(0, data.Count).ToArray();
        for (int ep = 0; ep < epochs; ep++)
        {
            rnd.Shuffle(order);
            var a = lr / (1 + ep * 0.15);
            foreach (var idx in order)
            {
                switch (data[idx])
                {
                    case ToggleExample t:
                    {
                        var g = (float)(a * (Sigmoid(Dot(t.Features)) - t.Y) * t.Weight);
                        foreach (var k in t.Features) _w[k] -= g;
                        break;
                    }
                    case ButtonExample b:
                    {
                        var ps = Softmax(b.Candidates.Select(Dot).ToArray());
                        for (int j = 0; j < b.Candidates.Count; j++)
                        {
                            var g = (float)(a * (ps[j] - (j == b.Y ? 1 : 0)) * b.Weight);
                            foreach (var k in b.Candidates[j]) _w[k] -= g;
                        }
                        break;
                    }
                }
            }
        }
    }

    static double[] Softmax(float[] z)
    {
        var m = z.Max();
        var e = z.Select(v => Math.Exp(v - m)).ToArray();
        var s = e.Sum();
        return e.Select(v => v / s).ToArray();
    }

    // ---------- Entscheiden ----------

    /// <summary>Reine Modellentscheidung (ohne Sicherheitsschicht).</summary>
    public AssistDecision Predict(DialogSnapshot w)
    {
        var ctx = ContextOf(w);
        var toggles = ToggleCandidates(w)
            .Select(i => (Index: i, P: Sigmoid(Dot(ControlFeatures(w, i, ctx)))))
            .OrderByDescending(x => x.P).ToList();
        if (toggles.Count > 0 && toggles[0].P >= High) return new AssistDecision(AssistAction.Click, toggles[0].Index, toggles[0].P, "Auswahl", toggles);
        if (toggles.Count > 0 && toggles[0].P >= ToggleLow) return new AssistDecision(AssistAction.Ask, toggles[0].Index, toggles[0].P, "Auswahl unsicher", toggles);

        var bc = ButtonCandidates(w);
        var z = bc.Select(i => Dot(ControlFeatures(w, i, ctx))).Append(Dot(WaitFeatures(w, ctx))).ToArray();
        var p = Softmax(z);
        var ranking = p.Select((v, j) => (Index: j < bc.Count ? bc[j] : -1, P: v)).OrderByDescending(x => x.P).ToList();
        var (i1, p1) = ranking[0];
        var p2 = ranking.Count > 1 ? ranking[1].P : 0;
        if (i1 == -1) return new AssistDecision(AssistAction.Wait, -1, p1, "warten", ranking);
        if (p1 >= High && p1 - p2 >= Margin) return new AssistDecision(AssistAction.Click, i1, p1, "sicher", ranking);
        return new AssistDecision(AssistAction.Ask, i1, p1, "unsicher", ranking);
    }

    /// <summary>Modellentscheidung plus feste Sicherheitsregeln.</summary>
    public AssistDecision Decide(DialogSnapshot w) => SafetyRules.Apply(this, w, Predict(w));

    // ---------- Startwissen ----------

    public static List<TrainingSample> ParseSamples(string json, double weight = 1.0)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<TrainingSample>();
        foreach (var s in doc.RootElement.GetProperty("samples").EnumerateArray())
            list.Add(new TrainingSample(SnapshotFromJson(s), s.GetProperty("target").GetInt32(), weight));
        return list;
    }

    public static DialogSnapshot SnapshotFromJson(JsonElement s)
    {
        var texts = s.GetProperty("texts").EnumerateArray().Select(t => t.GetString() ?? "").ToList();
        var controls = new List<DialogControl>();
        foreach (var c in s.GetProperty("controls").EnumerateArray())
        {
            var kind = c[0].GetString() switch { "checkbox" => ControlKind.CheckBox, "radio" => ControlKind.Radio, _ => ControlKind.Button };
            var on = c.GetArrayLength() > 2 && c[2].ValueKind == JsonValueKind.True;
            controls.Add(new DialogControl(kind, c[1].GetString() ?? "", on));
        }
        return new DialogSnapshot(s.GetProperty("title").GetString() ?? "", texts, controls);
    }

    public static string SnapshotToJson(DialogSnapshot w, int target)
    {
        using var ms = new MemoryStream();
        using (var j = new Utf8JsonWriter(ms))
        {
            j.WriteStartObject();
            j.WriteString("title", w.Title);
            j.WriteStartArray("texts"); foreach (var t in w.Texts) j.WriteStringValue(t); j.WriteEndArray();
            j.WriteStartArray("controls");
            foreach (var c in w.Controls)
            {
                j.WriteStartArray();
                j.WriteStringValue(c.Kind switch { ControlKind.CheckBox => "checkbox", ControlKind.Radio => "radio", _ => "button" });
                j.WriteStringValue(c.Label);
                if (c.Kind != ControlKind.Button) j.WriteBooleanValue(c.IsOn);
                j.WriteEndArray();
            }
            j.WriteEndArray();
            j.WriteNumber("target", target);
            j.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string ReadResource(string name)
    {
        var asm = typeof(DialogModel).Assembly;
        using var s = asm.GetManifestResourceStream("ModernUninstaller.Ml." + name)
                      ?? throw new FileNotFoundException("Eingebettete Ressource fehlt: " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}

/// <summary>Feste Regeln, die unabhängig vom Gelernten gelten. Sie verwandeln riskante Klicks in Rückfragen.</summary>
public static class SafetyRules
{
    static readonly string[] Risky = { "install", "upgrade", "repair", "modify", "send" };
    static readonly string[] ReasonsForNo = { "shared", "keep", "restart", "cancel_q", "survey", "browser", "offer", "repair" };

    public static AssistDecision Apply(DialogModel m, DialogSnapshot w, AssistDecision d)
    {
        var cc = m.ContextConcepts(w);
        // Fehlermeldungen gehören dem Benutzer.
        if (cc.Contains("error") && d.Action != AssistAction.Ask && w.Controls.Count > 0)
            return d with { Action = AssistAction.Ask, Index = d.Index >= 0 ? d.Index : FirstButton(w), Reason = "Fehlermeldung" };
        if (d.Action != AssistAction.Click) return d;

        var c = w.Controls[d.Index];
        var lc = m.LabelConcepts(c);
        string? why = null;

        if (c.Kind == ControlKind.CheckBox && !c.IsOn) why = "würde etwas ankreuzen";
        else if (Risky.Any(lc.Contains) && !(c.Kind == ControlKind.CheckBox && c.IsOn)) why = "riskante Aktion (" + string.Join(", ", Risky.Where(lc.Contains)) + ")";
        else if (RebootNow(lc)) why = "würde neu starten";
        else if (c.Kind == ControlKind.Button)
        {
            if (w.Controls.Any(x => x.Kind != ControlKind.Button && x.IsOn && RebootNow(m.LabelConcepts(x)))) why = "„Jetzt neu starten“ ist ausgewählt";
            else if (lc.Contains("yes") && (cc.Contains("restart") || cc.Contains("cancel_q"))) why = "„Ja“ bei Neustart- oder Abbruchfrage";
            else if (lc.Contains("no") && cc.Contains("q_remove") && !ReasonsForNo.Any(cc.Contains)) why = "„Nein“ bei einer Deinstallationsfrage";
        }
        return why == null ? d : d with { Action = AssistAction.Ask, Reason = why };
    }

    static bool RebootNow(SortedSet<string> lc) => lc.Contains("restart") && !lc.Contains("later") && !lc.Contains("no");
    static int FirstButton(DialogSnapshot w) { for (int i = 0; i < w.Controls.Count; i++) if (w.Controls[i].Kind == ControlKind.Button) return i; return -1; }
}

/// <summary>Gemeinsame Modellinstanz: Startwissen + gespeicherte Korrekturen (%LocalAppData%\ModernUninstaller\dialog-training.jsonl).</summary>
public static class DialogAssistant
{
    const double UserWeight = 3.0;
    static readonly object Gate = new();
    static DialogModel? _model;
    static Lexicon? _lexicon;
    static List<TrainingSample>? _seed;

    public static string TrainingFile => Path.Combine(Settings.DataDir, "dialog-training.jsonl");

    public static DialogModel Model
    {
        get { lock (Gate) return _model ??= Build(); }
    }

    static DialogModel Build()
    {
        _lexicon ??= Lexicon.FromJson(DialogModel.ReadResource("lexicon.json"));
        _seed ??= DialogModel.ParseSamples(DialogModel.ReadResource("seed-dialogs.json"));
        var m = new DialogModel(_lexicon);
        m.Train(_seed.Concat(LoadUserSamples()));
        return m;
    }

    public static int LearnedCount => LoadUserSamples().Count;

    static List<TrainingSample> LoadUserSamples()
    {
        var list = new List<TrainingSample>();
        try
        {
            if (!File.Exists(TrainingFile)) return list;
            foreach (var line in File.ReadLines(TrainingFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    list.Add(new TrainingSample(DialogModel.SnapshotFromJson(doc.RootElement), doc.RootElement.GetProperty("target").GetInt32(), UserWeight));
                }
                catch { /* defekte Zeile überspringen */ }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Korrektur des Benutzers speichern und das Modell neu trainieren (dauert Millisekunden).</summary>
    public static void Learn(DialogSnapshot w, int target)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                File.AppendAllText(TrainingFile, DialogModel.SnapshotToJson(w, target) + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* nicht kritisch */ }
            _model = Build();
        }
    }

    public static void Forget()
    {
        lock (Gate)
        {
            try { if (File.Exists(TrainingFile)) File.Delete(TrainingFile); } catch { }
            _model = null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

[Serializable]
public class VoiceConfig
{
    public List<string> wakeWords = new List<string>();
    public List<AppCommand> apps = new List<AppCommand>();
    public List<HotkeyCommand> hotkeys = new List<HotkeyCommand>();
    public List<DialogCommand> dialogs = new List<DialogCommand>();

    [Serializable]
    public class AppCommand
    {
        public List<string> phrases = new List<string>();
        public List<string> paths = new List<string>();
    }

    [Serializable]
    public class HotkeyCommand
    {
        public List<string> phrases = new List<string>();
        public List<string> keys = new List<string>();
        public int delayMs = 20;
    }

    [Serializable]
    public class DialogCommand
    {
        public List<string> phrases = new List<string>();
        public List<string> responses = new List<string>();
    }

    // ────────────────────────────────────────────────────────
    // ПАРСЕР
    // ────────────────────────────────────────────────────────
    public static VoiceConfig Parse(string text)
    {
        var cfg = new VoiceConfig();
        if (string.IsNullOrEmpty(text)) return cfg;

        string section = null;
        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

        foreach (var raw in lines)
        {
            string line = raw.Trim();

            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("#")) continue;
            if (line.StartsWith("//")) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2).Trim().ToUpperInvariant();
                continue;
            }

            switch (section)
            {
                case "WAKE":
                    cfg.wakeWords.Add(line);
                    break;

                case "APPS":
                    {
                        var parts = SplitOnce(line, "=>");
                        if (parts == null) continue;

                        var phrases = SplitList(parts[0]);
                        var paths = SplitList(parts[1], trimQuotes: true);

                        if (phrases.Count == 0 || paths.Count == 0) continue;
                        cfg.apps.Add(new AppCommand { phrases = phrases, paths = paths });
                        break;
                    }

                case "HOTKEYS":
                    {
                        var parts = SplitOnce(line, "=>");
                        if (parts == null) continue;

                        var phrases = SplitList(parts[0]);
                        var keys = parts[1]
                            .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();

                        if (phrases.Count == 0 || keys.Count == 0) continue;
                        cfg.hotkeys.Add(new HotkeyCommand { phrases = phrases, keys = keys });
                        break;
                    }

                case "DIALOGS":
                    {
                        var parts = SplitOnce(line, "=>");
                        if (parts == null) continue;

                        var phrases = SplitList(parts[0]);
                        var responses = SplitList(parts[1]);

                        if (phrases.Count == 0 || responses.Count == 0) continue;
                        cfg.dialogs.Add(new DialogCommand { phrases = phrases, responses = responses });
                        break;
                    }
            }
        }

        return cfg;
    }

    // ────────────────────────────────────────────────────────
    // ГРАММАТИКА ДЛЯ VOSK
    // ────────────────────────────────────────────────────────
    /// <summary>
    /// Собирает JSON-массив всех фраз + активаторов.
    /// Vosk ограничит распознавание только этими фразами — резко повышается точность.
    /// </summary>
    public string BuildGrammar()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var w in wakeWords)
            if (!string.IsNullOrWhiteSpace(w)) set.Add(w.Trim().ToLowerInvariant());

        foreach (var a in apps)
            foreach (var p in a.phrases)
                if (!string.IsNullOrWhiteSpace(p)) set.Add(p.Trim().ToLowerInvariant());

        foreach (var h in hotkeys)
            foreach (var p in h.phrases)
                if (!string.IsNullOrWhiteSpace(p)) set.Add(p.Trim().ToLowerInvariant());

        foreach (var d in dialogs)
            foreach (var p in d.phrases)
                if (!string.IsNullOrWhiteSpace(p)) set.Add(p.Trim().ToLowerInvariant());

        if (set.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append("[");
        bool first = true;
        foreach (var phrase in set)
        {
            if (!first) sb.Append(",");
            first = false;
            sb.Append("\"").Append(EscapeJson(phrase)).Append("\"");
        }
        // Обязательно: маркер «неизвестное слово»
        sb.Append(",\"[unk]\"]");
        return sb.ToString();
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ────────────────────────────────────────────────────────
    private static string[] SplitOnce(string s, string separator)
    {
        int idx = s.IndexOf(separator, StringComparison.Ordinal);
        if (idx < 0) return null;
        return new[]
        {
            s.Substring(0, idx).Trim(),
            s.Substring(idx + separator.Length).Trim()
        };
    }

    private static List<string> SplitList(string s, bool trimQuotes = false)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(s)) return list;

        foreach (var part in s.Split('|'))
        {
            string t = part.Trim();
            if (trimQuotes) t = t.Trim('"');
            t = t.Trim();

            if (!string.IsNullOrEmpty(t)) list.Add(t);
        }
        return list;
    }
}
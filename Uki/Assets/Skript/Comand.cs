using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using Vosk;

public class VoskVoiceAssistant : MonoBehaviour
{
    public enum MatchMode { Contains, Exact }

    // ─── WinAPI ─────────────────────────────────────────────
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const byte VK_SHIFT = 0x10;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12;
    private const byte VK_LWIN = 0x5B;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // ────────────────────────────────────────────────────────
    // НАСТРОЙКИ VOSK
    // ────────────────────────────────────────────────────────
    [Header("Настройки Vosk")]
    public string modelPath = "vosk-model-small-ru-0.22";

    [Tooltip("Использовать грамматику (ограничить распознавание списком фраз). " +
             "Резко повышает точность, но Vosk не сможет распознать фразы вне списка.")]
    public bool useGrammar = true;

    [Tooltip("Добавлять '[unk]' в грамматику — Vosk будет отфильтровывать фразы вне списка.")]
    public bool filterUnknownPhrases = true;

    [Tooltip("Отладка: вывести итоговую грамматику в консоль")]
    public bool debugGrammar = true;

    [Header("Микрофон")]
    public int microphoneIndex = 0;

    [Header("Фраза-активатор")]
    public List<string> wakeWords = new List<string>() { "джарвис" };

    [Header("Режим сопоставления")]
    public MatchMode matchMode = MatchMode.Contains;

    // ────────────────────────────────────────────────────────
    // РЕЖИМ НЕПРЕРЫВНОГО СЛУШАНИЯ
    // ────────────────────────────────────────────────────────
    [Header("Режим непрерывного слушания")]
    [Tooltip("После активации можно отдавать команды одну за другой без повтора активатора.")]
    public bool continuousListening = true;

    [Tooltip("Сколько секунд молчания ждать перед выходом. 0 = никогда.")]
    public float conversationTimeout = 12f;

    [Tooltip("Фразы для принудительного выхода из режима.")]
    public List<string> exitPhrases = new List<string>()
    {
        "хватит", "стоп", "пока", "отдыхай", "закончили"
    };

    [Tooltip("Озвучивать выход из режима.")]
    public bool speakOnExit = false;

    [Tooltip("Что сказать при активации.")]
    public string onActivatePhrase = "слушаю";

    [Tooltip("Что сказать при выходе из режима.")]
    public string onExitPhrase = "хорошо";

    [Tooltip("Отладка режима слушания.")]
    public bool debugConversationMode = true;

    [Header("Поведение окна — тихий режим")]
    public bool startHidden = true;
    public bool hideBeforeExecute = true;
    public bool noActivate = true;
    public bool hideFromTaskbar = true;
    public bool hideOnFocusLost = true;
    public TrayManager trayManager;

    // ────────────────────────────────────────────────────────
    // ГОРЯЧИЕ КЛАВИШИ
    // ────────────────────────────────────────────────────────
    [Header("Горячая клавиша внутри игры")]
    public bool inAppHotkeyEnabled = true;
    public KeyCode hotkeyKey = KeyCode.J;
    public bool hotkeyCtrl = true;
    public bool hotkeyShift = true;
    public bool hotkeyAlt = false;

    [Header("Глобальная горячая клавиша (Windows)")]
    public bool globalHotkeyEnabled = false;
    public KeyCode globalHotkeyKey = KeyCode.J;
    public bool globalHotkeyCtrl = true;
    public bool globalHotkeyShift = true;
    public bool globalHotkeyAlt = false;

    // ────────────────────────────────────────────────────────
    // КОМАНДЫ — ЗАПУСК ПРИЛОЖЕНИЙ
    // ────────────────────────────────────────────────────────
    [Header("Команды — запуск приложений")]
    public List<CommandEntry> commands = new List<CommandEntry>()
    {
        new CommandEntry
        {
            phrases = new List<string>() { "блокнот", "нотпад", "открой блокнот" },
            paths   = new List<string>() { "notepad.exe" }
        },
        new CommandEntry
        {
            phrases = new List<string>() { "калькулятор", "кальк" },
            paths   = new List<string>() { "calc.exe" }
        },
        new CommandEntry
        {
            phrases = new List<string>() { "браузер", "хром", "интернет" },
            paths   = new List<string>() { "chrome.exe" }
        }
    };

    // ────────────────────────────────────────────────────────
    // КОМАНДЫ — НАЖАТИЕ КЛАВИШ
    // ────────────────────────────────────────────────────────
    [Header("Команды — нажатие клавиш")]
    public List<HotkeyEntry> hotkeyCommands = new List<HotkeyEntry>()
    {
        new HotkeyEntry
        {
            phrases = new List<string>() { "сверни окно", "свернуть всё", "рабочий стол" },
            keys    = new List<string>() { "Win", "D" }
        },
        new HotkeyEntry
        {
            phrases = new List<string>() { "копируй", "копировать" },
            keys    = new List<string>() { "Ctrl", "C" }
        },
        new HotkeyEntry
        {
            phrases = new List<string>() { "вставь", "вставить" },
            keys    = new List<string>() { "Ctrl", "V" }
        },
        new HotkeyEntry
        {
            phrases = new List<string>() { "закрой окно", "закрыть программу" },
            keys    = new List<string>() { "Alt", "F4" }
        },
        new HotkeyEntry
        {
            phrases = new List<string>() { "диспетчер задач", "диспетчер" },
            keys    = new List<string>() { "Ctrl", "Shift", "Esc" }
        }
    };

    // ────────────────────────────────────────────────────────
    // ДИАЛОГОВЫЕ ОТВЕТЫ
    // ────────────────────────────────────────────────────────
    [Header("Диалоговые ответы")]
    public List<DialogEntry> dialogResponses = new List<DialogEntry>()
    {
        new DialogEntry
        {
            phrases = new List<string>() { "привет", "здравствуй", "хай" },
            responses = new List<string>() { "привет", "здравствуй, чем могу помочь", "приветствую" }
        },
        new DialogEntry
        {
            phrases = new List<string>() { "как дела", "как ты" },
            responses = new List<string>() { "у меня всё отлично", "всё хорошо, спасибо что спросил" }
        },
        new DialogEntry
        {
            phrases = new List<string>() { "кто ты", "как тебя зовут" },
            responses = new List<string>() { "я голосовой помощник", "меня зовут джарвис" }
        },
        new DialogEntry
        {
            phrases = new List<string>() { "спасибо", "благодарю" },
            responses = new List<string>() { "пожалуйста", "всегда рад помочь" }
        }
    };

    [System.Serializable]
    public class CommandEntry
    {
        public List<string> phrases = new List<string>();
        public List<string> paths = new List<string>();
    }

    [System.Serializable]
    public class HotkeyEntry
    {
        public List<string> phrases = new List<string>();
        public List<string> keys = new List<string>();
        public int delayMs = 20;
    }

    [System.Serializable]
    public class DialogEntry
    {
        public List<string> phrases = new List<string>();
        public List<string> responses = new List<string>();
    }

    [Header("Озвучка (TTS через PowerShell)")]
    public bool enableTTS = true;
    public string voiceName = "Microsoft Irina Desktop";
    [Range(-10, 10)] public int speechRate = 0;
    [Range(0, 100)] public int speechVolume = 100;

    [Header("UI (опционально)")]
    public Text dialogText;
    public Text hotkeyHintText;
    public Text statusText;

    // ─── Внутренние поля ────────────────────────────────────
    private Model _model;
    private VoskRecognizer _recognizer;
    private AudioClip _micClip;
    private bool _isListening;
    private string _selectedMicName;
    private int _lastMicPos;
    private IntPtr _hwnd;

    private bool _conversationActive = false;
    private float _lastCommandTime = 0f;

    private const int SampleRate = 16000;
    private const int ClipLengthSec = 10;
    private readonly System.Random _rng = new System.Random();

    void Start()
    {
        Application.runInBackground = true;

        _hwnd = GetActiveWindow();
        if (_hwnd != IntPtr.Zero)
            ApplyWindowFlags();

        UpdateHotkeyHint();
        UpdateStatusText();

        if (Microphone.devices.Length == 0)
        {
            UnityEngine.Debug.LogError("[Vosk] Микрофоны не найдены!");
            enabled = false;
            return;
        }

        if (microphoneIndex < 0 || microphoneIndex >= Microphone.devices.Length)
            microphoneIndex = 0;

        _selectedMicName = Microphone.devices[microphoneIndex];
        UnityEngine.Debug.Log($"[Vosk] Микрофон: {_selectedMicName}");

        string fullPath = ResolveModelPath(modelPath);
        if (string.IsNullOrEmpty(fullPath))
        {
            UnityEngine.Debug.LogError($"[Vosk] Модель не найдена: '{modelPath}'");
            enabled = false;
            return;
        }

        UnityEngine.Debug.Log($"[Vosk] Загружаю модель: {fullPath}");
        try
        {
            _model = new Model(fullPath);

            string grammarJson = useGrammar ? BuildGrammar() : null;

            if (useGrammar && !string.IsNullOrEmpty(grammarJson))
            {
                if (debugGrammar)
                    UnityEngine.Debug.Log("[Vosk] Грамматика:\n" + grammarJson);

                _recognizer = new VoskRecognizer(_model, SampleRate, grammarJson);
            }
            else
            {
                if (debugGrammar)
                    UnityEngine.Debug.Log("[Vosk] Грамматика отключена — свободное распознавание.");

                _recognizer = new VoskRecognizer(_model, SampleRate);
            }

            _recognizer.SetMaxAlternatives(3);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[Vosk] Ошибка инициализации: {ex}");
            enabled = false;
            return;
        }

        StartListening();

        if (startHidden)
        {
            if (trayManager != null) trayManager.HideWindow();
            else UnityEngine.Debug.LogWarning("[Vosk] startHidden=true, но trayManager не назначен.");
        }
    }

    // ────────────────────────────────────────────────────────
    // ГРАММАТИКА VOSK
    // ────────────────────────────────────────────────────────
    private string BuildGrammar()
    {
        var phrases = new HashSet<string>();

        if (wakeWords != null)
            foreach (var w in wakeWords)
                AddPhrase(phrases, w);

        if (exitPhrases != null)
            foreach (var e in exitPhrases)
                AddPhrase(phrases, e);

        if (dialogResponses != null)
            foreach (var d in dialogResponses)
                if (d?.phrases != null)
                    foreach (var p in d.phrases)
                        AddPhrase(phrases, p);

        if (commands != null)
            foreach (var c in commands)
                if (c?.phrases != null)
                    foreach (var p in c.phrases)
                        AddPhrase(phrases, p);

        if (hotkeyCommands != null)
            foreach (var h in hotkeyCommands)
                if (h?.phrases != null)
                    foreach (var p in h.phrases)
                        AddPhrase(phrases, p);

        var sb = new StringBuilder();
        sb.Append("[");
        bool first = true;

        foreach (var phrase in phrases)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append("\"").Append(EscapeJson(phrase)).Append("\"");
        }

        if (filterUnknownPhrases)
        {
            if (!first) sb.Append(", ");
            sb.Append("\"[unk]\"");
        }

        sb.Append("]");
        return sb.ToString();
    }

    private void AddPhrase(HashSet<string> set, string phrase)
    {
        if (string.IsNullOrEmpty(phrase)) return;

        string normalized = phrase.ToLowerInvariant().Trim();
        if (string.IsNullOrEmpty(normalized)) return;

        set.Add(normalized);

        string[] words = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            foreach (var w in words)
            {
                if (w.Length >= 3)
                    set.Add(w);
            }
        }
    }

    private string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// Пересобрать грамматику в рантайме (после изменения списков в коде)
    public void RebuildGrammar()
    {
        if (_model == null) return;

        if (_recognizer != null)
        {
            try { _recognizer.Dispose(); } catch { }
            _recognizer = null;
        }

        string grammarJson = useGrammar ? BuildGrammar() : null;

        if (useGrammar && !string.IsNullOrEmpty(grammarJson))
        {
            if (debugGrammar)
                UnityEngine.Debug.Log("[Vosk] Грамматика пересобрана:\n" + grammarJson);

            _recognizer = new VoskRecognizer(_model, SampleRate, grammarJson);
        }
        else
        {
            _recognizer = new VoskRecognizer(_model, SampleRate);
        }

        _recognizer.SetMaxAlternatives(3);
        UnityEngine.Debug.Log("[Vosk] Грамматика пересобрана.");
    }

    // ────────────────────────────────────────────────────────
    // WINDOW FLAGS
    // ────────────────────────────────────────────────────────
    private void ApplyWindowFlags()
    {
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (hideFromTaskbar) ex |= WS_EX_TOOLWINDOW;
        if (noActivate) ex |= WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }

    private void RemoveWindowFlags()
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex &= ~WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }

    private void HideWindow()
    {
        if (trayManager != null) trayManager.HideWindow();
        else if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, 0);
    }

    // ────────────────────────────────────────────────────────
    // MODEL / MIC
    // ────────────────────────────────────────────────────────
    private string ResolveModelPath(string relative)
    {
        string p1 = Path.Combine(Application.streamingAssetsPath, relative);
        if (Directory.Exists(p1)) return p1;

        string justName = Path.GetFileName(relative);
        string p2 = Path.Combine(Application.streamingAssetsPath, justName);
        if (Directory.Exists(p2)) return p2;

        string p3 = Path.Combine(Application.persistentDataPath, justName);
        if (Directory.Exists(p3)) return p3;

        return null;
    }

    private void StartListening()
    {
        _micClip = Microphone.Start(_selectedMicName, true, ClipLengthSec, SampleRate);
        if (_micClip == null)
        {
            UnityEngine.Debug.LogError("[Vosk] Microphone.Start вернул null.");
            return;
        }

        while (Microphone.GetPosition(_selectedMicName) <= 0) { }

        _lastMicPos = 0;
        _isListening = true;
        UnityEngine.Debug.Log($"[Vosk] Слушаю... (каналы: {_micClip.channels}, частота: {_micClip.frequency})");
    }

    // ────────────────────────────────────────────────────────
    // UPDATE
    // ────────────────────────────────────────────────────────
    void Update()
    {
        if (inAppHotkeyEnabled && IsInAppHotkeyDown()) ActivateByHotkey();
        if (globalHotkeyEnabled && IsGlobalHotkeyDown()) ActivateByHotkey();

        if (_conversationActive && continuousListening && conversationTimeout > 0f)
        {
            if (Time.time - _lastCommandTime > conversationTimeout)
            {
                ExitConversationMode("таймаут молчания");
            }
        }

        if (!_isListening || _recognizer == null || _micClip == null) return;

        int currentPos = Microphone.GetPosition(_selectedMicName);
        if (currentPos < 0 || currentPos == _lastMicPos) return;

        int sampleCount = (currentPos > _lastMicPos)
            ? currentPos - _lastMicPos
            : _micClip.samples - _lastMicPos + currentPos;

        if (sampleCount <= 0) return;

        int channels = Mathf.Max(1, _micClip.channels);
        float[] samples = new float[sampleCount * channels];
        _micClip.GetData(samples, _lastMicPos);

        short[] pcm = new short[sampleCount];
        if (channels == 1)
        {
            for (int i = 0; i < sampleCount; i++)
                pcm[i] = (short)(Mathf.Clamp(samples[i], -1f, 1f) * 32767f);
        }
        else
        {
            for (int i = 0; i < sampleCount; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += samples[i * channels + c];
                pcm[i] = (short)(Mathf.Clamp(sum / channels, -1f, 1f) * 32767f);
            }
        }

        _lastMicPos = currentPos;

        try
        {
            if (_recognizer.AcceptWaveform(pcm, pcm.Length))
                ProcessResult(_recognizer.Result());
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[Vosk] Ошибка AcceptWaveform: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────
    // РЕЖИМ РАЗГОВОРА
    // ────────────────────────────────────────────────────────
    private void EnterConversationMode(string reason)
    {
        if (_conversationActive) return;

        _conversationActive = true;
        _lastCommandTime = Time.time;

        if (debugConversationMode)
            UnityEngine.Debug.Log($"[Vosk] ▶ Вход в режим слушания ({reason}). " +
                                  $"Таймаут: {(conversationTimeout > 0 ? conversationTimeout + "с" : "нет")}");

        UpdateStatusText();

        if (!string.IsNullOrEmpty(onActivatePhrase))
            Speak(onActivatePhrase);
    }

    private void ExitConversationMode(string reason)
    {
        if (!_conversationActive) return;

        _conversationActive = false;

        if (debugConversationMode)
            UnityEngine.Debug.Log($"[Vosk] ⏹ Выход из режима слушания ({reason}).");

        UpdateStatusText();

        if (speakOnExit && !string.IsNullOrEmpty(onExitPhrase))
            Speak(onExitPhrase);
    }

    private void RestartConversationTimer() { _lastCommandTime = Time.time; }

    private bool IsExitPhrase(string recognized)
    {
        if (exitPhrases == null || exitPhrases.Count == 0) return false;
        foreach (var phrase in exitPhrases)
        {
            if (string.IsNullOrEmpty(phrase)) continue;
            if (recognized.Contains(NormalizeText(phrase))) return true;
        }
        return false;
    }

    private bool IsWakeWord(string recognized)
    {
        if (wakeWords == null) return false;
        foreach (var w in wakeWords)
        {
            if (string.IsNullOrEmpty(w)) continue;
            if (recognized.Contains(NormalizeText(w))) return true;
        }
        return false;
    }

    private void UpdateStatusText()
    {
        if (statusText == null) return;
        statusText.text = _conversationActive ? "🎤 слушаю…" : "💤 жду активатор";
    }

    // ────────────────────────────────────────────────────────
    // ХОТКЕИ
    // ────────────────────────────────────────────────────────
    private bool IsInAppHotkeyDown()
    {
        if (!Input.GetKeyDown(hotkeyKey)) return false;

        bool ctrlOk = !hotkeyCtrl || (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
        bool shiftOk = !hotkeyShift || (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        bool altOk = !hotkeyAlt || (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));

        bool ctrlFree = hotkeyCtrl || !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
        bool shiftFree = hotkeyShift || !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        bool altFree = hotkeyAlt || !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));

        return ctrlOk && shiftOk && altOk && ctrlFree && shiftFree && altFree;
    }

    private bool IsGlobalHotkeyDown()
    {
        int vk = KeyCodeToVK(globalHotkeyKey);
        if (vk == 0) return false;
        if ((GetAsyncKeyState(vk) & 0x0001) == 0) return false;

        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

        return (globalHotkeyCtrl == ctrl) && (globalHotkeyShift == shift) && (globalHotkeyAlt == alt);
    }

    private int KeyCodeToVK(KeyCode key)
    {
        if (key >= KeyCode.A && key <= KeyCode.Z) return (int)key;
        if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) return (int)key;
        if (key >= KeyCode.F1 && key <= KeyCode.F12) return (int)key;

        switch (key)
        {
            case KeyCode.Space: return 0x20;
            case KeyCode.Return: return 0x0D;
            case KeyCode.Escape: return 0x1B;
            case KeyCode.Tab: return 0x09;
            case KeyCode.Backspace: return 0x08;
            case KeyCode.UpArrow: return 0x26;
            case KeyCode.DownArrow: return 0x28;
            case KeyCode.LeftArrow: return 0x25;
            case KeyCode.RightArrow: return 0x27;
        }
        return 0;
    }

    private void ActivateByHotkey()
    {
        if (_conversationActive)
        {
            RestartConversationTimer();
            if (debugConversationMode)
                UnityEngine.Debug.Log("[Vosk] Хоткей: продлил сессию.");
            return;
        }

        UnityEngine.Debug.Log("[Vosk] Хоткей: активация.");
        EnterConversationMode("хоткей");
    }

    private void UpdateHotkeyHint()
    {
        if (hotkeyHintText == null) return;
        string text = "";
        if (inAppHotkeyEnabled) text = BuildHotkeyString(hotkeyCtrl, hotkeyShift, hotkeyAlt, hotkeyKey);
        if (globalHotkeyEnabled)
        {
            string g = BuildHotkeyString(globalHotkeyCtrl, globalHotkeyShift, globalHotkeyAlt, globalHotkeyKey);
            text = string.IsNullOrEmpty(text) ? $"[глобально] {g}" : $"{text} · [глобально] {g}";
        }
        hotkeyHintText.text = text;
    }

    private string BuildHotkeyString(bool ctrl, bool shift, bool alt, KeyCode key)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    // ────────────────────────────────────────────────────────
    // ОБРАБОТКА РЕЗУЛЬТАТА
    // ────────────────────────────────────────────────────────
    private void ProcessResult(string json)
    {
        string text = NormalizeText(ExtractTextFromJson(json));
        if (string.IsNullOrEmpty(text)) return;

        UnityEngine.Debug.Log($"[Vosk] Распознано: {text}");

        if (!_conversationActive)
        {
            if (IsWakeWord(text))
            {
                UnityEngine.Debug.Log($"[Vosk] Активатор найден.");
                EnterConversationMode("голос");
            }
            return;
        }

        RestartConversationTimer();

        if (IsExitPhrase(text))
        {
            UnityEngine.Debug.Log($"[Vosk] Выходная фраза: {text}");
            ExitConversationMode("выходная фраза");
            return;
        }

        if (IsWakeWord(text))
        {
            if (debugConversationMode)
                UnityEngine.Debug.Log("[Vosk] Повторный активатор — сессия продлена.");
            return;
        }

        var dialog = FindDialog(text);
        if (dialog != null)
        {
            string reply = PickRandom(dialog.responses);
            if (!string.IsNullOrEmpty(reply))
            {
                UnityEngine.Debug.Log($"[Vosk] Диалог: {reply}");
                ShowText(reply);
                Speak(reply);
            }
            return;
        }

        var hotkey = FindHotkeyCommand(text);
        if (hotkey != null)
        {
            UnityEngine.Debug.Log($"[Vosk] Хоткей-команда: {string.Join("+", hotkey.keys)}");
            Speak("выполняю");
            PressHotkey(hotkey);
            return;
        }

        var cmd = FindCommand(text);
        if (cmd != null)
        {
            UnityEngine.Debug.Log($"[Vosk] Команда: {cmd.phrases[0]}");
            Speak("запускаю");
            ExecuteCommand(cmd);
            return;
        }

        UnityEngine.Debug.LogWarning($"[Vosk] Неизвестная фраза: {text}");
        string fallback = "я тебя не понимаю";
        ShowText(fallback);
        Speak(fallback);
    }

    private DialogEntry FindDialog(string recognized)
    {
        foreach (var d in dialogResponses)
        {
            if (d == null || d.phrases == null) continue;
            foreach (var phrase in d.phrases)
            {
                if (string.IsNullOrEmpty(phrase)) continue;
                string p = NormalizeText(phrase);
                bool hit = (matchMode == MatchMode.Contains)
                    ? recognized.Contains(p)
                    : recognized.Equals(p, StringComparison.OrdinalIgnoreCase);
                if (hit) return d;
            }
        }
        return null;
    }

    private HotkeyEntry FindHotkeyCommand(string recognized)
    {
        foreach (var h in hotkeyCommands)
        {
            if (h == null || h.phrases == null) continue;
            foreach (var phrase in h.phrases)
            {
                if (string.IsNullOrEmpty(phrase)) continue;
                string p = NormalizeText(phrase);
                bool hit = (matchMode == MatchMode.Contains)
                    ? recognized.Contains(p)
                    : recognized.Equals(p, StringComparison.OrdinalIgnoreCase);
                if (hit) return h;
            }
        }
        return null;
    }

    private CommandEntry FindCommand(string recognized)
    {
        foreach (var c in commands)
        {
            if (c == null || c.phrases == null) continue;
            foreach (var phrase in c.phrases)
            {
                if (string.IsNullOrEmpty(phrase)) continue;
                string p = NormalizeText(phrase);
                bool hit = (matchMode == MatchMode.Contains)
                    ? recognized.Contains(p)
                    : recognized.Equals(p, StringComparison.OrdinalIgnoreCase);
                if (hit) return c;
            }
        }
        return null;
    }

    // ────────────────────────────────────────────────────────
    // ЭМУЛЯЦИЯ КЛАВИШ
    // ────────────────────────────────────────────────────────
    private void PressHotkey(HotkeyEntry entry)
    {
        if (entry.keys == null || entry.keys.Count == 0) return;

        var vkCodes = new List<byte>();
        foreach (var k in entry.keys)
        {
            byte vk = KeyNameToVK(k);
            if (vk == 0) { UnityEngine.Debug.LogWarning($"[Vosk] Неизвестная клавиша: «{k}»"); return; }
            vkCodes.Add(vk);
        }

        foreach (var vk in vkCodes)
        {
            keybd_event(vk, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
            if (entry.delayMs > 0) Thread.Sleep(entry.delayMs);
        }

        if (entry.delayMs > 0) Thread.Sleep(entry.delayMs);

        for (int i = vkCodes.Count - 1; i >= 0; i--)
        {
            keybd_event(vkCodes[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (entry.delayMs > 0) Thread.Sleep(entry.delayMs);
        }

        UnityEngine.Debug.Log($"[Vosk] Нажато: {string.Join("+", entry.keys)}");
    }

    private byte KeyNameToVK(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        string n = name.Trim();

        if (n.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("Control", StringComparison.OrdinalIgnoreCase)) return VK_CONTROL;
        if (n.Equals("Shift", StringComparison.OrdinalIgnoreCase)) return VK_SHIFT;
        if (n.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("Menu", StringComparison.OrdinalIgnoreCase)) return VK_MENU;
        if (n.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("Windows", StringComparison.OrdinalIgnoreCase)) return VK_LWIN;

        if (n.Length == 1)
        {
            char c = char.ToUpperInvariant(n[0]);
            if (c >= 'A' && c <= 'Z') return (byte)c;
            if (c >= '0' && c <= '9') return (byte)c;
        }

        if ((n.StartsWith("F", StringComparison.OrdinalIgnoreCase)) && n.Length <= 3)
        {
            if (int.TryParse(n.Substring(1), out int fn) && fn >= 1 && fn <= 12)
                return (byte)(0x70 + fn - 1);
        }

        switch (n.ToLowerInvariant())
        {
            case "esc": case "escape": return 0x1B;
            case "enter": case "return": return 0x0D;
            case "tab": return 0x09;
            case "space": return 0x20;
            case "backspace": return 0x08;
            case "delete": case "del": return 0x2E;
            case "insert": case "ins": return 0x2D;
            case "home": return 0x24;
            case "end": return 0x23;
            case "pageup": case "pgup": return 0x21;
            case "pagedown": case "pgdn": return 0x22;
            case "up": return 0x26;
            case "down": return 0x28;
            case "left": return 0x25;
            case "right": return 0x27;
            case "printscreen": case "prtsc": return 0x2C;
            case "pause": return 0x13;
        }

        return 0;
    }

    // ────────────────────────────────────────────────────────
    // HELPERS
    // ────────────────────────────────────────────────────────
    private string PickRandom(List<string> list)
    {
        if (list == null || list.Count == 0) return "";
        return list[_rng.Next(list.Count)];
    }

    private string NormalizeText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.ToLowerInvariant().Trim();

        var sb = new StringBuilder(s.Length);
        bool lastWasSpace = false;
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); lastWasSpace = false; }
            else if (char.IsWhiteSpace(c) && !lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
        }
        return sb.ToString().Trim();
    }

    private string ExtractTextFromJson(string json)
    {
        const string key = "\"text\"";
        int k = json.IndexOf(key, StringComparison.Ordinal);
        if (k < 0) return "";
        int colon = json.IndexOf(':', k + key.Length);
        if (colon < 0) return "";
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return "";
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return "";
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    private void ExecuteCommand(CommandEntry entry)
    {
        if (entry.paths == null || entry.paths.Count == 0) return;
        if (hideBeforeExecute) HideWindow();

        foreach (string path in entry.paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                Process.Start(psi);
                UnityEngine.Debug.Log($"[Vosk] Запущено: {path}");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[Vosk] Ошибка запуска «{path}»: {ex.Message}");
            }
        }
    }

    private void ShowText(string text)
    {
        if (dialogText != null) dialogText.text = text;
    }

    private void Speak(string response)
    {
        if (!enableTTS || string.IsNullOrEmpty(response)) return;

        try
        {
            string safe = response.Replace("'", "''");

            var sb = new StringBuilder();
            sb.Append("Add-Type -AssemblyName System.Speech; ");
            sb.Append("$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; ");
            sb.Append("$s.Rate = ").Append(speechRate).Append("; ");
            sb.Append("$s.Volume = ").Append(speechVolume).Append("; ");
            if (!string.IsNullOrEmpty(voiceName))
            {
                sb.Append("try { $s.SelectVoice('")
                  .Append(voiceName.Replace("'", "''"))
                  .Append("') } catch { }; ");
            }
            sb.Append("$s.Speak('").Append(safe).Append("');");

            byte[] bytes = Encoding.Unicode.GetBytes(sb.ToString());
            string encoded = Convert.ToBase64String(bytes);

            string psPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");

            var psi = new ProcessStartInfo
            {
                FileName = psPath,
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[Vosk] Ошибка TTS: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────
    // FOCUS / QUIT
    // ────────────────────────────────────────────────────────
    void OnApplicationFocus(bool hasFocus)
    {
        if (hideOnFocusLost && !hasFocus)
        {
            HideWindow();
            UnityEngine.Debug.Log("[Vosk] Потерян фокус — окно скрыто.");
        }
    }

    void OnApplicationQuit()
    {
        _isListening = false;

        if (_recognizer != null) { try { _recognizer.Dispose(); } catch { } _recognizer = null; }
        if (_model != null) { try { _model.Dispose(); } catch { } _model = null; }

        if (!string.IsNullOrEmpty(_selectedMicName) && Microphone.IsRecording(_selectedMicName))
            Microphone.End(_selectedMicName);

        RemoveWindowFlags();
    }
}
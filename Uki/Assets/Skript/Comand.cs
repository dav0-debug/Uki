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
    // ─── WinAPI ─────────────────────────────────────────────
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Виртуальные коды клавиш
    private const byte VK_SHIFT = 0x10;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12; // Alt
    private const byte VK_LWIN = 0x5B;
    private const byte VK_RIGHT = 0x27;
    private const byte VK_LEFT = 0x25;
    private const byte VK_UP = 0x26;
    private const byte VK_DOWN = 0x28;
    private const byte VK_SPACE = 0x20;

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

    [Header("Настройки Vosk")]
    public string modelPath = "vosk-model-small-ru-0.22";

    [Header("Микрофон")]
    public int microphoneIndex = 0;

    [Header("Фраза-активатор")]
    public List<string> wakeWords = new List<string>() { "юки", "джарвис" };

    // ────────────────────────────────────────────────────────
    // СЕССИЯ КОМАНД
    // ────────────────────────────────────────────────────────
    [Header("Сессия команд (без повтора активатора)")]
    [Tooltip("Сколько секунд держать сессию после последней команды.")]
    [Range(1f, 60f)]
    public float sessionTimeoutSec = 10f;

    [Tooltip("Слова для досрочного завершения сессии.")]
    public List<string> stopWords = new List<string>() { "стоп", "хватит", "замолчи", "отбой", "закончили" };

    [Tooltip("Говорить 'слушаю' при каждой команде (false — только при активации).")]
    public bool speakOnEachCommand = false;

    // ────────────────────────────────────────────────────────
    // ОПТИМИЗАЦИЯ СКОРОСТИ
    // ────────────────────────────────────────────────────────
    [Header("Оптимизация распознавания")]
    public bool useGrammar = true;
    public bool usePartialResults = true;
    [Range(0.5f, 5f)] public float clipLengthSec = 1.0f;
    [Range(0, 5)] public int maxAlternatives = 1;

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
    // КОМАНДЫ — ЯНДЕКС.МУЗЫКА
    // ────────────────────────────────────────────────────────
    [Header("Команды — Яндекс.Музыка")]
    public List<MediaCommand> mediaCommands = new List<MediaCommand>()
    {
        new MediaCommand { phrases = new List<string>() { "пауза", "поставь на паузу", "стоп музыка" }, action = MediaAction.PlayPause },
        new MediaCommand { phrases = new List<string>() { "играй", "продолжи", "включи музыку" }, action = MediaAction.PlayPause },
        new MediaCommand { phrases = new List<string>() { "следующий трек", "дальше", "переключи" }, action = MediaAction.NextTrack },
        new MediaCommand { phrases = new List<string>() { "предыдущий трек", "назад", "верни трек" }, action = MediaAction.PrevTrack },
        new MediaCommand { phrases = new List<string>() { "громче", "прибавь звук" }, action = MediaAction.VolumeUp },
        new MediaCommand { phrases = new List<string>() { "тише", "убавь звук" }, action = MediaAction.VolumeDown }
    };

    public enum MediaAction { PlayPause, NextTrack, PrevTrack, VolumeUp, VolumeDown }

    [System.Serializable]
    public class MediaCommand
    {
        public List<string> phrases = new List<string>();
        public MediaAction action;
    }

    // ────────────────────────────────────────────────────────
    // КОМАНДЫ — ЗАПУСК ПРИЛОЖЕНИЙ
    // ────────────────────────────────────────────────────────
    [Header("Команды — запуск приложений")]
    public List<CommandEntry> commands = new List<CommandEntry>()
    {
        new CommandEntry { phrases = new List<string>() { "яндекс музыка" }, paths = new List<string>() { "C:\\Users\\%USERNAME%\\AppData\\Local\\Programs\\YandexMusic\\Яндекс Музыка.exe" } },
        new CommandEntry { phrases = new List<string>() { "стим", "steam" }, paths = new List<string>() { "steam.exe" } },
        new CommandEntry { phrases = new List<string>() { "блокнот" }, paths = new List<string>() { "notepad.exe" } },
        new CommandEntry { phrases = new List<string>() { "калькулятор" }, paths = new List<string>() { "calc.exe" } },
        new CommandEntry { phrases = new List<string>() { "браузер" }, paths = new List<string>() { "chrome.exe" } }
    };

    [Header("Команды — нажатие клавиш")]
    public List<HotkeyEntry> hotkeyCommands = new List<HotkeyEntry>()
    {
        new HotkeyEntry { phrases = new List<string>() { "сверни окно" }, keys = new List<string>() { "Win", "D" } },
        new HotkeyEntry { phrases = new List<string>() { "копируй" }, keys = new List<string>() { "Ctrl", "C" } },
        new HotkeyEntry { phrases = new List<string>() { "вставь" }, keys = new List<string>() { "Ctrl", "V" } }
    };

    [Header("Диалоговые ответы")]
    public List<DialogEntry> dialogResponses = new List<DialogEntry>()
    {
        new DialogEntry { phrases = new List<string>() { "привет" }, responses = new List<string>() { "привет" } },
        new DialogEntry { phrases = new List<string>() { "как дела" }, responses = new List<string>() { "всё хорошо" } },
        new DialogEntry { phrases = new List<string>() { "спасибо" }, responses = new List<string>() { "пожалуйста" } }
    };

    [System.Serializable]
    public class CommandEntry { public List<string> phrases = new List<string>(); public List<string> paths = new List<string>(); }
    [System.Serializable]
    public class HotkeyEntry { public List<string> phrases = new List<string>(); public List<string> keys = new List<string>(); public int delayMs = 20; }
    [System.Serializable]
    public class DialogEntry { public List<string> phrases = new List<string>(); public List<string> responses = new List<string>(); }

    [Header("Озвучка (TTS)")]
    public bool enableTTS = true;
    public string voiceName = "Microsoft Irina Desktop";
    [Range(-10, 10)] public int speechRate = 0;
    [Range(0, 100)] public int speechVolume = 100;

    [Header("UI")]
    public Text dialogText;
    public Text hotkeyHintText;

    // ─── Внутренние поля ────────────────────────────────────
    private Model _model;
    private VoskRecognizer _recognizer;
    private AudioClip _micClip;
    private bool _isListening;
    private string _selectedMicName;
    private int _lastMicPos;
    private IntPtr _hwnd;
    private float _recognitionTimer = 0f;
    private const float RecognitionInterval = 0.1f;
    private const int SampleRate = 16000;
    private readonly System.Random _rng = new System.Random();

    private bool _isSessionActive = false;
    private float _sessionTimer = 0f;

    void Start()
    {
        Application.runInBackground = true;
        _hwnd = GetActiveWindow();
        if (_hwnd != IntPtr.Zero) ApplyWindowFlags();
        UpdateHotkeyHint();

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
            string grammarJson = BuildGrammarJson();

            if (useGrammar && !string.IsNullOrEmpty(grammarJson))
            {
                UnityEngine.Debug.Log("[Vosk] Включена грамматика.");
                _recognizer = new VoskRecognizer(_model, SampleRate, grammarJson);
            }
            else
            {
                UnityEngine.Debug.Log("[Vosk] Грамматика отключена.");
                _recognizer = new VoskRecognizer(_model, SampleRate);
            }

            _recognizer.SetMaxAlternatives(maxAlternatives);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[Vosk] Ошибка инициализации: {ex}");
            enabled = false;
            return;
        }

        StartListening();

        if (startHidden && trayManager != null) trayManager.HideWindow();
    }

    // ────────────────────────────────────────────────────────
    // ГРАММАТИКА
    // ────────────────────────────────────────────────────────
    private string BuildGrammarJson()
    {
        var entries = new List<string>();

        foreach (var c in commands) if (c?.phrases != null) entries.AddRange(c.phrases);
        foreach (var h in hotkeyCommands) if (h?.phrases != null) entries.AddRange(h.phrases);
        foreach (var d in dialogResponses) if (d?.phrases != null) entries.AddRange(d.phrases);
        foreach (var m in mediaCommands) if (m?.phrases != null) entries.AddRange(m.phrases);

        entries.AddRange(wakeWords);
        if (stopWords != null) entries.AddRange(stopWords);

        var wordsToAdd = new List<string>();
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e)) continue;
            var parts = e.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                foreach (var w in parts)
                    if (w.Length >= 2) wordsToAdd.Add(w);
            }
        }
        entries.AddRange(wordsToAdd);

        entries.Add("и");
        entries.Add("потом");
        entries.Add("затем");
        entries.Add("также");
        entries.Add("плюс");
        entries.Add("открой");
        entries.Add("запусти");
        entries.Add("включи");

        var unique = new HashSet<string>();
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e)) continue;
            string lower = e.ToLower().Trim();
            if (string.IsNullOrEmpty(lower)) continue;
            if (!unique.Add(lower)) continue;

            if (!first) sb.Append(",");
            sb.Append($"\"{lower}\"");
            first = false;
        }
        sb.Append(",\"[unk]\"]");

        string json = sb.ToString();
        UnityEngine.Debug.Log($"[Vosk] Грамматика ({unique.Count} токенов): {json}");
        return json;
    }

    private void StartListening()
    {
        _micClip = Microphone.Start(_selectedMicName, true, Mathf.RoundToInt(clipLengthSec), SampleRate);
        if (_micClip == null)
        {
            UnityEngine.Debug.LogError("[Vosk] Microphone.Start вернул null.");
            return;
        }
        while (Microphone.GetPosition(_selectedMicName) <= 0) { }
        _lastMicPos = 0;
        _isListening = true;
        UnityEngine.Debug.Log($"[Vosk] Слушаю... (буфер: {clipLengthSec} сек)");
    }

    void Update()
    {
        if (inAppHotkeyEnabled && IsInAppHotkeyDown()) ActivateByHotkey();
        if (globalHotkeyEnabled && IsGlobalHotkeyDown()) ActivateByHotkey();

        if (_isSessionActive)
        {
            _sessionTimer += Time.deltaTime;
            if (_sessionTimer >= sessionTimeoutSec)
            {
                _isSessionActive = false;
                _sessionTimer = 0f;
                UnityEngine.Debug.Log($"[Vosk] Сессия завершена по таймауту.");
            }
        }

        if (!_isListening || _recognizer == null || _micClip == null) return;

        _recognitionTimer += Time.deltaTime;
        if (_recognitionTimer < RecognitionInterval) return;
        _recognitionTimer = 0f;

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
            {
                ProcessResult(_recognizer.Result());
            }
            else if (usePartialResults)
            {
                _recognizer.PartialResult();
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[Vosk] Ошибка AcceptWaveform: {ex.Message}");
        }
    }

    private void ProcessResult(string json)
    {
        string text = NormalizeText(ExtractTextFromJson(json));
        if (string.IsNullOrEmpty(text)) return;

        UnityEngine.Debug.Log($"[Vosk] Распознано: {text}");

        if (!_isSessionActive)
        {
            foreach (var w in wakeWords)
            {
                if (string.IsNullOrEmpty(w)) continue;
                if (text.Contains(NormalizeText(w)))
                {
                    UnityEngine.Debug.Log($"[Vosk] Активатор «{w}» — сессия на {sessionTimeoutSec} сек.");

                    string afterWake = RemoveWakeWord(text, w);
                    StartSession();

                    if (!string.IsNullOrEmpty(afterWake))
                        ProcessCommandText(afterWake);

                    return;
                }
            }
            return;
        }

        ProcessCommandText(text);
    }

    private void StartSession()
    {
        _isSessionActive = true;
        _sessionTimer = 0f;
        Speak("слушаю");
    }

    private string RemoveWakeWord(string text, string wake)
    {
        string w = NormalizeText(wake);
        int idx = text.IndexOf(w, StringComparison.Ordinal);
        if (idx < 0) return text;
        return text.Substring(idx + w.Length).Trim();
    }

    private void ProcessCommandText(string text)
    {
        foreach (var stop in stopWords)
        {
            if (string.IsNullOrEmpty(stop)) continue;
            string s = NormalizeText(stop);
            if (text == s || text.StartsWith(s + " ") || text.EndsWith(" " + s) || text.Contains(" " + s + " "))
            {
                UnityEngine.Debug.Log($"[Vosk] Стоп-слово «{stop}» — сессия закрыта.");
                Speak("хорошо, заканчиваю");
                _isSessionActive = false;
                _sessionTimer = 0f;
                return;
            }
        }

        _sessionTimer = 0f;

        int matched = 0;
        var alreadyDone = new HashSet<string>();

        // ─── 1. Медиа-команды (Яндекс.Музыка) ───
        foreach (var m in mediaCommands)
        {
            if (m?.phrases == null) continue;
            foreach (var phrase in m.phrases)
            {
                if (string.IsNullOrEmpty(phrase)) continue;
                string p = NormalizeText(phrase);
                if (text.Contains(p) && alreadyDone.Add("m:" + p))
                {
                    UnityEngine.Debug.Log($"[Vosk] Медиа-команда: {phrase} → {m.action}");
                    SendMediaKey(m.action);
                    matched++;
                }
            }
        }

        // ─── 2. Все диалоги ───
        foreach (var d in dialogResponses)
        {
            if (d?.phrases == null) continue;
            foreach (var phrase in d.phrases)
            {
                if (string.IsNullOrEmpty(phrase)) continue;
                string p = NormalizeText(phrase);
                if (text.Contains(p) && alreadyDone.Add("d:" + p))
                {
                    string reply = PickRandom(d.responses);
                    if (!string.IsNullOrEmpty(reply)) { ShowText(reply); Speak(reply); }
                    matched++;
                }
            }
        }

        // ─── 3. ВСЕ хоткей-команды ───
        foreach (var h in hotkeyCommands)
        {
            if (h?.phrases == null) continue;
            foreach (var phrase in h.phrases)
            {
                if (string.IsNullOrEmpty(phrase)) continue;
                string p = NormalizeText(phrase);
                if (text.Contains(p) && alreadyDone.Add("h:" + p))
                {
                    UnityEngine.Debug.Log($"[Vosk] Хоткей-команда: {phrase} → {string.Join("+", h.keys)}");
                    if (speakOnEachCommand) Speak("выполняю");
                    PressHotkey(h);
                    matched++;
                }
            }
        }

        // ─── 4. ВСЕ команды запуска приложений ───
        foreach (var c in commands)
        {
            if (c?.phrases == null) continue;
            foreach (var phrase in c.phrases)
            {
                if (string.IsNullOrEmpty(phrase)) continue;
                string p = NormalizeText(phrase);
                if (text.Contains(p) && alreadyDone.Add("c:" + p))
                {
                    UnityEngine.Debug.Log($"[Vosk] Команда: {phrase} → {string.Join(", ", c.paths)}");
                    if (speakOnEachCommand) Speak("запускаю " + phrase);
                    ExecuteCommand(c);
                    matched++;
                }
            }
        }

        if (matched == 0)
        {
            UnityEngine.Debug.LogWarning($"[Vosk] Ничего не совпало: {text}");
            Speak("я тебя не понимаю");
        }
        else
        {
            UnityEngine.Debug.Log($"[Vosk] Выполнено команд: {matched}");
        }
    }

    // ────────────────────────────────────────────────────────
    // ОТПРАВКА ГОРЯЧИХ КЛАВИШ ЯНДЕКС.МУЗЫКИ
    // ────────────────────────────────────────────────────────
    private void SendMediaKey(MediaAction action)
    {
        byte modifier = VK_CONTROL;
        byte key = 0;

        switch (action)
        {
            case MediaAction.PlayPause: key = VK_SPACE; break;
            case MediaAction.NextTrack: key = VK_RIGHT; break;
            case MediaAction.PrevTrack: key = VK_LEFT; break;
            case MediaAction.VolumeUp: key = VK_UP; break;
            case MediaAction.VolumeDown: key = VK_DOWN; break;
        }

        if (key == 0) return;

        try
        {
            // Нажимаем Ctrl + клавиша
            keybd_event(modifier, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
            Thread.Sleep(10);
            keybd_event(key, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
            Thread.Sleep(30);
            keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(10);
            keybd_event(modifier, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            UnityEngine.Debug.Log($"[Vosk] Отправлена комбинация: Ctrl + {key:X2}");
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"[Vosk] Ошибка отправки клавиш: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────
    // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
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
            case KeyCode.Space: return VK_SPACE;
            case KeyCode.Return: return 0x0D;
            case KeyCode.Escape: return 0x1B;
            case KeyCode.Tab: return 0x09;
            case KeyCode.Backspace: return 0x08;
            case KeyCode.UpArrow: return VK_UP;
            case KeyCode.DownArrow: return VK_DOWN;
            case KeyCode.LeftArrow: return VK_LEFT;
            case KeyCode.RightArrow: return VK_RIGHT;
        }
        return 0;
    }

    private void ActivateByHotkey()
    {
        if (_isSessionActive) return;
        UnityEngine.Debug.Log("[Vosk] Хоткей: активация сессии.");
        StartSession();
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
            case "space": return VK_SPACE;
            case "backspace": return 0x08;
            case "delete": case "del": return 0x2E;
            case "insert": case "ins": return 0x2D;
            case "home": return 0x24;
            case "end": return 0x23;
            case "pageup": case "pgup": return 0x21;
            case "pagedown": case "pgdn": return 0x22;
            case "up": return VK_UP;
            case "down": return VK_DOWN;
            case "left": return VK_LEFT;
            case "right": return VK_RIGHT;
            case "printscreen": case "prtsc": return 0x2C;
            case "pause": return 0x13;
        }
        return 0;
    }

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
        int k = json.IndexOf("\"text\"", StringComparison.Ordinal);
        if (k < 0) return "";
        int colon = json.IndexOf(':', k + 6);
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
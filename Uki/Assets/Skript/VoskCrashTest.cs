using System;
using System.IO;
using UnityEngine;
using Vosk;

public class VoskCrashTest : MonoBehaviour
{
    [Tooltip("Путь к папке модели. Может быть относительно StreamingAssets или абсолютный.")]
    public string modelPath = "vosk-model-small-ru-0.22";

    void Start()
    {
        Debug.Log("=== ШАГ 0: старт ===");

        // ─── ШАГ 1: проверяем путь ────────────────────────
        string full = Path.IsPathRooted(modelPath)
            ? modelPath
            : Path.Combine(Application.streamingAssetsPath, modelPath);

        Debug.Log("=== ШАГ 1: путь к модели = " + full);

        if (!Directory.Exists(full))
        {
            Debug.LogError("❌ Папка модели НЕ существует. Дальше идти нельзя.");
            return;
        }

        // ─── ШАГ 2: проверяем файлы внутри ────────────────
        var files = Directory.GetFiles(full);
        Debug.Log($"=== ШАГ 2: файлов в папке: {files.Length}");
        foreach (var f in files) Debug.Log("   " + Path.GetFileName(f));

        if (files.Length == 0)
        {
            Debug.LogError("❌ Папка модели пуста!");
            return;
        }

        string am = Path.Combine(full, "am", "final.mdl");
        string graph = Path.Combine(full, "graph");
        if (!File.Exists(am)) Debug.LogError("❌ Нет файла am/final.mdl");
        if (!Directory.Exists(graph)) Debug.LogError("❌ Нет папки graph");
        if (!File.Exists(am) || !Directory.Exists(graph))
        {
            Debug.LogError("❌ Структура модели неполная. Скачайте модель заново.");
            return;
        }

        // ─── ШАГ 3: создаём модель ────────────────────────
        Debug.Log("=== ШАГ 3: вызываю new Model(...)");
        Model model;
        try
        {
            model = new Model(full);
        }
        catch (Exception ex)
        {
            Debug.LogError("❌ Краш (или исключение) в new Model: " + ex);
            return;
        }
        Debug.Log("✅ Модель загружена");

        // ─── ШАГ 4: создаём распознаватель ────────────────
        Debug.Log("=== ШАГ 4: вызываю new VoskRecognizer(...)");
        VoskRecognizer rec;
        try
        {
            rec = new VoskRecognizer(model, 16000f);
            rec.SetMaxAlternatives(1);
        }
        catch (Exception ex)
        {
            Debug.LogError("❌ Ошибка в new VoskRecognizer: " + ex);
            model.Dispose();
            return;
        }
        Debug.Log("✅ Распознаватель готов");

        // ─── ШАГ 5: подаём тишину ─────────────────────────
        Debug.Log("=== ШАГ 5: подаю 0.5 сек тишины в AcceptWaveform");
        short[] silence = new short[8000]; // 16000 Гц × 0.5 c
        try
        {
            rec.AcceptWaveform(silence, silence.Length);
            Debug.Log("✅ AcceptWaveform прошёл");
        }
        catch (Exception ex)
        {
            Debug.LogError("❌ Ошибка в AcceptWaveform: " + ex);
        }

        // ─── ШАГ 6: проверяем Result ──────────────────────
        Debug.Log("=== ШАГ 6: Result() = " + rec.Result());

        // ─── ШАГ 7: уборка ────────────────────────────────
        Debug.Log("=== ШАГ 7: Dispose");
        try
        {
            rec.Dispose();
            model.Dispose();
            Debug.Log("✅ Всё чисто освобождено");
        }
        catch (Exception ex)
        {
            Debug.LogError("❌ Ошибка при Dispose: " + ex);
        }

        Debug.Log("=== ВСЁ ОК: Vosk работает корректно ===");
    }
}
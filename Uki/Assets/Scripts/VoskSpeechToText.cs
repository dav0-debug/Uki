using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ionic.Zip;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Vosk;

public class VoskSpeechToText : MonoBehaviour
{
    [Tooltip("Location of the model, relative to the Streaming Assets folder. " +
             "Can be either a .zip file or an already-extracted folder name.")]
    public string ModelPath = "vosk-model-small-ru-0.22";

    [Tooltip("The source of the microphone input.")]
    public VoiceProcessor VoiceProcessor;

    [Tooltip("The Max number of alternatives that will be processed.")]
    public int MaxAlternatives = 3;

    [Tooltip("How long should we record before restarting?")]
    public float MaxRecordLength = 5;

    [Tooltip("Should the recognizer start when the application is launched?")]
    public bool AutoStart = true;

    [Tooltip("The phrases that will be detected. If left empty, all words will be detected.")]
    public List<string> KeyPhrases = new List<string>();

    private Model _model;
    private VoskRecognizer _recognizer;
    private bool _recognizerReady;
    private readonly List<short> _buffer = new List<short>();

    public Action<string> OnStatusUpdated;
    public Action<string> OnTranscriptionResult;

    private string _decompressedModelPath;
    private string _grammar = "";
    private bool _isDecompressing;
    private bool _isInitializing;
    private bool _didInit;

    private bool _running;
    private readonly ConcurrentQueue<short[]> _threadedBufferQueue = new ConcurrentQueue<short[]>();
    private readonly ConcurrentQueue<string> _threadedResultQueue = new ConcurrentQueue<string>();

    static readonly ProfilerMarker voskRecognizerCreateMarker = new ProfilerMarker("VoskRecognizer.Create");
    static readonly ProfilerMarker voskRecognizerReadMarker = new ProfilerMarker("VoskRecognizer.AcceptWaveform");

    void Start()
    {
        if (AutoStart)
            StartVoskStt();
    }

    public void StartVoskStt(List<string> keyPhrases = null, string modelPath = default,
                             bool startMicrophone = false, int maxAlternatives = 3)
    {
        if (_isInitializing) { Debug.LogError("Initializing in progress!"); return; }
        if (_didInit) { Debug.LogError("Vosk has already been initialized!"); return; }

        if (!string.IsNullOrEmpty(modelPath)) ModelPath = modelPath;
        if (keyPhrases != null) KeyPhrases = keyPhrases;
        MaxAlternatives = maxAlternatives;

        StartCoroutine(DoStartVoskStt(startMicrophone));
    }

    private IEnumerator DoStartVoskStt(bool startMicrophone)
    {
        _isInitializing = true;
        yield return WaitForMicrophoneInput();
        yield return Decompress();

        OnStatusUpdated?.Invoke("Loading Model from: " + _decompressedModelPath);
        _model = new Model(_decompressedModelPath);

        yield return null;

        OnStatusUpdated?.Invoke("Initialized");
        VoiceProcessor.OnFrameCaptured += VoiceProcessorOnOnFrameCaptured;
        VoiceProcessor.OnRecordingStop += VoiceProcessorOnOnRecordingStop;

        if (startMicrophone) VoiceProcessor.StartRecording();

        _isInitializing = false;
        _didInit = true;

        ToggleRecording();
    }

    private void UpdateGrammar()
    {
        if (KeyPhrases == null || KeyPhrases.Count == 0) { _grammar = ""; return; }

        JSONArray keywords = new JSONArray();
        foreach (string keyphrase in KeyPhrases)
            keywords.Add(new JSONString(keyphrase.ToLower()));
        keywords.Add(new JSONString("[unk]"));
        _grammar = keywords.ToString();
    }

    // ────────────────────────────────────────────────────────────────
    // ИСПРАВЛЕННЫЙ Decompress
    // ────────────────────────────────────────────────────────────────
    private IEnumerator Decompress()
    {
        // Имя папки модели = имя файла без .zip (если оно есть).
        // ВАЖНО: Path.GetFileNameWithoutExtension("vosk-model-small-ru-0.22")
        // вернёт "vosk-model-small-ru" (потому что .22 — "расширение"),
        // поэтому проверяем именно .zip, а не HasExtension.
        string modelName;
        if (Path.GetExtension(ModelPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            modelName = Path.GetFileNameWithoutExtension(ModelPath);
        else
            modelName = ModelPath;

        string targetDir = Path.Combine(Application.persistentDataPath, modelName);

        // 1) Модель уже распакована в persistentDataPath — используем её.
        if (Directory.Exists(targetDir) && Directory.GetFiles(targetDir).Length > 0)
        {
            OnStatusUpdated?.Invoke("Using existing decompressed model.");
            _decompressedModelPath = targetDir;
            Debug.Log("[Vosk] Используем уже распакованную модель: " + targetDir);
            yield break;
        }

        OnStatusUpdated?.Invoke("Preparing model...");

        string sourceFolder = Path.Combine(Application.streamingAssetsPath, modelName);
        string sourceZip = Path.Combine(Application.streamingAssetsPath, ModelPath);

        // 2) В StreamingAssets лежит распакованная ПАПКА — копируем её в persistentDataPath.
        if (Directory.Exists(sourceFolder))
        {
            OnStatusUpdated?.Invoke("Copying model folder from StreamingAssets...");
            Debug.Log("[Vosk] Копируем папку модели: " + sourceFolder + " → " + targetDir);
            yield return null;

            try
            {
                CopyDirectory(sourceFolder, targetDir);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Vosk] Ошибка копирования модели: {ex}");
                yield break;
            }

            _decompressedModelPath = targetDir;
            OnStatusUpdated?.Invoke("Model ready.");
            yield return new WaitForSeconds(0.5f);
            yield break;
        }

        // 3) В StreamingAssets лежит ZIP — распаковываем.
        if (!Path.GetExtension(ModelPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            Debug.LogError($"[Vosk] Модель не найдена. Ожидалась папка '{sourceFolder}' " +
                           $"или .zip-файл в StreamingAssets.");
            yield break;
        }

        Stream dataStream;
        if (sourceZip.Contains("://")) // Android / WebGL
        {
            UnityWebRequest www = UnityWebRequest.Get(sourceZip);
            www.SendWebRequest();
            while (!www.isDone) yield return null;
            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[Vosk] Не удалось скачать модель: {www.error}");
                yield break;
            }
            dataStream = new MemoryStream(www.downloadHandler.data);
        }
        else
        {
            if (!File.Exists(sourceZip))
            {
                Debug.LogError($"[Vosk] Zip-файл модели не найден: {sourceZip}");
                yield break;
            }
            dataStream = File.OpenRead(sourceZip);
        }

        OnStatusUpdated?.Invoke("Extracting model...");
        _isDecompressing = false;
        var zipFile = ZipFile.Read(dataStream);
        zipFile.ExtractProgress += ZipFileOnExtractProgress;
        zipFile.ExtractAll(Application.persistentDataPath);

        while (!_isDecompressing) yield return null;

        _decompressedModelPath = targetDir;
        OnStatusUpdated?.Invoke("Extraction complete!");
        yield return new WaitForSeconds(1);
        zipFile.Dispose();
        dataStream.Dispose();
    }

    // Рекурсивное копирование содержимого sourceDir в destDir.
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string destSub = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSub);
        }
    }

    private void ZipFileOnExtractProgress(object sender, ExtractProgressEventArgs e)
    {
        if (e.EventType == ZipProgressEventType.Extracting_AfterExtractAll)
        {
            _isDecompressing = true;
            _decompressedModelPath = e.ExtractLocation;
        }
    }

    private IEnumerator WaitForMicrophoneInput()
    {
        while (Microphone.devices.Length <= 0)
            yield return null;
    }

    public void ToggleRecording()
    {
        Debug.Log("Toggle Recording");
        if (!VoiceProcessor.IsRecording)
        {
            Debug.Log("Start Recording");
            _running = true;
            VoiceProcessor.StartRecording();
            Task.Run(ThreadedWork).ConfigureAwait(false);
        }
        else
        {
            Debug.Log("Stop Recording");
            _running = false;
            VoiceProcessor.StopRecording();
        }
    }

    void Update()
    {
        if (_threadedResultQueue.TryDequeue(out string voiceResult))
            OnTranscriptionResult?.Invoke(voiceResult);
    }

    private void VoiceProcessorOnOnFrameCaptured(short[] samples)
    {
        _threadedBufferQueue.Enqueue(samples);
    }

    private void VoiceProcessorOnOnRecordingStop()
    {
        Debug.Log("Stopped");
    }

    private async Task ThreadedWork()
    {
        voskRecognizerCreateMarker.Begin();
        if (!_recognizerReady)
        {
            UpdateGrammar();
            if (string.IsNullOrEmpty(_grammar))
                _recognizer = new VoskRecognizer(_model, 16000.0f);
            else
                _recognizer = new VoskRecognizer(_model, 16000.0f, _grammar);

            _recognizer.SetMaxAlternatives(MaxAlternatives);
            _recognizerReady = true;
            Debug.Log("Recognizer ready");
        }
        voskRecognizerCreateMarker.End();

        voskRecognizerReadMarker.Begin();

        while (_running)
        {
            if (_threadedBufferQueue.TryDequeue(out short[] voiceResult))
            {
                if (_recognizer.AcceptWaveform(voiceResult, voiceResult.Length))
                {
                    var result = _recognizer.Result();
                    _threadedResultQueue.Enqueue(result);
                }
            }
            else
            {
                await Task.Delay(100);
            }
        }

        voskRecognizerReadMarker.End();
    }
}
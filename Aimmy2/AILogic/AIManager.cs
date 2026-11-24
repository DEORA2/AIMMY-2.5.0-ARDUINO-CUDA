using AILogic;
using Whales2.Class;
using Class;
using InputLogic;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Newtonsoft.Json.Linq;
using Other;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Visuality;
using static AILogic.MathUtil;
using static Other.LogManager;

namespace Whales2.AILogic
{
    internal class AIManager : IDisposable
    {
        #region Variables

        private int _currentImageSize;
        private readonly object _sizeLock = new object();
        private volatile bool _sizeChangePending = false;

        public void RequestSizeChange(int newSize)
        {
            lock (_sizeLock)
            {
                _sizeChangePending = true;
            }
        }

        // Dynamic properties instead of constants
        public int IMAGE_SIZE => _currentImageSize;
        private int NUM_DETECTIONS { get; set; } = 8400; // Will be set dynamically for dynamic models
        private bool IsDynamicModel { get; set; } = false;

        // Public static property to check if current loaded model is dynamic
        public static bool CurrentModelIsDynamic { get; private set; } = false;
        private int ModelFixedSize { get; set; } = 640; // Store the fixed size for non-dynamic models
        private int NUM_CLASSES { get; set; } = 1;
        private Dictionary<int, string> _modelClasses = new Dictionary<int, string>
        {
            { 0, "enemy" }
        };
        public Dictionary<int, string> ModelClasses => _modelClasses; // apparently this is better than making _modelClasses public
        public static event Action<Dictionary<int, string>>? ClassesUpdated;
        public static event Action<int>? ImageSizeUpdated;
        public static event Action<bool>? DynamicModelStatusChanged;

        private const int SAVE_FRAME_COOLDOWN_MS = 500;

        private DateTime lastSavedTime = DateTime.MinValue;
        private List<string>? _outputNames;
        private string _inputName = "images";
        // Null until the output shape is known; dynamic-shape models resolve it from
        // the first inference.
        private ModelOutputLayout? _outputLayout;
        private int _metadataClassCount;
        // Trailing per-box values that aren't box/score data: mask coefficients
        // (segment), keypoints (pose) or the rotation angle (obb).
        private int _extraChannels;
        private bool _layoutErrorLogged;
        private RectangleF LastDetectionBox;
        private KalmanPrediction kalmanPrediction;
        private WiseTheFoxPrediction wtfpredictionManager;

        private byte[]? _bitmapBuffer; // Reusable buffer for bitmap operations

        // Display-aware properties
        private int ScreenWidth => DisplayManager.ScreenWidth;
        private int ScreenHeight => DisplayManager.ScreenHeight;
        private int ScreenLeft => DisplayManager.ScreenLeft;
        private int ScreenTop => DisplayManager.ScreenTop;

        private readonly RunOptions? _modeloptions;
        private InferenceSession? _onnxModel;
        // Set when the model runs as a CUDA graph; it owns _onnxModel then
        private CudaGraphRunner? _graphRunner;
        private readonly object _inferenceLock = new();
        private bool _disposed;

        private Thread? _aiLoopThread;
        private volatile bool _isAiLoopRunning;

        // For Auto-Labelling Data System
        private bool PlayerFound = false;

        // Sticky-Aim
        private readonly StickyTargetTracker _stickyTracker = new();

        private double CenterXTranslated = 0;
        private double CenterYTranslated = 0;

        // Benchmarking
        private int iterationCount = 0;
        private long totalTime = 0;

        // Status readouts for the UI (read-only, sampled by MainWindow)
        public static string ExecutionProvider { get; private set; } = "N/A";
        private int _fpsFrames;
        private long _fpsWindowStart = Stopwatch.GetTimestamp();
        private long _lastFrameTimestamp;
        private double _fps;

        /// <summary>True once the model session started and the inference loop is running.</summary>
        public bool IsRunning => _isAiLoopRunning;

        /// <summary>Inference frames per second over the last second; 0 when the loop is idle.</summary>
        public double CurrentFps =>
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFrameTimestamp)).TotalSeconds > 1.5 ? 0 : _fps;

        private void CountFrame()
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastFrameTimestamp, now);
            _fpsFrames++;

            double elapsed = Stopwatch.GetElapsedTime(_fpsWindowStart, now).TotalSeconds;
            if (elapsed >= 1.0)
            {
                _fps = _fpsFrames / elapsed;
                _fpsFrames = 0;
                _fpsWindowStart = now;
            }
        }

        private int detectedX { get; set; }
        private int detectedY { get; set; }

        public double AIConf = 0;
        private static int targetX, targetY;

        // Pre-calculated values - now dynamic
        private float _scaleX => ScreenWidth / (float)IMAGE_SIZE;
        private float _scaleY => ScreenHeight / (float)IMAGE_SIZE;

        // Tensor reuse (model inference)
        private DenseTensor<float>? _reusableTensor;
        private float[]? _reusableInputArray;
        private List<NamedOnnxValue>? _reusableInputs;

        // Benchmarking
        private readonly Dictionary<string, BenchmarkData> _benchmarks = new();
        private readonly object _benchmarkLock = new();


        private readonly CaptureManager _captureManager = new();
        #endregion Variables

        #region Benchmarking

        private class BenchmarkData
        {
            public long TotalTime { get; set; }
            public int CallCount { get; set; }
            public long MinTime { get; set; } = long.MaxValue;
            public long MaxTime { get; set; }
            public double AverageTime => CallCount > 0 ? (double)TotalTime / CallCount : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IDisposable Benchmark(string name)
        {
            return new BenchmarkScope(this, name);
        }

        private class BenchmarkScope : IDisposable
        {
            private readonly AIManager _manager;
            private readonly string _name;
            private readonly Stopwatch _sw;

            public BenchmarkScope(AIManager manager, string name)
            {
                _manager = manager;
                _name = name;
                _sw = Stopwatch.StartNew();
            }

            public void Dispose()
            {
                _sw.Stop();
                _manager.RecordBenchmark(_name, _sw.ElapsedMilliseconds);
            }
        }

        private void RecordBenchmark(string name, long elapsedMs)
        {
            lock (_benchmarkLock)
            {
                if (!_benchmarks.TryGetValue(name, out var data))
                {
                    data = new BenchmarkData();
                    _benchmarks[name] = data;
                }

                data.TotalTime += elapsedMs;
                data.CallCount++;
                data.MinTime = Math.Min(data.MinTime, elapsedMs);
                data.MaxTime = Math.Max(data.MaxTime, elapsedMs);
            }
        }

        public void PrintBenchmarks()
        {
            lock (_benchmarkLock)
            {
                var lines = new List<string>
                {
                    "=== AIManager Performance Benchmarks ==="
                };

                foreach (var kvp in _benchmarks.OrderBy(x => x.Key))
                {
                    var data = kvp.Value;
                    lines.Add($"{kvp.Key}: Avg={data.AverageTime:F2}ms, Min={data.MinTime}ms, Max={data.MaxTime}ms, Count={data.CallCount}");
                }

                lines.Add($"Overall FPS: {(iterationCount > 0 ? 1000.0 / (totalTime / (double)iterationCount) : 0):F2}");

                //File.WriteAllLines("AIManager_Benchmarks.txt", lines);

                Log(LogLevel.Info, string.Join(Environment.NewLine, lines));
            }
        }

        #endregion Benchmarking

        public AIManager(string modelPath)
        {
            // Initialize the cached image size
            _currentImageSize = int.Parse(Dictionary.dropdownState["Image Size"]);

            // Initialize DXGI capture for current display
            if (Dictionary.dropdownState["Screen Capture Method"] == "DirectX")
            {
                _captureManager.InitializeDxgiDuplication();
            }

            kalmanPrediction = new KalmanPrediction();
            wtfpredictionManager = new WiseTheFoxPrediction();

            _modeloptions = new RunOptions();

            var sessionOptions = new SessionOptions
            {
                EnableCpuMemArena = true,
                EnableMemoryPattern = false,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = 4
            };

            // Attempt to load via CUDA (else fallback to CPU)
            Task.Run(() => InitializeModel(sessionOptions, modelPath));
        }

        #region Models

        private async Task InitializeModel(SessionOptions sessionOptions, string modelPath)
        {
            using (Benchmark("ModelInitialization"))
            {
                try
                {
                    await LoadModelAsync(modelPath);
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, $"Error starting the model via CUDA: {ex.Message}\n\nFalling back to CPU, performance may be poor.", true);

                    try
                    {
                        await LoadModelAsync(modelPath);
                    }
                    catch (Exception e)
                    {
                        Log(LogLevel.Error, $"Error starting the model via CPU: {e.Message}, you won't be able to aim assist at all.", true);
                    }
                }

                FileManager.CurrentlyLoadingModel = false;
            }
        }

        private static readonly Dictionary<string, string> CudaOptions = new()
        {
            ["device_id"] = "0",
            // Grow GPU memory only by what is asked for instead of doubling the pool
            ["arena_extend_strategy"] = "kSameAsRequested",
            // Channels-last layout: on an RTX 4080 Laptop this used ~170 MB less RAM and ran
            // slightly faster than the default layout (measured with a 640 YOLO model)
            ["prefer_nhwc"] = "1",
            ["cudnn_conv_algo_search"] = "EXHAUSTIVE",
            ["do_copy_in_default_stream"] = "1",
        };

        // The model runs on the GPU, so ONNX Runtime's CPU-side memory pools only hold on to RAM
        private static SessionOptions CreateSessionOptions() => new()
        {
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        private static string TryAppendBestProvider(SessionOptions sessionOptions, HashSet<string> available)
        {
            // CUDA first (needs the NVIDIA CUDA 12 + cuDNN 9 runtime on PATH), CPU as last resort.
            if (available.Contains("CUDAExecutionProvider"))
            {
                try
                {
                    using var cudaOptions = new OrtCUDAProviderOptions();
                    cudaOptions.UpdateOptions(CudaOptions);
                    sessionOptions.AppendExecutionProvider_CUDA(cudaOptions);
                    return "CUDA";
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Warning, $"CUDA failed, falling back to CPU (is the CUDA 12 / cuDNN 9 runtime installed?): {ex.Message}", true, 6000);
                }
            }

            sessionOptions.AppendExecutionProvider_CPU();
            return "CPU";
        }

        /// <summary>
        /// Swaps the CUDA session for a CUDA graph runner when the model allows it (all layers on CUDA,
        /// float input/output). The normal session stays in use otherwise.
        /// </summary>
        private void TryEnableCudaGraph(string modelPath)
        {
            var runner = CudaGraphRunner.TryCreate(modelPath, IMAGE_SIZE, CudaOptions, CreateSessionOptions, out var reason);
            if (runner == null)
            {
                Log(LogLevel.Info, $"CUDA graphs not used for this model, running normal CUDA: {reason}");
                ShowProviderNotice("USING CUDA");
                return;
            }

            _onnxModel?.Dispose();
            _onnxModel = runner.Session;
            _graphRunner = runner;
            Log(LogLevel.Info, $"CUDA graphs on ({IMAGE_SIZE}x{IMAGE_SIZE})");
            ShowProviderNotice("USING CUDA GRAPHS");
        }

        private static void ShowProviderNotice(string message)
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    try { new NoticeBar(message, 2000).Show(); } catch { }
                });
            }
            catch { }
        }

        private Task LoadModelAsync(string modelPath)
        {
            using SessionOptions sessionOptions = CreateSessionOptions();
            string provider;
            ExecutionProvider = "N/A";

            // Only providers the loaded native onnxruntime.dll actually exports.
            // Trying to append an EP the build doesn't include throws
            // EntryPointNotFoundException, so gate every try on this set.
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var ep in OrtEnv.Instance().GetAvailableProviders())
                    available.Add(ep);
            }
            catch { /* empty set => we fall through to CPU */ }

            try
            {
                provider = TryAppendBestProvider(sessionOptions, available);
                _onnxModel = new InferenceSession(modelPath, sessionOptions);
                ExecutionProvider = provider; // only report a provider once the session actually started

                // Validate the onnx model output shape (ensure model is OnnxV8)
                if (!ValidateOnnxShape())
                {
                    ExecutionProvider = "N/A";
                    _onnxModel?.Dispose();
                    return Task.CompletedTask;
                }

                if (provider == "CUDA")
                    TryEnableCudaGraph(modelPath);
                else
                    ShowProviderNotice("USING CPU");

                // Pre-allocate bitmap buffer
                _bitmapBuffer = new byte[3 * IMAGE_SIZE * IMAGE_SIZE];
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Error loading the model: {ex.Message}", true);
                _onnxModel?.Dispose();
                return Task.CompletedTask;
            }

            // Begin the loop
            _isAiLoopRunning = true;
            _aiLoopThread = new Thread(AiLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal // Higher priority for AI thread
            };
            _aiLoopThread.Start();
            return Task.CompletedTask;
        }

        private bool ValidateOnnxShape()
        {
            if (_onnxModel != null)
            {
                var inputMetadata = _onnxModel.InputMetadata;
                var outputMetadata = _onnxModel.OutputMetadata;

                Log(LogLevel.Info, "=== Model Metadata ===");
                Log(LogLevel.Info, "Input Metadata:");

                bool isDynamic = false;
                int fixedInputSize = 0;

                _inputName = _onnxModel.InputNames[0];

                foreach (var kvp in inputMetadata)
                {
                    string dimensionsStr = string.Join("x", kvp.Value.Dimensions);
                    Log(LogLevel.Info, $"  Name: {kvp.Key}, Dimensions: {dimensionsStr}");
                }

                var inputDims = inputMetadata[_inputName].Dimensions;
                if (inputDims.Length == 4)
                {
                    // Only a dynamic height/width makes the model size-selectable; a
                    // dynamic batch axis alone still means a fixed input size.
                    if (inputDims[2] <= 0 || inputDims[3] <= 0)
                    {
                        isDynamic = true;
                    }
                    else if (inputDims[2] != inputDims[3])
                    {
                        Log(LogLevel.Error,
                            $"Model input is {inputDims[3]}x{inputDims[2]}; only square inputs are supported. Re-export with a square imgsz.",
                            true, 10000);
                        return false;
                    }
                    else
                    {
                        fixedInputSize = inputDims[2];
                    }
                }
                else if (inputDims.Any(d => d <= 0))
                {
                    isDynamic = true;
                }

                Log(LogLevel.Info, "Output Metadata:");
                foreach (var kvp in outputMetadata)
                {
                    string dimensionsStr = string.Join("x", kvp.Value.Dimensions);
                    Log(LogLevel.Info, $"  Name: {kvp.Key}, Dimensions: {dimensionsStr}");
                }

                IsDynamicModel = isDynamic;
                CurrentModelIsDynamic = isDynamic;

                if (IsDynamicModel)
                {
                    // For dynamic models, calculate NUM_DETECTIONS based on selected image size
                    NUM_DETECTIONS = CalculateNumDetections(IMAGE_SIZE);
                    LoadClasses();
                    ImageSizeUpdated?.Invoke(IMAGE_SIZE);
                    Log(LogLevel.Info, $"Loaded dynamic model - using selected image size {IMAGE_SIZE}x{IMAGE_SIZE} with {NUM_DETECTIONS} detections", true, 3000);
                }
                else
                {
                    // For fixed models, auto-adjust image size if needed
                    ModelFixedSize = fixedInputSize;

                    // Supported sizes: every multiple of 32 from 160 to 1024 (YOLO stride).
                    var supportedSizes = new[]
                    {
                        "1024", "992", "960", "928", "896", "864", "832", "800",
                        "768", "736", "704", "672", "640", "608", "576", "544",
                        "512", "480", "448", "416", "384", "352", "320", "288",
                        "256", "224", "192", "160"
                    };
                    var fixedSizeStr = fixedInputSize.ToString();

                    if (!supportedSizes.Contains(fixedSizeStr))
                    {
                        Log(LogLevel.Error,
                            $"Model requires unsupported size {fixedInputSize}x{fixedInputSize}. Supported sizes are: {string.Join(", ", supportedSizes)}",
                            true, 10000);
                        return false;
                    }

                    // Always calculate NUM_DETECTIONS based on the model's fixed size
                    NUM_DETECTIONS = CalculateNumDetections(fixedInputSize);
                    _currentImageSize = fixedInputSize;

                    if (fixedInputSize != int.Parse(Dictionary.dropdownState["Image Size"]))
                    {
                        // Auto-adjust the image size to match the model
                        Log(LogLevel.Warning,
                            $"Fixed-size model expects {fixedInputSize}x{fixedInputSize}. Automatically adjusting Image Size setting.",
                            true, 3000);

                        Dictionary.dropdownState["Image Size"] = fixedSizeStr;

                        // Update the UI dropdown if it exists
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                // Find the MainWindow and update the dropdown
                                var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                                mainWindow?.UpdateImageSizeDropdown(fixedSizeStr);
                            }
                            catch { }
                        });
                    }

                    ImageSizeUpdated?.Invoke(fixedInputSize);
                    LoadClasses();
                }

                if (!ConfigureOutputLayout(outputMetadata))
                    return false;

                if (!IsDynamicModel)
                {
                    Log(LogLevel.Info, $"Loaded fixed-size model: {fixedInputSize}x{fixedInputSize}", true, 2000);
                }

                // Notify UI about dynamic model status
                DynamicModelStatusChanged?.Invoke(IsDynamicModel);

                return true;
            }

            return false;
        }

        private bool ConfigureOutputLayout(IReadOnlyDictionary<string, NodeMetadata> outputMetadata)
        {
            string primaryName = _onnxModel!.OutputNames[0];
            // Only the detection head is used; not requesting mask prototypes and other
            // side outputs lets ONNX Runtime skip computing them.
            _outputNames = new List<string> { primaryName };

            string task = GetModelMetadataValue("task") ?? "";
            if (task.Equals("classify", StringComparison.OrdinalIgnoreCase))
            {
                Log(LogLevel.Error,
                    "This is a classification model, not a detection model. Export a YOLO detect, segment, pose or OBB model instead.",
                    true, 10000);
                return false;
            }

            _extraChannels = GetExtraChannels(task, outputMetadata, primaryName);
            _metadataClassCount = _modelClasses.Count > 0 ? _modelClasses.Keys.Max() + 1 : 0;
            _outputLayout = null;
            _layoutErrorLogged = false;

            int[] dims = outputMetadata[primaryName].Dimensions;
            if (ModelOutputLayout.TryResolve(dims, IMAGE_SIZE, _metadataClassCount, _extraChannels, out var layout, out var error))
            {
                ApplyOutputLayout(layout!);
                return true;
            }

            if (error != null)
            {
                Log(LogLevel.Error,
                    $"Unsupported model output {string.Join("x", dims)}: {error}.\nSupported: YOLOv5, v7, v8, v9, v10, v11 and v12 detect / segment / pose / OBB ONNX exports.",
                    true, 10000);
                return false;
            }

            Log(LogLevel.Info, $"Output shape {string.Join("x", dims)} is dynamic; the layout will be detected from the first frame.");
            return true;
        }

        private void ApplyOutputLayout(ModelOutputLayout layout)
        {
            _outputLayout = layout;
            NUM_CLASSES = layout.NumClasses;

            if (_metadataClassCount > 0 && _metadataClassCount != layout.NumClasses && layout.Format != OutputFormat.EndToEnd)
            {
                Log(LogLevel.Warning,
                    $"Model metadata lists {_metadataClassCount} class(es) but the output has {layout.NumClasses}; using the output.");
            }

            // Models with missing or stale class-name metadata still need a name per
            // class so they show up in the Target Class list.
            bool added = false;
            for (int id = 0; id < layout.NumClasses; id++)
            {
                if (_modelClasses.TryAdd(id, $"Class_{id}"))
                    added = true;
            }
            if (added)
                ClassesUpdated?.Invoke(new Dictionary<int, string>(_modelClasses));

            Log(LogLevel.Info, $"Model output layout: {layout.Describe()}");
        }

        private int GetExtraChannels(string task, IReadOnlyDictionary<string, NodeMetadata> outputMetadata, string primaryName)
        {
            // Segmentation heads append one coefficient per mask prototype; the
            // prototype output's channel count says how many.
            int[]? protoDims = outputMetadata
                .Where(o => o.Key != primaryName)
                .Select(o => o.Value.Dimensions)
                .FirstOrDefault(d => d.Length == 4);

            if (task.Equals("segment", StringComparison.OrdinalIgnoreCase) || protoDims != null)
                return protoDims != null && protoDims[1] > 0 ? protoDims[1] : 32;

            if (task.Equals("pose", StringComparison.OrdinalIgnoreCase))
            {
                // kpt_shape is stored like "[17, 3]": keypoints x values per keypoint.
                string kptShape = GetModelMetadataValue("kpt_shape") ?? "[17, 3]";
                int product = 1;
                foreach (var part in kptShape.Split(new[] { '[', ']', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(part, out int value) && value > 0)
                        product *= value;
                }
                return product > 1 ? product : 51;
            }

            if (task.Equals("obb", StringComparison.OrdinalIgnoreCase))
                return 1;

            return 0;
        }

        private string? GetModelMetadataValue(string key)
        {
            try
            {
                var map = _onnxModel?.ModelMetadata?.CustomMetadataMap;
                return map != null && map.TryGetValue(key, out string? value) ? value : null;
            }
            catch
            {
                return null;
            }
        }

        private void LoadClasses()
        {
            if (_onnxModel == null) return;
            _modelClasses.Clear();

            try
            {
                var metadata = _onnxModel.ModelMetadata;

                if (metadata != null &&
                    metadata.CustomMetadataMap.TryGetValue("names", out string? value) &&
                    !string.IsNullOrEmpty(value))
                {
                    JObject data = JObject.Parse(value);
                    if (data != null && data.Type == JTokenType.Object)
                    {
                        //int maxClassId = -1;
                        foreach (var item in data)
                        {
                            if (int.TryParse(item.Key, out int classId) && item.Value.Type == JTokenType.String)
                            {
                                _modelClasses[classId] = item.Value.ToString();
                            }
                        }
                        NUM_CLASSES = _modelClasses.Count > 0 ? _modelClasses.Keys.Max() + 1 : 1;
                        Log(LogLevel.Info, $"Loaded {_modelClasses.Count} class(es) from model metadata: {data.ToString(Newtonsoft.Json.Formatting.None)}", false);
                    }
                    else
                    {
                        Log(LogLevel.Error, "Model metadata 'names' field is not a valid JSON object.", true);
                    }
                }
                else
                {
                    // Class count is read from the output shape instead, with generic names.
                    Log(LogLevel.Warning, "Model metadata does not contain 'names'; using generic class names.");
                }
                ClassesUpdated?.Invoke(new Dictionary<int, string>(_modelClasses));
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Error loading classes: {ex.Message}", true);
            }
        }

        #endregion Models

        #region AI

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldPredict() =>
            Dictionary.toggleState["Show Detected Player"] ||
            Dictionary.toggleState["Constant AI Tracking"] ||
            InputBindingManager.IsHoldingBinding("Aim Keybind") ||
            InputBindingManager.IsHoldingBinding("Second Aim Keybind");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldProcess() =>
            Dictionary.toggleState["Aim Assist"] ||
            Dictionary.toggleState["Show Detected Player"] ||
            Dictionary.toggleState["Auto Trigger"];

        private async void AiLoop()
        {
            Stopwatch stopwatch = new();
            DetectedPlayerWindow? DetectedPlayerOverlay = Dictionary.DetectedPlayerOverlay;

            while (_isAiLoopRunning)
            {
                // Check for pending size changes at the start of each iteration
                lock (_sizeLock)
                {
                    if (_sizeChangePending)
                    {
                        // Skip this iteration to allow clean shutdown
                        continue;
                    }
                }

                stopwatch.Restart();

                // Handle any pending display changes
                _captureManager.HandlePendingDisplayChanges();

                using (Benchmark("AILoopIteration"))
                {
                    UpdateFOV();

                    if (ShouldProcess())
                    {
                        if (ShouldPredict())
                        {
                            Prediction? closestPrediction;
                            using (Benchmark("GetClosestPrediction"))
                            {
                                closestPrediction = await GetClosestPrediction();
                            }

                            CountFrame();

                            if (closestPrediction == null)
                            {
                                DisableOverlay(DetectedPlayerOverlay!);
                                continue;
                            }

                            // Don't auto-fire at a sticky-aim stand-in for a target that isn't visible
                            if (!closestPrediction.IsPredicted)
                            {
                                using (Benchmark("AutoTrigger"))
                                {
                                    await AutoTrigger();
                                }
                            }

                            using (Benchmark("CalculateCoordinates"))
                            {
                                CalculateCoordinates(DetectedPlayerOverlay, closestPrediction, _scaleX, _scaleY);
                            }

                            // Never steer towards a guessed position: the guess can't account for how far the
                            // camera has already turned, so aiming at it pushes the crosshair past the target.
                            // Sticky aim only keeps the lock through the gap; aiming resumes when it's seen again.
                            if (!closestPrediction.IsPredicted)
                            {
                                using (Benchmark("HandleAim"))
                                {
                                    HandleAim(closestPrediction);
                                }
                            }

                            totalTime += stopwatch.ElapsedMilliseconds;
                            iterationCount++;
                        }
                        else
                        {
                            // Processing so we are at the ready but not holding right/click.
                            await Task.Delay(1);
                        }
                    }
                    else
                    {
                        // No work to do—sleep briefly to free up CPU
                        await Task.Delay(1);
                    }
                }

                stopwatch.Stop();
            }
        }

        #region AI Loop Functions

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task AutoTrigger()
        {
            // if auto trigger is disabled,
            // or if the aim keybinds are not held,
            // or if constant AI tracking is enabled,
            // we check for spray release and return
            if (!Dictionary.toggleState["Auto Trigger"] ||
                !(InputBindingManager.IsHoldingBinding("Aim Keybind") && !InputBindingManager.IsHoldingBinding("Second Aim Keybind")) ||
                Dictionary.toggleState["Constant AI Tracking"]) // this logic is a bit weird, but it works.
                                                                // but it might need to be revised
            {
                CheckSprayRelease();
                return;
            }


            if (Dictionary.toggleState["Spray Mode"])
            {
                await MouseManager.DoTriggerClick(LastDetectionBox);
                return;
            }


            if (Dictionary.toggleState["Cursor Check"])
            {
                var mousePos = WinAPICaller.GetCursorPosition();

                if (!DisplayManager.IsPointInCurrentDisplay(new System.Windows.Point(mousePos.X, mousePos.Y)))
                {
                    return;
                }

                if (LastDetectionBox.Contains(mousePos.X, mousePos.Y))
                {
                    await MouseManager.DoTriggerClick(LastDetectionBox);
                }
            }
            else
            {
                await MouseManager.DoTriggerClick();
            }

            if (!Dictionary.toggleState["Aim Assist"] || !Dictionary.toggleState["Show Detected Player"]) return;

        }
        private void CheckSprayRelease()
        {
            if (!Dictionary.toggleState["Spray Mode"]) return;

            // if auto trigger is disabled, we reset the spray state
            // if the aim keybinds are not held, we reset the spray state
            bool shouldSpray = Dictionary.toggleState["Auto Trigger"] &&
                (InputBindingManager.IsHoldingBinding("Aim Keybind") && InputBindingManager.IsHoldingBinding("Second Aim Keybind")); //||
                                                                                                                                     //Dictionary.toggleState["Constant AI Tracking"];

            // spray mode might need to be revised - taylor
            if (!shouldSpray)
            {
                MouseManager.ResetSprayState();
            }
        }

        private async void UpdateFOV()
        {
            if (Dictionary.dropdownState["Detection Area Type"] == "Closest to Mouse" && Dictionary.toggleState["FOV"])
            {
                var mousePosition = WinAPICaller.GetCursorPosition();

                // Check if mouse is on the current display
                if (!DisplayManager.IsPointInCurrentDisplay(new System.Windows.Point(mousePosition.X, mousePosition.Y)))
                {
                    // Mouse is on a different display - don't update FOV position
                    return;
                }

                // Translate mouse position relative to current display
                var displayRelativeX = mousePosition.X - DisplayManager.ScreenLeft;
                var displayRelativeY = mousePosition.Y - DisplayManager.ScreenTop;

                await Application.Current.Dispatcher.BeginInvoke(() =>
                    Dictionary.FOVWindow.FOVStrictEnclosure.Margin = new Thickness(
                        Convert.ToInt16(displayRelativeX / WinAPICaller.scalingFactorX) - 320, // this is based off the window size, not the size of the model -whip
                        Convert.ToInt16(displayRelativeY / WinAPICaller.scalingFactorY) - 320, 0, 0));
            }
        }

        private static void DisableOverlay(DetectedPlayerWindow DetectedPlayerOverlay)
        {
            if (Dictionary.toggleState["Show Detected Player"] && Dictionary.DetectedPlayerOverlay != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (Dictionary.toggleState["Show AI Confidence"])
                    {
                        DetectedPlayerOverlay!.DetectedPlayerConfidence.Opacity = 0;
                    }

                    if (Dictionary.toggleState["Show Tracers"])
                    {
                        DetectedPlayerOverlay!.DetectedTracers.Opacity = 0;
                    }

                    DetectedPlayerOverlay!.DetectedPlayerFocus.Opacity = 0;
                });
            }
        }

        private void UpdateOverlay(DetectedPlayerWindow DetectedPlayerOverlay, Prediction closestPrediction)
        {
            var scalingFactorX = WinAPICaller.scalingFactorX;
            var scalingFactorY = WinAPICaller.scalingFactorY;

            // Convert screen coordinates to display-relative coordinates
            var displayRelativeX = LastDetectionBox.X - DisplayManager.ScreenLeft;
            var displayRelativeY = LastDetectionBox.Y - DisplayManager.ScreenTop;

            // Calculate center position in display-relative coordinates
            var centerX = Convert.ToInt16(displayRelativeX / scalingFactorX) + (LastDetectionBox.Width / 2.0);
            var centerY = Convert.ToInt16(displayRelativeY / scalingFactorY);

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Dictionary.toggleState["Show AI Confidence"])
                {
                    DetectedPlayerOverlay.DetectedPlayerConfidence.Opacity = 1;
                    DetectedPlayerOverlay.DetectedPlayerConfidence.Content = $"{closestPrediction.ClassName}: {Math.Round((AIConf * 100), 2)}%";

                    var labelEstimatedHalfWidth = DetectedPlayerOverlay.DetectedPlayerConfidence.ActualWidth / 2.0;
                    DetectedPlayerOverlay.DetectedPlayerConfidence.Margin = new Thickness(
                        centerX - labelEstimatedHalfWidth,
                        centerY - DetectedPlayerOverlay.DetectedPlayerConfidence.ActualHeight - 2, 0, 0);
                }
                var showTracers = Dictionary.toggleState["Show Tracers"];
                DetectedPlayerOverlay.DetectedTracers.Opacity = showTracers ? 1 : 0;
                if (showTracers)
                {
                    var tracerPosition = Dictionary.dropdownState["Tracer Position"];

                    var boxTop = centerY;
                    var boxBottom = centerY + LastDetectionBox.Height;
                    var boxHorizontalCenter = centerX;
                    var boxVerticalCenter = centerY + (LastDetectionBox.Height / 2.0);
                    var boxLeft = centerX - (LastDetectionBox.Width / 2.0);
                    var boxRight = centerX + (LastDetectionBox.Width / 2.0);

                    switch (tracerPosition)
                    {
                        case "Top":
                            DetectedPlayerOverlay.DetectedTracers.X2 = boxHorizontalCenter;
                            DetectedPlayerOverlay.DetectedTracers.Y2 = boxTop;
                            break;

                        case "Bottom":
                            DetectedPlayerOverlay.DetectedTracers.X2 = boxHorizontalCenter;
                            DetectedPlayerOverlay.DetectedTracers.Y2 = boxBottom;
                            break;

                        case "Middle":
                            var screenHorizontalCenter = DisplayManager.ScreenWidth / (2.0 * WinAPICaller.scalingFactorX);
                            if (boxHorizontalCenter < screenHorizontalCenter)
                            {
                                // if the box is on the left half of the screen, aim for the right-middle of the box
                                DetectedPlayerOverlay.DetectedTracers.X2 = boxRight;
                                DetectedPlayerOverlay.DetectedTracers.Y2 = boxVerticalCenter;
                            }
                            else
                            {
                                // if the box is on the right half, aim for the left-middle
                                DetectedPlayerOverlay.DetectedTracers.X2 = boxLeft;
                                DetectedPlayerOverlay.DetectedTracers.Y2 = boxVerticalCenter;
                            }
                            break;

                        default:
                            // default to the bottom-center if the setting is unrecognized
                            DetectedPlayerOverlay.DetectedTracers.X2 = boxHorizontalCenter;
                            DetectedPlayerOverlay.DetectedTracers.Y2 = boxBottom;
                            break;
                    }
                }

                DetectedPlayerOverlay.Opacity = Dictionary.sliderSettings["Opacity"];

                DetectedPlayerOverlay.DetectedPlayerFocus.Opacity = 1;
                DetectedPlayerOverlay.DetectedPlayerFocus.Margin = new Thickness(
                    centerX - (LastDetectionBox.Width / 2.0), centerY, 0, 0);
                DetectedPlayerOverlay.DetectedPlayerFocus.Width = LastDetectionBox.Width;
                DetectedPlayerOverlay.DetectedPlayerFocus.Height = LastDetectionBox.Height;
            });
        }

        private void CalculateCoordinates(DetectedPlayerWindow DetectedPlayerOverlay, Prediction closestPrediction, float scaleX, float scaleY)
        {
            AIConf = closestPrediction.Confidence;

            if (Dictionary.toggleState["Show Detected Player"] && Dictionary.DetectedPlayerOverlay != null)
            {
                using (Benchmark("UpdateOverlay"))
                {
                    UpdateOverlay(DetectedPlayerOverlay!, closestPrediction);
                }
                if (!Dictionary.toggleState["Aim Assist"]) return;
            }

            double YOffset = Dictionary.sliderSettings["Y Offset (Up/Down)"];
            double XOffset = Dictionary.sliderSettings["X Offset (Left/Right)"];

            double YOffsetPercentage = Dictionary.sliderSettings["Y Offset (%)"];
            double XOffsetPercentage = Dictionary.sliderSettings["X Offset (%)"];

            var rect = closestPrediction.Rectangle;

            if (Dictionary.toggleState["X Axis Percentage Adjustment"])
            {
                detectedX = (int)((rect.X + (rect.Width * (XOffsetPercentage / 100))) * scaleX);
            }
            else
            {
                detectedX = (int)((rect.X + rect.Width / 2) * scaleX + XOffset);
            }

            if (Dictionary.toggleState["Y Axis Percentage Adjustment"])
            {
                detectedY = (int)((rect.Y + rect.Height - (rect.Height * (YOffsetPercentage / 100))) * scaleY + YOffset);
            }
            else
            {
                detectedY = CalculateDetectedY(scaleY, YOffset, closestPrediction);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CalculateDetectedY(float scaleY, double YOffset, Prediction closestPrediction)
        {
            var rect = closestPrediction.Rectangle;
            float yBase = rect.Y;
            float yAdjustment = 0;

            switch (Dictionary.dropdownState["Aiming Boundaries Alignment"])
            {
                case "Center":
                    yAdjustment = rect.Height / 2;
                    break;

                case "Top":
                    // yBase is already at the top
                    break;

                case "Bottom":
                    yAdjustment = rect.Height;
                    break;
            }

            return (int)((yBase + yAdjustment) * scaleY + YOffset);
        }

        private void HandleAim(Prediction closestPrediction)
        {
            if (Dictionary.toggleState["Aim Assist"] &&
                (Dictionary.toggleState["Constant AI Tracking"] ||
                 Dictionary.toggleState["Aim Assist"] && InputBindingManager.IsHoldingBinding("Aim Keybind") ||
                 Dictionary.toggleState["Aim Assist"] && InputBindingManager.IsHoldingBinding("Second Aim Keybind")))
            {
                if (Dictionary.toggleState["Predictions"])
                {
                    HandlePredictions(kalmanPrediction, closestPrediction, detectedX, detectedY);
                }
                else
                {
                    MouseManager.MoveCrosshair(detectedX, detectedY);
                }
            }
        }

        private void HandlePredictions(KalmanPrediction kalmanPrediction, Prediction closestPrediction, int detectedX, int detectedY)
        {
            var predictionMethod = Dictionary.dropdownState["Prediction Method"];
            switch (predictionMethod)
            {
                case "Kalman Filter":
                    KalmanPrediction.Detection detection = new()
                    {
                        X = detectedX,
                        Y = detectedY,
                        Timestamp = DateTime.UtcNow
                    };

                    kalmanPrediction.UpdateKalmanFilter(detection);
                    var predictedPosition = kalmanPrediction.GetKalmanPosition();

                    MouseManager.MoveCrosshair(predictedPosition.X, predictedPosition.Y);
                    break;

                case "Shall0e's Prediction":
                    // Update position (calculates velocity internally)
                    ShalloePredictionV2.UpdatePosition(detectedX, detectedY);

                    // Get predicted position
                    MouseManager.MoveCrosshair(ShalloePredictionV2.GetSPX(), ShalloePredictionV2.GetSPY());
                    break;

                case "wisethef0x's EMA Prediction":
                    WiseTheFoxPrediction.WTFDetection wtfdetection = new()
                    {
                        X = detectedX,
                        Y = detectedY,
                        Timestamp = DateTime.UtcNow
                    };

                    wtfpredictionManager.UpdateDetection(wtfdetection);
                    var wtfpredictedPosition = wtfpredictionManager.GetEstimatedPosition();

                    // Use both predicted X and Y
                    MouseManager.MoveCrosshair(wtfpredictedPosition.X, wtfpredictedPosition.Y);
                    break;
            }
        }

        private async Task<Prediction?> GetClosestPrediction(bool useMousePosition = true)
        {
            //whats these variables for? - taylor 
            //int adjustedTargetX, adjustedTargetY;

            if (Dictionary.dropdownState["Detection Area Type"] == "Closest to Mouse")
            {
                var mousePos = WinAPICaller.GetCursorPosition();

                // Check if mouse is on the current display
                if (DisplayManager.IsPointInCurrentDisplay(new System.Windows.Point(mousePos.X, mousePos.Y)))
                {
                    // Mouse is on current display, use its position
                    targetX = mousePos.X;
                    targetY = mousePos.Y;
                }
                else
                {
                    // Mouse is on different display, use center of current display
                    targetX = DisplayManager.ScreenLeft + (DisplayManager.ScreenWidth / 2);
                    targetY = DisplayManager.ScreenTop + (DisplayManager.ScreenHeight / 2);
                }
            }
            else
            {
                // Center of current display
                targetX = DisplayManager.ScreenLeft + (DisplayManager.ScreenWidth / 2);
                targetY = DisplayManager.ScreenTop + (DisplayManager.ScreenHeight / 2);
            }

            Rectangle detectionBox = new(targetX - IMAGE_SIZE / 2, targetY - IMAGE_SIZE / 2, IMAGE_SIZE, IMAGE_SIZE); // Detection box dynamic size

            Bitmap? frame;

            using (Benchmark("ScreenGrab"))
            {
                frame = _captureManager.ScreenGrab(detectionBox);
            }

            if (frame == null) return null;

            IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
            Tensor<float>? outputTensor = null;

            try
            {
                // Dispose() takes this lock too, so the model can't be freed mid-inference
                lock (_inferenceLock)
                {
                    if (_disposed) return null;
                    var graph = _graphRunner;
                    if (graph != null)
                    {
                        // CUDA graph: write the frame straight into the page-locked input buffer, then replay
                        using (Benchmark("BitmapToFloatArray"))
                        {
                            BitmapToFloatArrayInPlace(frame, graph.Input, IMAGE_SIZE);
                        }
                        using (Benchmark("ModelInference"))
                        {
                            graph.Run();
                            outputTensor = graph.Output;
                        }
                    }
                    else
                    {
                        float[] inputArray;
                        using (Benchmark("BitmapToFloatArray"))
                        {
                            if (_reusableInputArray == null || _reusableInputArray.Length != 3 * IMAGE_SIZE * IMAGE_SIZE)
                            {
                                _reusableInputArray = new float[3 * IMAGE_SIZE * IMAGE_SIZE];
                            }
                            inputArray = _reusableInputArray;

                            // Fill the reusable array
                            BitmapToFloatArrayInPlace(frame, inputArray, IMAGE_SIZE);
                        }

                        // Reuse tensor and inputs - recreate if size changed
                        /// this needs to be revised !!!!! - taylor
                        if (_reusableTensor == null || _reusableTensor.Dimensions[2] != IMAGE_SIZE)
                        {
                            _reusableTensor = new DenseTensor<float>(inputArray, new int[] { 1, 3, IMAGE_SIZE, IMAGE_SIZE });
                            _reusableInputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, _reusableTensor) };
                        }
                        else
                        {
                            // Directly copy into existing DenseTensor buffer
                            inputArray.AsSpan().CopyTo(_reusableTensor.Buffer.Span);
                        }

                        if (_onnxModel == null) return null;
                        using (Benchmark("ModelInference"))
                        {
                            results = _onnxModel.Run(_reusableInputs, _outputNames, _modeloptions);
                            outputTensor = results[0].AsTensor<float>();
                        }
                    }
                }

                if (outputTensor == null)
                {
                    Log(LogLevel.Error, "Model inference returned null output tensor.", true, 2000);
                    SaveFrame(frame);
                    return null;
                }

                if (_outputLayout == null || !_outputLayout.Matches(outputTensor.Dimensions))
                {
                    if (!ModelOutputLayout.TryResolve(outputTensor.Dimensions, IMAGE_SIZE, _metadataClassCount, _extraChannels, out var layout, out var layoutError))
                    {
                        if (!_layoutErrorLogged)
                        {
                            _layoutErrorLogged = true;
                            Log(LogLevel.Error,
                                $"Unsupported model output {string.Join("x", outputTensor.Dimensions.ToArray())}: {layoutError ?? "shape could not be determined"}.",
                                true, 10000);
                        }
                        return null;
                    }
                    ApplyOutputLayout(layout!);
                }

                // Calculate the FOV boundaries
                float FovSize = (float)Dictionary.sliderSettings["FOV Size"];
                float fovMinX = (IMAGE_SIZE - FovSize) / 2.0f;
                float fovMaxX = (IMAGE_SIZE + FovSize) / 2.0f;
                float fovMinY = (IMAGE_SIZE - FovSize) / 2.0f;
                float fovMaxY = (IMAGE_SIZE + FovSize) / 2.0f;

                //List<double[]> KDpoints;
                List<Prediction> KDPredictions;
                using (Benchmark("PrepareKDTreeData"))
                {
                    KDPredictions = PrepareKDTreeData(outputTensor, detectionBox, fovMinX, fovMaxX, fovMinY, fovMaxY);
                }

                if (KDPredictions.Count == 0)
                {
                    SaveFrame(frame);
                    // Sticky aim keeps following a target that just dropped out for a moment
                    Prediction? held = HandleStickyAim(null, KDPredictions, detectionBox);
                    if (held != null) UpdateDetectionBox(held, detectionBox);
                    return held;
                }

                //kdtree was replaced with linear search
                Prediction? bestCandidate = null;
                double bestDistSq = double.MaxValue;
                double center = IMAGE_SIZE / 2.0;

                // TODO: Optimize this linear search further if needed
                // TODO: Consider updating KD-Tree and adding options to switch from linear to kd.
                // we can honestly replacing linear search by letting sticky aim handle the search
                using (Benchmark("LinearSearch"))
                {
                    foreach (var p in KDPredictions)
                    {
                        var dx = p.CenterXTranslated * IMAGE_SIZE - center;
                        var dy = p.CenterYTranslated * IMAGE_SIZE - center;
                        double d2 = dx * dx + dy * dy; // dx^2 + dy^2

                        if (d2 < bestDistSq) { bestDistSq = d2; bestCandidate = p; }
                    }
                }

                Prediction? finalTarget = HandleStickyAim(bestCandidate, KDPredictions, detectionBox);
                if (finalTarget != null)
                {
                    UpdateDetectionBox(finalTarget, detectionBox);
                    SaveFrame(frame, finalTarget.IsPredicted ? null : finalTarget); // never auto-label a guessed box
                    return finalTarget;
                }

                return null;
            }
            finally
            {
                // Always dispose the cloned frame to prevent memory leaks
                frame.Dispose();
                results?.Dispose();
            }
        }

        private Prediction? HandleStickyAim(Prediction? bestCandidate, List<Prediction> detections, Rectangle detectionBox)
        {
            if (!Dictionary.toggleState["Sticky Aim"])
            {
                _stickyTracker.Reset();
                return bestCandidate;
            }

            float switchRadius = (float)Dictionary.sliderSettings["Sticky Aim Threshold"];
            return _stickyTracker.Update(detections, detectionBox.Left, detectionBox.Top, IMAGE_SIZE,
                switchRadius, Stopwatch.GetTimestamp());
        }
        private void UpdateDetectionBox(Prediction target, Rectangle detectionBox)
        {
            float translatedXMin = target.Rectangle.X + detectionBox.Left;
            float translatedYMin = target.Rectangle.Y + detectionBox.Top;
            LastDetectionBox = new(translatedXMin, translatedYMin,
                target.Rectangle.Width, target.Rectangle.Height);

            CenterXTranslated = target.CenterXTranslated;
            CenterYTranslated = target.CenterYTranslated;
        }
        // is it really kdtreedata though....
        private List<Prediction> PrepareKDTreeData(
            Tensor<float> outputTensor,
            Rectangle detectionBox,
            float fovMinX, float fovMaxX, float fovMinY, float fovMaxY)
        {
            float minConfidence = (float)Dictionary.sliderSettings["AI Minimum Confidence"] / 100.0f;
            string selectedClass = Dictionary.dropdownState["Target Class"];
            int selectedClassId = selectedClass == "Best Confidence" ? -1 : _modelClasses.FirstOrDefault(c => c.Value == selectedClass).Key;

            var layout = _outputLayout!;
            int numClasses = layout.NumClasses;
            if (selectedClassId >= numClasses && layout.Format != OutputFormat.EndToEnd)
                selectedClassId = -1;

            ReadOnlySpan<int> dims = outputTensor.Dimensions;
            int boxCount = layout.BoxCount(dims);
            // Read the flat buffer directly (batch 0 is first); the multi-index
            // accessor recomputes strides on every element.
            ReadOnlySpan<float> data = outputTensor is DenseTensor<float> dense
                ? dense.Buffer.Span
                : outputTensor.ToArray();
            int channelStride = layout.ChannelsFirst ? boxCount : 1;
            int boxStride = layout.ChannelsFirst ? 1 : layout.Channels;
            int classStart = layout.ClassStart;

            var KDpredictions = new List<Prediction>();

            for (int i = 0; i < boxCount; i++)
            {
                int b = i * boxStride;

                int bestClassId = 0;
                float bestConfidence = 0f;
                float x_center, y_center, width, height;

                if (layout.Format == OutputFormat.EndToEnd)
                {
                    bestConfidence = data[b + 4 * channelStride];
                    if (bestConfidence < minConfidence) continue;

                    bestClassId = (int)data[b + 5 * channelStride];
                    if (selectedClassId >= 0 && bestClassId != selectedClassId) continue;

                    float x1 = data[b];
                    float y1 = data[b + channelStride];
                    float x2 = data[b + 2 * channelStride];
                    float y2 = data[b + 3 * channelStride];
                    x_center = (x1 + x2) / 2;
                    y_center = (y1 + y2) / 2;
                    width = x2 - x1;
                    height = y2 - y1;
                }
                else
                {
                    float objectness = 1f;
                    if (layout.Format == OutputFormat.Objectness)
                    {
                        objectness = data[b + 4 * channelStride];
                        if (objectness < minConfidence) continue;
                    }

                    if (numClasses == 1)
                    {
                        bestConfidence = data[b + classStart * channelStride];
                    }
                    else if (selectedClassId >= 0)
                    {
                        bestConfidence = data[b + (classStart + selectedClassId) * channelStride];
                        bestClassId = selectedClassId;
                    }
                    else
                    {
                        for (int classId = 0; classId < numClasses; classId++)
                        {
                            float classConfidence = data[b + (classStart + classId) * channelStride];
                            if (classConfidence > bestConfidence)
                            {
                                bestConfidence = classConfidence;
                                bestClassId = classId;
                            }
                        }
                    }

                    bestConfidence *= objectness;
                    if (bestConfidence < minConfidence) continue;

                    x_center = data[b];
                    y_center = data[b + channelStride];
                    width = data[b + 2 * channelStride];
                    height = data[b + 3 * channelStride];
                }

                // Some exports emit boxes normalized to 0..1 instead of input pixels;
                // a real box can't be under a pixel wide, so this can't misfire.
                if (width <= 1f && height <= 1f && x_center <= 1f && y_center <= 1f)
                {
                    x_center *= IMAGE_SIZE;
                    y_center *= IMAGE_SIZE;
                    width *= IMAGE_SIZE;
                    height *= IMAGE_SIZE;
                }

                float x_min = x_center - width / 2;
                float y_min = y_center - height / 2;
                float x_max = x_center + width / 2;
                float y_max = y_center + height / 2;

                if (x_min < fovMinX || x_max > fovMaxX || y_min < fovMinY || y_max > fovMaxY) continue;

                RectangleF rect = new(x_min, y_min, width, height);
                Prediction prediction = new()
                {
                    Rectangle = rect,
                    Confidence = bestConfidence,
                    ClassId = bestClassId,
                    ClassName = _modelClasses.GetValueOrDefault(bestClassId, $"Class_{bestClassId}"),
                    CenterXTranslated = x_center / IMAGE_SIZE,
                    CenterYTranslated = y_center / IMAGE_SIZE,
                    ScreenCenterX = detectionBox.Left + x_center,
                    ScreenCenterY = detectionBox.Top + y_center
                };

                //KDpoints.Add(new double[] { x_center, y_center });
                KDpredictions.Add(prediction);
            }

            return KDpredictions;
        }

        #endregion AI Loop Functions

        #endregion AI

        #region Screen Capture

        private void SaveFrame(Bitmap frame, Prediction? DoLabel = null)
        {
            // Only save frames if "Collect Data While Playing" is enabled
            if (!Dictionary.toggleState["Collect Data While Playing"]) return;

            // Skip if we're in constant tracking mode (unless auto-labeling is enabled)
            if (Dictionary.toggleState["Constant AI Tracking"] && !Dictionary.toggleState["Auto Label Data"]) return;

            // Cooldown check
            if ((DateTime.Now - lastSavedTime).TotalMilliseconds < SAVE_FRAME_COOLDOWN_MS) return;

            try
            {
                // Validate bitmap is still usable
                if (frame == null) return;

                // Accessing Width/Height will throw if bitmap is disposed
                int width = frame.Width;
                int height = frame.Height;
                if (width <= 0 || height <= 0) return;

                lastSavedTime = DateTime.Now;
                string uuid = Guid.NewGuid().ToString();
                string imagePath = Path.Combine("bin", "images", $"{uuid}.jpg");

                // Save synchronously to avoid "Object is currently in use elsewhere" error
                frame.Save(imagePath, ImageFormat.Jpeg);

                if (Dictionary.toggleState["Auto Label Data"] && DoLabel != null)
                {
                    var labelPath = Path.Combine("bin", "labels", $"{uuid}.txt");

                    float x = (DoLabel!.Rectangle.X + DoLabel.Rectangle.Width / 2) / width;
                    float y = (DoLabel!.Rectangle.Y + DoLabel.Rectangle.Height / 2) / height;
                    float labelWidth = DoLabel.Rectangle.Width / width;
                    float labelHeight = DoLabel.Rectangle.Height / height;

                    File.WriteAllText(labelPath, $"{DoLabel.ClassId} {x} {y} {labelWidth} {labelHeight}");
                }
            }
            catch (ArgumentException)
            {
                // Bitmap was disposed or invalid - silently ignore
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"SaveFrame failed: {ex.Message}");
            }
        }



        #endregion Screen Capture

        public void Dispose()
        {
            // Signal that we're shutting down
            lock (_sizeLock)
            {
                _sizeChangePending = true;
            }

            // Stop the loop
            _isAiLoopRunning = false;
            if (_aiLoopThread != null && _aiLoopThread.IsAlive)
            {
                if (!_aiLoopThread.Join(TimeSpan.FromSeconds(1)))
                {
                    try { _aiLoopThread.Interrupt(); }
                    catch { }
                }
            }

            // Print final benchmarks
            PrintBenchmarks();

            // Dispose DXGI objects
            _captureManager.Dispose();

            // Clean up other resources
            _reusableInputArray = null;
            _reusableInputs = null;
            lock (_inferenceLock)
            {
                _disposed = true;
                if (_graphRunner != null)
                {
                    _graphRunner.Dispose(); // also disposes its session (_onnxModel)
                    _graphRunner = null;
                }
                else
                {
                    _onnxModel?.Dispose();
                }
                _onnxModel = null;
            }
            _modeloptions?.Dispose();
            _bitmapBuffer = null;
        }
    }
    public class Prediction
    {
        public RectangleF Rectangle { get; set; }
        public float Confidence { get; set; }
        public int ClassId { get; set; } = 0;
        public string ClassName { get; set; } = "Enemy";
        public float CenterXTranslated { get; set; }
        public float CenterYTranslated { get; set; }
        public float ScreenCenterX { get; set; }  // Absolute screen position
        public float ScreenCenterY { get; set; }
        /// <summary>True when sticky aim filled in a briefly missing target from its predicted position.</summary>
        public bool IsPredicted { get; set; }
    }
}

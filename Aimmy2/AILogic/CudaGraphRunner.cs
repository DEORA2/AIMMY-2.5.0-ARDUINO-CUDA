using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Whales2.AILogic
{
    /// <summary>
    /// Runs a model on CUDA with a CUDA graph: the GPU work for one inference is recorded once and then
    /// replayed every frame, which removes most of the per-run launch overhead (on an RTX 4080 Laptop a
    /// 640 YOLO model went from ~240 to ~400 runs/s).
    ///
    /// A CUDA graph needs fixed input/output addresses on the GPU, so the frame is written into a
    /// page-locked host buffer (<see cref="Input"/>), copied to a fixed GPU buffer, the graph is replayed,
    /// and the first output is copied back into <see cref="Output"/>. Only fixed input sizes work, which
    /// is fine here: changing the image size creates a new AIManager (and runner).
    /// </summary>
    internal sealed unsafe class CudaGraphRunner : IDisposable
    {
        // cudart is already loaded by ONNX Runtime's CUDA provider (CUDA 12 runtime on PATH)
        [DllImport("cudart64_12.dll")] private static extern int cudaMemcpy(IntPtr dst, IntPtr src, nuint count, int kind);
        [DllImport("cudart64_12.dll")] private static extern int cudaMallocHost(out IntPtr ptr, nuint size);
        [DllImport("cudart64_12.dll")] private static extern int cudaFreeHost(IntPtr ptr);
        private const int HostToDevice = 1, DeviceToHost = 2;

        public InferenceSession Session { get; }
        public string InputName { get; }
        public string OutputName { get; }
        /// <summary>Page-locked input buffer, NCHW float [1, 3, size, size]. Fill it, then call <see cref="Run"/>.</summary>
        public Span<float> Input => new((void*)_inputHost, _inputLength);
        /// <summary>First model output, refreshed by <see cref="Run"/>.</summary>
        public DenseTensor<float> Output { get; }

        private readonly OrtMemoryInfo _gpuInfo;
        private readonly OrtAllocator _gpuAllocator;
        private readonly OrtIoBinding _binding;
        private readonly RunOptions _runOptions = new();
        private readonly OrtValue _inputGpu;
        private readonly List<OrtValue> _outputsGpu = new();
        private readonly IntPtr _inputHost;
        private readonly int _inputLength;
        private readonly float[] _outputHost;
        private readonly GCHandle _outputHandle;
        private bool _disposed;

        private CudaGraphRunner(InferenceSession session, int imageSize, Dictionary<string, long[]> outputShapes)
        {
            Session = session;
            InputName = session.InputNames[0];
            OutputName = session.OutputNames[0];

            _gpuInfo = new OrtMemoryInfo("Cuda", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
            _gpuAllocator = new OrtAllocator(session, _gpuInfo);

            long[] inputShape = { 1, 3, imageSize, imageSize };
            _inputLength = 3 * imageSize * imageSize;
            _inputGpu = OrtValue.CreateAllocatedTensorValue(_gpuAllocator, TensorElementType.Float, inputShape);
            Check(cudaMallocHost(out _inputHost, (nuint)(_inputLength * sizeof(float))), "cudaMallocHost");
            Input.Clear();

            _binding = session.CreateIoBinding();
            _binding.BindInput(InputName, _inputGpu);
            foreach (var name in session.OutputNames)
            {
                var value = OrtValue.CreateAllocatedTensorValue(_gpuAllocator, TensorElementType.Float, outputShapes[name]);
                _outputsGpu.Add(value);
                _binding.BindOutput(name, value);
            }

            var primary = outputShapes[OutputName];
            _outputHost = new float[primary.Aggregate(1L, (a, d) => a * d)];
            _outputHandle = GCHandle.Alloc(_outputHost, GCHandleType.Pinned);
            Output = new DenseTensor<float>(_outputHost, primary.Select(d => (int)d).ToArray());
        }

        /// <summary>
        /// Creates a CUDA graph session for a model with a float input of [1, 3, size, size].
        /// Returns null (with a reason) if the model or setup can't use CUDA graphs; use a normal session then.
        /// </summary>
        public static CudaGraphRunner? TryCreate(string modelPath, int imageSize, IDictionary<string, string> cudaOptions,
            Func<SessionOptions> makeSessionOptions, out string? reason)
        {
            reason = null;
            InferenceSession? session = null;
            CudaGraphRunner? runner = null;
            try
            {
                var outputShapes = GetOutputShapes(modelPath, imageSize, makeSessionOptions, out reason);
                if (outputShapes == null) return null;

                using var options = makeSessionOptions();
                using (var cuda = new OrtCUDAProviderOptions())
                {
                    var settings = new Dictionary<string, string>(cudaOptions) { ["enable_cuda_graph"] = "1" };
                    cuda.UpdateOptions(settings);
                    options.AppendExecutionProvider_CUDA(cuda);
                }
                session = new InferenceSession(modelPath, options);
                if (session.InputMetadata[session.InputNames[0]].ElementDataType != TensorElementType.Float ||
                    session.OutputNames.Any(n => session.OutputMetadata[n].ElementDataType != TensorElementType.Float))
                {
                    reason = "model inputs/outputs are not float32";
                    session.Dispose();
                    return null;
                }

                runner = new CudaGraphRunner(session, imageSize, outputShapes);
                // Warm-up runs; ONNX Runtime records the graph during these
                for (int i = 0; i < 3; i++) runner.Run();
                return runner;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                if (runner != null) runner.Dispose();
                else session?.Dispose();
                return null;
            }
        }

        /// <summary>Copies <see cref="Input"/> to the GPU, replays the graph and copies the first output back.</summary>
        public void Run()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Check(cudaMemcpy(DevicePointer(_inputGpu), _inputHost, (nuint)(_inputLength * sizeof(float)), HostToDevice), "copy to GPU");
            Session.RunWithBinding(_runOptions, _binding);
            Check(cudaMemcpy(_outputHandle.AddrOfPinnedObject(), DevicePointer(_outputsGpu[0]),
                (nuint)(_outputHost.Length * sizeof(float)), DeviceToHost), "copy from GPU");
        }

        /// <summary>
        /// Output shapes for this input size. Fixed shapes come from the model; dynamic ones are found with
        /// one CPU run (CUDA graphs need every output buffer allocated up front).
        /// </summary>
        private static Dictionary<string, long[]>? GetOutputShapes(string modelPath, int imageSize,
            Func<SessionOptions> makeSessionOptions, out string? reason)
        {
            reason = null;
            using (var probeOptions = makeSessionOptions())
            using (var meta = new InferenceSession(modelPath, probeOptions))
            {
                var fixedShapes = meta.OutputNames.ToDictionary(n => n, n => meta.OutputMetadata[n].Dimensions.Select(d => (long)d).ToArray());
                var inputDims = meta.InputMetadata[meta.InputNames[0]].Dimensions;
                bool inputFits = inputDims.Length == 4 && (inputDims[2] <= 0 || inputDims[2] == imageSize);
                if (!inputFits)
                {
                    reason = $"model input {string.Join("x", inputDims)} does not match {imageSize}";
                    return null;
                }
                if (fixedShapes.Values.All(s => s.All(d => d > 0)))
                    return fixedShapes;

                // Dynamic output: run once on the CPU to see the real shapes for this size
                var input = new DenseTensor<float>(new[] { 1, 3, imageSize, imageSize });
                using var results = meta.Run(new[] { NamedOnnxValue.CreateFromTensor(meta.InputNames[0], input) });
                return results.ToDictionary(r => r.Name, r => r.AsTensor<float>().Dimensions.ToArray().Select(d => (long)d).ToArray());
            }
        }

        private static IntPtr DevicePointer(OrtValue value)
        {
            // The span points at GPU memory: only its address is used, it is never read or written
            var span = value.GetTensorMutableRawData();
            return (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));
        }

        private static void Check(int cudaError, string what)
        {
            if (cudaError != 0) throw new InvalidOperationException($"CUDA {what} failed (error {cudaError})");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _binding.Dispose();
            _inputGpu.Dispose();
            foreach (var v in _outputsGpu) v.Dispose();
            _gpuAllocator.Dispose();
            _gpuInfo.Dispose();
            _runOptions.Dispose();
            Session.Dispose();
            if (_inputHost != IntPtr.Zero) cudaFreeHost(_inputHost);
            if (_outputHandle.IsAllocated) _outputHandle.Free();
        }
    }
}

using Whales2.Class;
using Other;
using SharpGen.Runtime;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using LogLevel = Other.LogManager.LogLevel;

namespace AILogic
{
    internal enum CaptureStatus { NewFrame, NoNewFrame, Failed }

    internal class CaptureManager
    {
        #region Variables
        private string _currentCaptureMethod = ""; // Track current method
        private bool _directXFailedPermanently = false; // Track if DirectX failed with unsupported error
        private bool _notificationShown = false; // Prevent spam notifications

        // Capturing
        public Bitmap? screenCaptureBitmap { get; private set; }
        public Bitmap? directXBitmap { get; private set; }
        private ID3D11Device? _dxDevice;
        private IDXGIOutputDuplication? _deskDuplication;
        private ID3D11Texture2D? _stagingTex;

        // How long DirectX capture waits for the screen to change before reporting NoNewFrame.
        // A new frame is returned as soon as it arrives, so this adds no delay.
        private const uint NewFrameWaitMs = 10;

        /// <summary>Time the last successful capture spent copying and converting pixels (Stopwatch ticks, excludes waiting).</summary>
        public long LastCopyTicks { get; private set; }


        // Display change handling
        public readonly object _displayLock = new();
        public bool _displayChangesPending { get; set; } = false;

        // Performance tracking
        private int _consecutiveFailures = 0;
        private const int MAX_CONSECUTIVE_FAILURES = 5;

        #endregion
        #region Handlers
        public CaptureManager()
        {
            // Subscribe to display changes FIRST
            DisplayManager.DisplayChanged += OnDisplayChanged;
        }

        private void OnDisplayChanged(object? sender, DisplayChangedEventArgs e)
        {
            lock (_displayLock)
            {
                _displayChangesPending = true;
                _consecutiveFailures = 0;
                DisposeDxgiResources();
            }
            LogManager.Log(LogLevel.Info, "Display change detected. DirectX resources will be reinitialized.");
        }

        public void HandlePendingDisplayChanges()
        {
            lock (_displayLock)
            {
                if (!_displayChangesPending) return;

                try
                {
                    InitializeDxgiDuplication();
                    _displayChangesPending = false;
                }
                catch (Exception ex)
                {

                }
            }
        }

        #endregion
        #region DirectX
        public void InitializeDxgiDuplication()
        {
            DisposeDxgiResources();
            try
            {
                var currentDisplay = DisplayManager.CurrentDisplay;
                if (currentDisplay == null)
                {
                    LogManager.Log(LogLevel.Error, "No current display available. DisplayManager may not be initialized.");
                    throw new InvalidOperationException("No current display available. DisplayManager may not be initialized.");
                }

                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                IDXGIOutput1? targetOutput1 = null;
                IDXGIAdapter1? targetAdapter = null;
                bool foundTarget = false;

                for (uint adapterIndex = 0;
                    factory.EnumAdapters1(adapterIndex, out var adapter).Success;
                    adapterIndex++)
                {
                    LogManager.Log(LogLevel.Info, $"Checking Adapter {adapterIndex}: {adapter.Description.Description.TrimEnd('\0')}");

                    for (uint outputIndex = 0;
                        adapter.EnumOutputs(outputIndex, out var output).Success;
                        outputIndex++)
                    {
                        using (output)
                        {
                            var output1 = output.QueryInterface<IDXGIOutput1>();
                            var outputDesc = output1.Description;
                            var outputBounds = new Vortice.Mathematics.Rect(
                                outputDesc.DesktopCoordinates.Left,
                                outputDesc.DesktopCoordinates.Top,
                                outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left,
                                outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top);
                            LogManager.Log(LogLevel.Info, $"Found Output {outputIndex}: DeviceName = '{outputDesc.DeviceName.TrimEnd('\0')}', Bounds = {outputBounds}");

                            // Try different matching strategies
                            bool nameMatch = currentDisplay?.DeviceName != null && outputDesc.DeviceName.TrimEnd('\0') == currentDisplay.DeviceName.TrimEnd('\0');
                            bool boundsMatch = currentDisplay?.Bounds != null && outputBounds.Equals(currentDisplay.Bounds);

                            if (nameMatch || boundsMatch)
                            {
                                targetOutput1 = output1;
                                targetAdapter = adapter;
                                foundTarget = true;
                                break;
                            }
                            output1.Dispose();
                        }
                    }

                    if (foundTarget) break;
                }

                // Fallback to specific display index if not found
                if (!foundTarget)
                {
                    int targetIndex = currentDisplay?.Index ?? 0;
                    int currentIndex = 0;

                    for (uint adapterIndex = 0;
                        factory.EnumAdapters1(adapterIndex, out var adapter).Success;
                        adapterIndex++)
                    {
                        for (uint outputIndex = 0;
                            adapter.EnumOutputs(outputIndex, out var output).Success;
                            outputIndex++)
                        {
                            if (currentIndex == targetIndex)
                            {
                                LogManager.Log(LogLevel.Warning, $"Could not match display by name or bounds. Found a fallback index, {targetIndex}.");
                                targetOutput1 = output.QueryInterface<IDXGIOutput1>();
                                targetAdapter = adapter;
                                foundTarget = true;
                                break;
                            }
                            currentIndex++;
                            output.Dispose();
                        }

                        if (foundTarget)
                            break;
                        adapter.Dispose();
                    }
                }

                if (targetAdapter == null || targetOutput1 == null)
                {
                    LogManager.Log(LogLevel.Error, "No suitable display output found for DirectX capture.", true, 6000);
                    throw new Exception("No suitable display output found");
                }

                FeatureLevel[] featureLevels = {
                    FeatureLevel.Level_12_2, // 50 series support
                    FeatureLevel.Level_12_1,
                    FeatureLevel.Level_12_0,
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0,
                    FeatureLevel.Level_9_3,
                    FeatureLevel.Level_9_2,
                    FeatureLevel.Level_9_1
                };

                // Create D3D11 device
                var result = D3D11.D3D11CreateDevice(
                    targetAdapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.None,
                    featureLevels,
                    out _dxDevice);

                if (result.Failure || _dxDevice == null)
                {
                    result = D3D11.D3D11CreateDevice(
                      targetAdapter,
                      DriverType.Unknown,
                      DeviceCreationFlags.None,
                      null,
                      out _dxDevice);

                    if (result.Failure || _dxDevice == null)
                    {
                        LogManager.Log(LogLevel.Error, $"Failed to create D3D11 device: {result}", true, 6000);
                        throw new Exception($"Failed to create D3D11 device: {result}");
                    }
                }

                // Create desktop duplication
                _deskDuplication = targetOutput1.DuplicateOutput(_dxDevice);
                _consecutiveFailures = 0; //reset on success

                LogManager.Log(LogLevel.Info, "DirectX Desktop Duplication initialized successfully.");
            }
            catch (SharpGenException ex) when (ex.ResultCode == Vortice.DXGI.ResultCode.Unsupported || ex.HResult == unchecked((int)0x887A0004))
            {
                LogManager.Log(LogLevel.Error, $"DirectX Desktop Duplication not supported on this system: {ex.Message}", true, 6000);
                _directXFailedPermanently = true;
                DisposeDxgiResources();

                Dictionary.dropdownState["Screen Capture Method"] = "GDI+";
                _currentCaptureMethod = "GDI+";

                LogManager.Log(LogLevel.Error, "DirectX Desktop Duplication not supported on this system. Switched to GDI+ capture.", true, 6000);
            }
            catch (Exception ex)
            {
                LogManager.Log(LogLevel.Error, $"Failed to initialize DirectX Desktop Duplication: {ex.Message}", true, 6000);
                DisposeDxgiResources();
                throw;
            }
        }
        /// <summary>
        /// DirectX desktop duplication. Waits up to <see cref="NewFrameWaitMs"/> for the screen to change; when it has,
        /// copies the detection box to a staging texture and converts it straight from the mapped texture into the
        /// model input. Returns NoNewFrame when the picture hasn't changed, so the model isn't run on the same image twice.
        /// </summary>
        private unsafe CaptureStatus DirectXInto(Rectangle detectionBox, Span<float> input, int imageSize, bool keepFrame)
        {
            int w = detectionBox.Width;
            int h = detectionBox.Height;
            bool frameAcquired = false;
            IDXGIResource? desktopResource = null;

            try
            {
                lock (_displayLock)
                {
                    if (_displayChangesPending)
                    {
                        InitializeDxgiDuplication();
                        _displayChangesPending = false;
                    }
                }

                // Check if we need to reinitialize
                if (_dxDevice == null || _dxDevice.ImmediateContext == null || _deskDuplication == null)
                {
                    InitializeDxgiDuplication();
                    if (_dxDevice == null || _dxDevice.ImmediateContext == null || _deskDuplication == null)
                    {
                        lock (_displayLock) { _displayChangesPending = true; }
                        return CaptureStatus.Failed;
                    }
                }

                // Staging texture always matches the requested size
                if (_stagingTex == null ||
                    _stagingTex.Description.Width != w ||
                    _stagingTex.Description.Height != h)
                {
                    _stagingTex?.Dispose();
                    _stagingTex = _dxDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)w,
                        Height = (uint)h,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new(1, 0),
                        Usage = ResourceUsage.Staging,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        BindFlags = BindFlags.None
                    });
                }

                uint timeout = _consecutiveFailures > 0 ? 5u : NewFrameWaitMs;
                var result = _deskDuplication!.AcquireNextFrame(timeout, out var frameInfo, out desktopResource);

                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    // The screen hasn't changed since the last frame
                    _consecutiveFailures = 0;
                    return CaptureStatus.NoNewFrame;
                }
                else if (result == Vortice.DXGI.ResultCode.DeviceRemoved || result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    // Device lost - need to reinitialize
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                        lock (_displayLock) { _displayChangesPending = true; }
                    return CaptureStatus.Failed;
                }
                else if (result != Result.Ok)
                {
                    _consecutiveFailures++;
                    return CaptureStatus.Failed;
                }

                frameAcquired = true;
                _consecutiveFailures = 0;

                // Only the mouse pointer moved: the picture itself is unchanged
                if (frameInfo.LastPresentTime == 0)
                    return CaptureStatus.NoNewFrame;

                long copyStart = Stopwatch.GetTimestamp();
                using (var screenTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    // The duplicated output starts at (0,0), not at its screen position
                    int relativeDetectionLeft = detectionBox.Left - DisplayManager.ScreenLeft;
                    int relativeDetectionTop = detectionBox.Top - DisplayManager.ScreenTop;
                    int relativeDetectionRight = relativeDetectionLeft + detectionBox.Width;
                    int relativeDetectionBottom = relativeDetectionTop + detectionBox.Height;

                    // The visible portion in display-relative coordinates
                    int srcLeft = Math.Max(relativeDetectionLeft, 0);
                    int srcTop = Math.Max(relativeDetectionTop, 0);
                    int srcRight = Math.Min(relativeDetectionRight, DisplayManager.ScreenWidth);
                    int srcBottom = Math.Min(relativeDetectionBottom, DisplayManager.ScreenHeight);

                    if (srcRight <= srcLeft || srcBottom <= srcTop)
                    {
                        LogManager.Log(LogLevel.Warning, "No visible region to copy from DirectX capture.", true, 3000);
                        return CaptureStatus.Failed;
                    }

                    var box = new Box(srcLeft, srcTop, 0, srcRight, srcBottom, 1);
                    _dxDevice.ImmediateContext.CopySubresourceRegion(
                        _stagingTex, 0,
                        (uint)(srcLeft - relativeDetectionLeft),
                        (uint)(srcTop - relativeDetectionTop),
                        0,
                        screenTexture, 0, box);
                }

                var map = _dxDevice.ImmediateContext.Map(_stagingTex, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    byte* src = (byte*)map.DataPointer;
                    int srcStride = (int)map.RowPitch;
                    bool mask = Dictionary.toggleState["Third Person Support"];

                    MathUtil.BgraToPlanarRgb(src, srcStride, imageSize, input, mask);

                    // A picture is only kept when training images are being saved
                    if (keepFrame)
                        CopyToBitmap(src, srcStride, w, h, mask);
                }
                finally
                {
                    _dxDevice.ImmediateContext.Unmap(_stagingTex, 0);
                }

                LastCopyTicks = Stopwatch.GetTimestamp() - copyStart;
                return CaptureStatus.NewFrame;
            }
            catch (Exception e)
            {
                LogManager.Log(LogLevel.Error, $"DirectX capture error: {e.Message}");

                if (++_consecutiveFailures >= MAX_CONSECUTIVE_FAILURES)
                    lock (_displayLock) { _displayChangesPending = true; }

                return CaptureStatus.Failed;
            }
            finally
            {
                desktopResource?.Dispose();
                try
                {
                    if (frameAcquired && _deskDuplication != null)
                        _deskDuplication.ReleaseFrame();
                }
                catch { }
            }
        }

        /// <summary>Copies the mapped pixels into <see cref="directXBitmap"/>, for saving training images.</summary>
        private unsafe void CopyToBitmap(byte* src, int srcStride, int w, int h, bool maskThirdPerson)
        {
            if (directXBitmap == null || directXBitmap.Width != w || directXBitmap.Height != h)
            {
                directXBitmap?.Dispose();
                directXBitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            }

            var data = directXBitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* dst = (byte*)data.Scan0;
                int rowBytes = Math.Min(srcStride, data.Stride);
                for (int y = 0; y < h; y++)
                    Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * data.Stride, data.Stride, rowBytes);

                if (maskThirdPerson)
                {
                    // Same mask the model sees: the bottom-left quarter (where your own character is) is black
                    for (int y = h - h / 2; y < h; y++)
                    {
                        uint* row = (uint*)(dst + (long)y * data.Stride);
                        for (int x = 0; x < w / 2; x++)
                            row[x] = 0xFF000000;
                    }
                }
            }
            finally
            {
                directXBitmap.UnlockBits(data);
            }
        }
        #endregion

        #region GDI
        private unsafe CaptureStatus GdiInto(Rectangle detectionBox, Span<float> input, int imageSize)
        {
            if (_dxDevice != null || _deskDuplication != null)
            {
                DisposeDxgiResources();
            }

            if (screenCaptureBitmap == null || screenCaptureBitmap.Width != detectionBox.Width || screenCaptureBitmap.Height != detectionBox.Height)
            {
                screenCaptureBitmap?.Dispose();
                screenCaptureBitmap = new Bitmap(detectionBox.Width, detectionBox.Height, PixelFormat.Format32bppArgb);
            }

            try
            {
                long copyStart = Stopwatch.GetTimestamp();
                using (var g = Graphics.FromImage(screenCaptureBitmap))
                {
                    g.CopyFromScreen(
                        detectionBox.Left,
                        detectionBox.Top,
                        0, 0,
                        detectionBox.Size,
                        CopyPixelOperation.SourceCopy
                    );

                    if (Dictionary.toggleState["Third Person Support"])
                    {
                        int width = screenCaptureBitmap.Width / 2;
                        int height = screenCaptureBitmap.Height / 2;
                        int startY = screenCaptureBitmap.Height - height;

                        using var brush = new SolidBrush(System.Drawing.Color.Black);
                        g.FillRectangle(brush, 0, startY, width, height);
                    }
                }

                // Convert straight from the captured bitmap; it is reused, never cloned
                var rect = new Rectangle(0, 0, screenCaptureBitmap.Width, screenCaptureBitmap.Height);
                var data = screenCaptureBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    MathUtil.BgraToPlanarRgb((byte*)data.Scan0, data.Stride, imageSize, input, maskThirdPerson: false);
                }
                finally
                {
                    screenCaptureBitmap.UnlockBits(data);
                }

                // GDI can't tell whether the screen changed, so every capture counts as new
                LastCopyTicks = Stopwatch.GetTimestamp() - copyStart;
                return CaptureStatus.NewFrame;
            }
            catch (Exception ex)
            {
                LogManager.Log(LogLevel.Error, $"GDI+ screen capture failed: {ex.Message}");
                return CaptureStatus.Failed;
            }
        }
        #endregion

        /// <summary>
        /// Captures the detection box straight into the model input (planar RGB, 0-1).
        /// NewFrame: the input holds a new picture. NoNewFrame: the screen hasn't changed, the input is untouched.
        /// Failed: nothing was captured (device lost, display changing...).
        /// </summary>
        /// <param name="keepFrame">Also keep the picture so <see cref="CopyLastFrame"/> can return it (for training images).</param>
        public CaptureStatus CaptureInto(Rectangle detectionBox, Span<float> input, int imageSize, bool keepFrame)
        {
            if (detectionBox.Width != imageSize || detectionBox.Height != imageSize || input.Length != 3 * imageSize * imageSize)
                throw new ArgumentException("The detection box and input buffer must match the model's image size.");

            string selectedMethod = Dictionary.dropdownState["Screen Capture Method"];

            // If DirectX failed permanently, force GDI+
            if (_directXFailedPermanently && selectedMethod == "DirectX")
            {
                Dictionary.dropdownState["Screen Capture Method"] = "GDI+";
                selectedMethod = "GDI+";
                _currentCaptureMethod = "GDI+";
            }

            // Handle method switch
            if (selectedMethod != _currentCaptureMethod)
            {
                screenCaptureBitmap?.Dispose();
                screenCaptureBitmap = null;

                directXBitmap?.Dispose();
                directXBitmap = null;

                _currentCaptureMethod = selectedMethod;
                _notificationShown = false;

                if (selectedMethod == "GDI+")
                    DisposeDxgiResources();
                else
                    InitializeDxgiDuplication();
            }

            return selectedMethod == "DirectX" && !_directXFailedPermanently
                ? DirectXInto(detectionBox, input, imageSize, keepFrame)
                : GdiInto(detectionBox, input, imageSize);
        }

        /// <summary>A copy of the last captured picture (DirectX keeps one only while keepFrame is set), or null.</summary>
        public Bitmap? CopyLastFrame()
        {
            var source = _currentCaptureMethod == "GDI+" ? screenCaptureBitmap : directXBitmap;
            try
            {
                return source == null ? null : (Bitmap)source.Clone();
            }
            catch
            {
                return null;
            }
        }

        #region dispose
        public void DisposeDxgiResources()
        {
            lock (_displayLock)
            {
                try
                {

                    // Try to release any pending frame
                    if (_deskDuplication != null)
                    {
                        try
                        {
                            _deskDuplication.ReleaseFrame();
                        }
                        catch { }
                    }

                    _deskDuplication?.Dispose();
                    _stagingTex?.Dispose();
                    _dxDevice?.Dispose();
                    directXBitmap?.Dispose();

                    _deskDuplication = null;
                    _stagingTex = null;
                    _dxDevice = null;
                    directXBitmap = null;

                    // Small delay to ensure resources are fully released
                    //System.Threading.Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    LogManager.Log(LogLevel.Error, $"Error disposing DXGI resources: {ex.Message}");
                }
            }
        }
        public void Dispose()
        {
            DisplayManager.DisplayChanged -= OnDisplayChanged;
            DisposeDxgiResources();
            screenCaptureBitmap?.Dispose();
        }
        #endregion
    }
}
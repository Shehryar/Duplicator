using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.Direct3D11;
using SharpDX.DirectWrite;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using SharpDX.MediaFoundation;
using SharpDX.Multimedia;

namespace Duplicator
{
    public class Duplicator : INotifyPropertyChanged, IDisposable
    {
        // use that guid in TraceSpy's ETW Trace Provider (https://github.com/smourier/TraceSpy) 
        // or use is with MFTrace (see config.xml and t.bat in the project)
        private static EventProvider _provider = new EventProvider(new Guid("964D4572-ADB9-4F3A-8170-FCBECEC27465"));

        // recording common
        private bool _recordingEnabled;
        private Lazy<SinkWriter> _sinkWriter;
        private Stopwatch _watch = new Stopwatch();
        private Lazy<MediaSink> _sink;
        private object _lock = new object();

        // video recording
        private Lazy<DXGIDeviceManager> _devManager;
        private Lazy<Texture2D> _frameCopy;
        private int _videoOutputIndex;
        private long _lastVideoWatch;

        // sound recording
        private int _audioOutputIndex;
        private long _lastAudioWatch;
        private LoopbackAudioCapture _audioCapture;
        private int _audioSamplesCount;
        private int _audioGapsCount;
        private bool _discontinuity;

        // duplicating
        private Lazy<SwapChain1> _swapChain;
        private Lazy<SharpDX.Direct2D1.Device> _2D1Device;
        private Lazy<SharpDX.Direct2D1.DeviceContext> _backBufferDc;
        private Lazy<SharpDX.Direct3D11.Device> _device;
        private Lazy<OutputDuplication> _outputDuplication;
        private Lazy<Output1> _output;
        private Lazy<TextFormat> _diagsTextFormat;
        private Lazy<Brush> _diagsBrush;
        private Lazy<Size2> _desktopSize;
        private int _resized;
        private bool _duplicationEnabled;
        private Bitmap _pointerBitmap;
        private System.Threading.Timer _frameRateTimer;
        private Thread _duplicationThread;
        private int _accumulatedFramesCount;
        private int _videoSamplesCount;
        private int _currentFrameNumber;
        private int _currentFrameRate;
        private RawPoint _pointerPosition;
        private RawPoint _pointerHotspot;
        private bool _pointerVisible;
        private int _pointerType;
        private Size2 _size;
        private SharpDX.DXGI.Resource _frame;
#if DEBUG
        private Lazy<DeviceDebug> _deviceDebug;
#endif

        public event PropertyChangedEventHandler PropertyChanged;

        public Duplicator(DuplicatorOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            Options = options;
            Options.PropertyChanged += OnOptionsChanged;

            _devManager = new Lazy<DXGIDeviceManager>(CreateDeviceManager);
            _sinkWriter = new Lazy<SinkWriter>(CreateSinkWriter);
            _device = new Lazy<SharpDX.Direct3D11.Device>(CreateDevice);
#if DEBUG
            _deviceDebug = new Lazy<DeviceDebug>(CreateDeviceDebug);
#endif
            _desktopSize = new Lazy<Size2>(CreateDesktopSize);
            _output = new Lazy<Output1>(CreateOutput);
            _outputDuplication = new Lazy<OutputDuplication>(CreateOutputDuplication);
            _frameCopy = new Lazy<Texture2D>(CreateFrameCopy);
            _diagsTextFormat = new Lazy<TextFormat>(CreateDiagsTextFormat);
            _diagsBrush = new Lazy<Brush>(CreateDiagsBrush);
            _frameRateTimer = new System.Threading.Timer((state) =>
            {
                _currentFrameRate = _currentFrameNumber;
                _currentFrameNumber = 0;
            }, null, 0, 1000);

            _audioCapture = new LoopbackAudioCapture();
            _audioCapture.RaiseNativeDataEvents = true;
            _audioCapture.RaiseDataEvents = false;
            _audioCapture.NativeDataReady += AudioCaptureDataReady;

            _swapChain = new Lazy<SwapChain1>(CreateSwapChain);
            _2D1Device = new Lazy<SharpDX.Direct2D1.Device>(Create2D1Device);
            _backBufferDc = new Lazy<SharpDX.Direct2D1.DeviceContext>(CreateBackBufferDc);
            _sink = new Lazy<MediaSink>(CreateMediaSink);
        }

        public DuplicatorOptions Options { get; }
        public Size2 DesktopSize => _desktopSize.Value;
        public Size2 RenderSize { get; private set; }
        public bool IsUsingDirect3D11AwareEncoder { get; private set; }
        public bool IsUsingHardwareBasedEncoder { get; private set; }
        public bool IsUsingBuiltinEncoder { get; private set; }
        public IntPtr Hwnd { get; set; }
        public string RecordFilePath { get; set; }

        public Size2 Size
        {
            get => _size;
            set
            {
                if (_size == value)
                    return;

                _size = value;
                Interlocked.Exchange(ref _resized, 1);
                if (Options.PreserveRatio && value.Height > 0 && value.Width > 0 && DesktopSize.Width > 0 && DesktopSize.Height > 0)
                {
                    if (value.Width * DesktopSize.Height > value.Height * DesktopSize.Width)
                    {
                        RenderSize = new Size2((value.Height * DesktopSize.Width) / DesktopSize.Height, value.Height);
                    }
                    else
                    {
                        RenderSize = new Size2(value.Width, (value.Width * DesktopSize.Height) / DesktopSize.Width);
                    }
                }
                else
                {
                    RenderSize = value;
                }

                OnPropertyChanged(nameof(Size));
                OnPropertyChanged(nameof(RenderSize));
            }
        }

        public bool IsRecording { get => _sinkWriter.IsValueCreated; set => _recordingEnabled = value; }

        public bool IsDuplicating
        {
            get => _outputDuplication.IsValueCreated;
            set
            {
                if (IsDuplicating == value)
                    return;

                _duplicationEnabled = value;
                if (value)
                {
                    Size = Size;
                    _duplicationThread = new Thread(DuplicatorThreadFunc);
                    _duplicationThread.Name = nameof(Duplicator) + DateTime.Now.TimeOfDay;
                    _duplicationThread.IsBackground = true;
                    _duplicationThread.Start();
                }
                else
                {
                    IsRecording = false;
                    var t = _duplicationThread;
                    _duplicationThread = null;
                    if (t != null)
                    {
                        var result = t.Join((int)Math.Min(Options.FrameAcquisitionTimeout * 2L, int.MaxValue));
                    }
                }
            }
        }

        private void WriteVideoSample()
        {
            var frame = _frame;
            if (frame == null)
                return;

            using (var res = frame.QueryInterface<SharpDX.Direct3D11.Resource>())
            {
                _device.Value.ImmediateContext.CopyResource(res, _frameCopy.Value);
            }

            using (var sample = MediaFactory.CreateSample())
            {
                MediaFactory.CreateDXGISurfaceBuffer(typeof(Texture2D).GUID, _frameCopy.Value, 0, new RawBool(true), out MediaBuffer buffer);
                using (buffer)
                using (var buffer2 = buffer.QueryInterface<Buffer2D>())
                {
                    var elapsedNs = (10000000 * _watch.ElapsedTicks) / Stopwatch.Frequency;
                    sample.SampleTime = _lastVideoWatch;
                    sample.SampleDuration = elapsedNs - _lastVideoWatch;
                    _lastVideoWatch = elapsedNs;

                    buffer.CurrentLength = buffer2.ContiguousLength;
                    sample.AddBuffer(buffer);
                    //Trace("[" + _videoSamplesCount + "] time: " + sample.SampleTime + " duration: " + sample.SampleDuration);
                    _sinkWriter.Value.WriteSample(_videoOutputIndex, sample);
                    _videoSamplesCount++;
                }
            }
        }

        private void AudioCaptureDataReady(object sender, LoopbackAudioCaptureNativeDataEventArgs e)
        {
            WriteAudioSample(e.Data, e.Size);
            e.Handled = true;
        }

        private void WriteAudioSample(IntPtr data, int size)
        {
            if (size == 0)
            {
                var elapsedNs = (10000000 * _watch.ElapsedTicks) / Stopwatch.Frequency;
                try
                {
                    Trace("SendStreamTick : " + elapsedNs);
                    _sinkWriter.Value.SendStreamTick(_audioOutputIndex, elapsedNs);
                }
                catch (Exception e)
                {
                    // we may be closing down
                    Trace("SendStreamTick failed: " + e.Message);
                }
                _audioGapsCount++;
                _discontinuity = true;
                return;
            }

            using (var buffer = MediaFactory.CreateMemoryBuffer(size))
            {
                var ptr = buffer.Lock(out int max, out int current);
                CopyMemory(ptr, data, size);
                buffer.CurrentLength = size;
                buffer.Unlock();

                using (var sample = MediaFactory.CreateSample())
                {
                    if (_discontinuity)
                    {
                        _discontinuity = false;
                        Trace("Set Discontinuity");
                        sample.Set(SampleAttributeKeys.Discontinuity, true);
                    }

                    var elapsedNs = (10000000 * _watch.ElapsedTicks) / Stopwatch.Frequency;
                    sample.SampleTime = _lastAudioWatch;
                    _lastAudioWatch = elapsedNs;
                    sample.AddBuffer(buffer);
                    //Trace("sample time ns: " + sample.SampleTime + " bytes: " + size);
                    _sinkWriter.Value.WriteSample(_audioOutputIndex, sample);
                    _audioSamplesCount++;
                }
            }
        }

        private bool TryGetFrame()
        {
            var od = _outputDuplication.Value; // can be null if adapter is not connected
            if (od == null)
                return false;

            var frame = _frame;
            if (frame != null)
            {
                _frame = null;
                frame.Dispose();
                od.ReleaseFrame();
            }

            SharpDX.DXGI.Resource resource = null;
            try
            {
                od.AcquireNextFrame(Options.FrameAcquisitionTimeout, out OutputDuplicateFrameInformation frameInfo, out resource);
                _currentFrameNumber++;
                _accumulatedFramesCount = frameInfo.AccumulatedFrames;

                if (frameInfo.LastMouseUpdateTime != 0)
                {
                    _pointerVisible = frameInfo.PointerPosition.Visible;
                    _pointerPosition = frameInfo.PointerPosition.Position;
                    if (frameInfo.PointerShapeBufferSize != 0)
                    {
                        ComputePointerBitmap(ref frameInfo);
                    }
                }
            }
            catch (SharpDXException ex)
            {
                // DXGI_ERROR_WAIT_TIMEOUT
                if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code)
                    return true;

                // DXGI_ERROR_ACCESS_LOST
                if (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Code)
                {
                    DisposeDuplication();
                    return false;
                }

                return false;
            }

            _frame = resource;
            return true;
        }

        // we do everything in here to avoid threading issues
        private void DuplicatorThreadFunc()
        {
            // force od creation here so we can notify we're duplicating
            var od = _outputDuplication.Value;
            OnPropertyChanged(nameof(IsDuplicating));
            do
            {

                if (Interlocked.CompareExchange(ref _resized, 0, 1) == 1)
                {
                    if (_swapChain.IsValueCreated)
                    {
                        if (_backBufferDc.IsValueCreated)
                        {
                            // release outstanding references to swapchain's back buffers
                            _backBufferDc = Reset(_backBufferDc, CreateBackBufferDc);
                        }

                        //Trace("resize: " + RenderSize.Width + "x" + RenderSize.Height);
                        _swapChain.Value.ResizeBuffers(
                            _swapChain.Value.Description1.BufferCount,
                            Size.Width,
                            Size.Height,
                            _swapChain.Value.Description1.Format,
                            _swapChain.Value.Description1.Flags);
                    }
                }

                if (_duplicationEnabled && od != null)
                {
                    if (TryGetFrame())
                    {
                        if (_duplicationEnabled)
                        {
                            RenderFrame();
                        }

                        if (_recordingEnabled)
                        {
                            WriteVideoSample();
                        }
                        else
                        {
                            DisposeRecording();
                        }
                    }
                }
                else
                {
                    DisposeRecording();
                    DisposeDuplication();
                    return;
                }
            }
            while (true);
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            Trace("Name " + name);
        }

        // How to render by using a Direct2D device context
        // https://msdn.microsoft.com/en-us/library/windows/desktop/hh780339.aspx
        private void RenderFrame()
        {
            var frame = _frame;
            if (frame == null)
                return;

            //Trace("SW:" + _swapChain.Value.Description1.Width + "x" + _swapChain.Value.Description1.Height + " rs:" + RenderSize.Width + "x" + RenderSize.Height);
            _backBufferDc.Value.BeginDraw();
            _backBufferDc.Value.Clear(new RawColor4(0, 0, 0, 1));
            using (var frameSurface = frame.QueryInterface<Surface>())
            {
                // build a DC
                using (var frameDc = new SharpDX.Direct2D1.DeviceContext(_2D1Device.Value, DeviceContextOptions.EnableMultithreadedOptimizations))
                {
                    // bind a bitmap corresponding to the frame to this DC
                    using (var frameBitmap = new Bitmap1(frameDc, frameSurface))
                    {
                        var renderX = (Size.Width - RenderSize.Width) / 2;
                        var renderY = (Size.Height - RenderSize.Height) / 2;

                        // draw this bitmap to the swap chain's backbuffer-bound DC
                        _backBufferDc.Value.DrawBitmap(frameBitmap, new RawRectangleF(renderX, renderY, renderX + RenderSize.Width, renderY + RenderSize.Height), 1, BitmapInterpolationMode.Linear);

                        // add some useful info if required
                        var diags = new List<string>();
                        if (Options.ShowInputFps)
                        {
                            diags.Add(_currentFrameRate + " fps");
                        }

                        if (Options.ShowAccumulatedFrames)
                        {
                            diags.Add(_accumulatedFramesCount + " af");
                        }

                        // draw the pointer if visible and if we have it
                        if (Options.ShowCursor && _pointerVisible)
                        {
                            var pb = _pointerBitmap;
                            if (pb != null)
                            {
                                RawRectangleF rect;

                                // note: the doc says not to use the hotspot, but it seems we still need to need it...
                                if (Options.IsCursorProportional)
                                {
                                    int captureX = ((_pointerPosition.X - _pointerHotspot.X) * RenderSize.Width) / DesktopSize.Width + renderX;
                                    int captureY = ((_pointerPosition.Y - _pointerHotspot.Y) * RenderSize.Height) / DesktopSize.Height + renderY;
                                    rect = new RawRectangleF(
                                        captureX,
                                        captureY,
                                        captureX + (pb.Size.Width * RenderSize.Width) / DesktopSize.Width,
                                        captureY + (pb.Size.Height * RenderSize.Height) / DesktopSize.Height);
                                }
                                else
                                {
                                    int captureX = (_pointerPosition.X * RenderSize.Width) / DesktopSize.Width - _pointerHotspot.X + renderX;
                                    int captureY = (_pointerPosition.Y * RenderSize.Height) / DesktopSize.Height - _pointerHotspot.Y + renderY;
                                    rect = new RawRectangleF(
                                        captureX,
                                        captureY,
                                        captureX + pb.Size.Width,
                                        captureY + pb.Size.Height);
                                }

                                _backBufferDc.Value.DrawBitmap(pb, rect, 1, BitmapInterpolationMode.NearestNeighbor);
                            }
                        }

                        // add diags, if any
                        if (diags.Count > 0)
                        {
                            _backBufferDc.Value.DrawText(string.Join(Environment.NewLine, diags), _diagsTextFormat.Value, new RawRectangleF(0, 0, Size.Width, Size.Height), _diagsBrush.Value);
                        }
                    }
                }
            }

            _backBufferDc.Value.EndDraw();

            // let's flip it
            _swapChain.Value.Present(1, 0);
        }

        public void Dispose()
        {
            _frameRateTimer = Dispose(_frameRateTimer);
            IsDuplicating = false;
            DisposeRecording();
            DisposeDuplication();
#if DEBUG
            DXGIReportLiveObjects();
#endif
        }

        private void DisposeRecording()
        {
            // dispose could be called by other threads, so we need to protect this
            lock (_lock)
            {
                if (!_sinkWriter.IsValueCreated)
                    return;

                _audioCapture.Stop();

                if (_videoSamplesCount > 0)
                {
                    Trace("SinkWriter Finalize video samples: " + _videoSamplesCount + " audio samples: " + _audioSamplesCount + " audio gaps: " + _audioGapsCount);
                    _sinkWriter.Value.Finalize();
                    _videoSamplesCount = 0;
                }

                _audioSamplesCount = 0;
                _sink = Reset(_sink, CreateMediaSink);
                _sinkWriter = Reset(_sinkWriter, CreateSinkWriter);
                _devManager = Reset(_devManager, CreateDeviceManager);
                _watch.Stop();
                _lastVideoWatch = 0;
                _lastAudioWatch = 0;
                _videoOutputIndex = 0;
                _audioOutputIndex = 0;
                OnPropertyChanged(nameof(IsRecording));
            }
        }

        private void DisposeDuplication()
        {
            // dispose could be called by other threads, so we need to protect this
            lock (_lock)
            {
                _currentFrameRate = 0;
                _currentFrameNumber = 0;
                _desktopSize = new Lazy<Size2>(CreateDesktopSize);
                _pointerBitmap = Dispose(_pointerBitmap);
                _diagsTextFormat = Reset(_diagsTextFormat, CreateDiagsTextFormat);
                _diagsBrush = Reset(_diagsBrush, CreateDiagsBrush);
                _frame = Dispose(_frame);
                _output = Reset(_output, CreateOutput);
                _outputDuplication = Reset(_outputDuplication, CreateOutputDuplication);
#if DEBUG
                _deviceDebug = Reset(_deviceDebug, CreateDeviceDebug);
#endif

                _2D1Device = Reset(_2D1Device, Create2D1Device);
                _backBufferDc = Reset(_backBufferDc, CreateBackBufferDc);
                _swapChain = Reset(_swapChain, CreateSwapChain);

                _device = Reset(_device, CreateDevice);
                OnPropertyChanged(nameof(IsDuplicating));
            }
        }

        // we need to handle some important properties change
        private void OnOptionsChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DuplicatorOptions.Adapter):
                case nameof(DuplicatorOptions.Output):
                    var duplicating = IsDuplicating;
                    if (!duplicating)
                    {
                        DisposeRecording();
                        DisposeDuplication();
                        break;
                    }

                    IsDuplicating = false;
                    IsDuplicating = duplicating;
                    break;

                case nameof(DuplicatorOptions.PreserveRatio):
                    Size = Size;
                    break;
            }
        }

#if DEBUG
        private DeviceDebug CreateDeviceDebug() => _device.Value.QueryInterface<DeviceDebug>();
#endif

        private SharpDX.Direct2D1.Device Create2D1Device()
        {
            using (var fac = new SharpDX.Direct2D1.Factory1())
            {
                using (var dxDev = _device.Value.QueryInterface<SharpDX.DXGI.Device>())
                {
                    return new SharpDX.Direct2D1.Device(fac, dxDev);
                }
            }
        }

        private SharpDX.Direct2D1.DeviceContext CreateBackBufferDc()
        {
            var dc = new SharpDX.Direct2D1.DeviceContext(_2D1Device.Value, DeviceContextOptions.EnableMultithreadedOptimizations);
            using (var backBuffer = _swapChain.Value.GetBackBuffer<Surface>(0))
            {
                using (var bmp = new Bitmap1(dc, backBuffer))
                {
                    dc.Target = bmp;
                    return dc;
                }
            }
        }

        private SharpDX.Direct3D11.Device CreateDevice()
        {
            using (var fac = new SharpDX.DXGI.Factory1())
            {
                using (var adapter = Options.GetAdapter())
                {
                    if (adapter == null)
                        return null;

                    var flags = DeviceCreationFlags.BgraSupport; // for D2D cooperation
                    flags |= DeviceCreationFlags.VideoSupport;
#if DEBUG
                    flags |= DeviceCreationFlags.Debug;
#endif
                    return new SharpDX.Direct3D11.Device(adapter, flags);
                }
            }
        }

        private SwapChain1 CreateSwapChain()
        {
            // https://msdn.microsoft.com/en-us/library/windows/desktop/hh780339.aspx
            using (var fac = new SharpDX.Direct2D1.Factory1())
            {
                using (var dxFac = new SharpDX.DXGI.Factory2(
#if DEBUG
                    true
#else
                    false
#endif
                    ))
                {
                    using (var dxDev = _device.Value.QueryInterface<SharpDX.DXGI.Device1>())
                    {
                        var desc = new SwapChainDescription1();
                        desc.SampleDescription = new SampleDescription(1, 0);
                        desc.SwapEffect = SwapEffect.FlipSequential;
                        desc.Scaling = Scaling.None;
                        desc.Usage = Usage.RenderTargetOutput;
                        desc.Format = Format.B8G8R8A8_UNorm;
                        desc.BufferCount = 2;
                        var sw = new SwapChain1(dxFac, dxDev, Hwnd, ref desc);
                        dxDev.MaximumFrameLatency = 1;
                        return sw;
                    }
                }
            }
        }

        private Size2 CreateDesktopSize() => _output.Value != null ? new Size2(
                _output.Value.Description.DesktopBounds.Right - _output.Value.Description.DesktopBounds.Left,
                _output.Value.Description.DesktopBounds.Bottom - _output.Value.Description.DesktopBounds.Top) : new Size2();

        private Output1 CreateOutput() => Options.GetOutput();

        private Texture2D CreateFrameCopy()
        {
            // this is meant for GPU to GPU operation for maximum performance, so CPU has no access to this
            // we don't use KeyedMutex, it does not seem to be necessary here.
            var desc = new Texture2DDescription()
            {
                CpuAccessFlags = CpuAccessFlags.None,
                BindFlags = BindFlags.None,
                Format = Format.B8G8R8A8_UNorm,
                Width = DesktopSize.Width,
                Height = DesktopSize.Height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Default
            };

            return new Texture2D(_device.Value, desc);
        }

        private Brush CreateDiagsBrush() => new SolidColorBrush(_backBufferDc.Value, new RawColor4(1, 0, 0, 1));
        private TextFormat CreateDiagsTextFormat()
        {
            using (var fac = new SharpDX.DirectWrite.Factory1())
            {
                var diagsTextFormat = new TextFormat(fac, "Lucida Console", 16);
                diagsTextFormat.WordWrapping = WordWrapping.Character;
                return diagsTextFormat;
            }
        }

        private DeviceContextRenderTarget CreateRenderTarget()
        {
            using (var fac = new SharpDX.Direct2D1.Factory1())
            {
                var props = new RenderTargetProperties();
                props.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore);
                return new DeviceContextRenderTarget(fac, props);
            }
        }

        private OutputDuplication CreateOutputDuplication() => _output.Value?.DuplicateOutput(_device.Value);

        private DXGIDeviceManager CreateDeviceManager()
        {
            var devManager = new DXGIDeviceManager();
            MediaFactory.CreateDXGIDeviceManager(out int token, devManager);
            devManager.ResetDevice(_device.Value);
            return devManager;
        }

        private SinkWriter CreateSinkWriter()
        {
            MediaManager.Startup(); // this is ok to be called more than once

            if (string.IsNullOrEmpty(RecordFilePath))
            {
                RecordFilePath = Options.GetNewFilePath();
            }

            RecordFilePath += ".mp4"; // we only support MP4...

            string dir = Path.GetDirectoryName(RecordFilePath);
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            int width = DesktopSize.Width;
            int height = DesktopSize.Height;

            SinkWriter writer;
            using (var ma = new MediaAttributes())
            {
                // note this doesn't mean you *will* have a hardware transform. Intel Media SDK sometimes is not happy with configuration for HDCP issues.
                ma.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, Options.EnableHardwareTransforms ? 1 : 0);
                ma.Set(SinkWriterAttributeKeys.D3DManager, _devManager.Value);
                ma.Set(SinkWriterAttributeKeys.DisableThrottling, Options.DisableThrottling ? 1 : 0);
                ma.Set(SinkWriterAttributeKeys.LowLatency, Options.LowLatency);
                Trace("CreateSinkWriterFromURL path: " + RecordFilePath);
                writer = MediaFactory.CreateSinkWriterFromURL(RecordFilePath, IntPtr.Zero, ma);
            }

            using (var videoStream = new MediaType())
            {
                // avg bitrate is mandatory for builtin encoder, not for some others like Intel Media SDK
                // in fact, what will that be used for? anyway, here is a standard formula from here
                // https://stackoverflow.com/questions/5024114/suggested-compression-ratio-with-h-264
                //
                // [image width] x [image height] x [framerate] x [motion rank] x 0.07 = [desired bitrate]
                //

                int motionRank = 1;
                int bitrate = (int)(width * height * Options.RecordingFrameRate * motionRank * 0.07f);
                if (bitrate <= 0)
                    throw new InvalidOperationException();

                videoStream.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
                videoStream.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                videoStream.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.FromFourCC(new FourCC("H264")));
                videoStream.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                videoStream.Set(MediaTypeAttributeKeys.FrameRate, ((long)Options.RecordingFrameRate << 32) | 1);
                videoStream.Set(MediaTypeAttributeKeys.FrameSize, ((long)width << 32) | (uint)height);
                writer.AddStream(videoStream, out _videoOutputIndex);
                Trace("Added Video Stream index: " + _videoOutputIndex);
            }

            using (var video = new MediaType())
            {
                video.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                video.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                video.Set(MediaTypeAttributeKeys.FrameSize, ((long)width << 32) | (uint)height);
                video.Set(MediaTypeAttributeKeys.FrameRate, ((long)Options.RecordingFrameRate << 32) | 1);
                Trace("Add Video Input Media Type");
                writer.SetInputMediaType(_videoOutputIndex, video, null);
            }

            if (Options.EnableSoundRecording)
            {
                using (var audioStream = new MediaType())
                {
                    audioStream.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    audioStream.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    audioStream.Set(MediaTypeAttributeKeys.AudioNumChannels, 2);
                    audioStream.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 44100);
                    audioStream.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
                    writer.AddStream(audioStream, out _audioOutputIndex);
                    Trace("Added Audio Stream index: " + _audioOutputIndex);
                }

                using (var audio = new MediaType())
                {
                    audio.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    audio.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    audio.Set(MediaTypeAttributeKeys.AudioNumChannels, 2);
                    audio.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 44100);
                    audio.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16);
                    Trace("Add Audio Input Media Type");
                    writer.SetInputMediaType(_audioOutputIndex, audio, null);
                }
            }

            // gather some information
            IsUsingBuiltinEncoder = H264Encoder.IsBuiltinEncoder(writer, _videoOutputIndex);
            IsUsingDirect3D11AwareEncoder = H264Encoder.IsDirect3D11AwareEncoder(writer, _videoOutputIndex);
            IsUsingHardwareBasedEncoder = H264Encoder.IsHardwareBasedEncoder(writer, _videoOutputIndex);
            Trace("IsBuiltinEncoder: " + IsUsingBuiltinEncoder + " IsUsingDirect3D11AwareEncoder: " + IsUsingDirect3D11AwareEncoder + " IsUsingHardwareBasedEncoder: " + IsUsingHardwareBasedEncoder);
            OnPropertyChanged(nameof(IsUsingBuiltinEncoder));
            OnPropertyChanged(nameof(IsUsingDirect3D11AwareEncoder));
            OnPropertyChanged(nameof(IsUsingHardwareBasedEncoder));
            Trace("Begin Writing");

            writer.BeginWriting();
            _watch.Start();

            if (Options.EnableSoundRecording)
            {
                var ad = Options.GetAudioDevice() ?? LoopbackAudioCapture.GetSpeakersDevice();
                _audioCapture.Start(ad);
            }
            OnPropertyChanged(nameof(IsRecording));
            return writer;
        }

        private MediaSink CreateMediaSink()
        {
            // note: we can only call that *after* BeginWriting
            const int MF_SINK_WRITER_MEDIASINK = -1;
            _sinkWriter.Value.GetServiceForStream(MF_SINK_WRITER_MEDIASINK, Guid.Empty, typeof(MediaSink).GUID, out IntPtr sinkPtr);
            return new MediaSink(sinkPtr);
        }

        // compute the shape of the pointer bitmap
        private void ComputePointerBitmap(ref OutputDuplicateFrameInformation frameInfo)
        {
            var bmp = _pointerBitmap;
            _pointerBitmap = null;
            if (bmp != null)
            {
                bmp.Dispose();
            }

            var pointerShapeBuffer = Marshal.AllocHGlobal(frameInfo.PointerShapeBufferSize);
            OutputDuplicatePointerShapeInformation shapeInfo;
            try
            {
                _outputDuplication.Value.GetFramePointerShape(frameInfo.PointerShapeBufferSize, pointerShapeBuffer, out int shapeInfoSize, out shapeInfo);
                //Trace("new pointer alloc size: " + frameInfo.PointerShapeBufferSize + " size:" + shapeInfo.Width + "x" + shapeInfo.Height +
                //    " hs: " + shapeInfo.HotSpot.X + "x" + shapeInfo.HotSpot.Y +
                //    " pitch: " + shapeInfo.Pitch + " type: " + shapeInfo.Type +
                //    " pos: " + _pointerPosition.X + "x" + _pointerPosition.Y);
            }
            catch
            {
                Marshal.FreeHGlobal(pointerShapeBuffer);
                throw;
            }

            try
            {
                _pointerType = shapeInfo.Type;
                _pointerHotspot = shapeInfo.HotSpot;
                int bufferSize;
                int pitch;
                Size2 size;
                const int DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME = 1;
                if (shapeInfo.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME)
                {
                    var ptr = ComputeMonochromePointerShape(shapeInfo, pointerShapeBuffer, out bufferSize);
                    Marshal.FreeHGlobal(pointerShapeBuffer);
                    pointerShapeBuffer = ptr;
                    size = new Size2(shapeInfo.Width, shapeInfo.Height / 2);
                    pitch = shapeInfo.Width * 4;
                }
                else // note: we do not handle DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR...
                {
                    bufferSize = frameInfo.PointerShapeBufferSize;
                    size = new Size2(shapeInfo.Width, shapeInfo.Height);
                    pitch = shapeInfo.Pitch;
                }

                // we need to handle alpha channel for the pointer
                var bprops = new BitmapProperties();
                bprops.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied);
                _pointerBitmap = new Bitmap(_backBufferDc.Value, size, new DataPointer(pointerShapeBuffer, bufferSize), pitch, bprops);
            }
            finally
            {
                Marshal.FreeHGlobal(pointerShapeBuffer);
            }
        }

        // handling DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME
        // https://msdn.microsoft.com/en-us/library/windows/desktop/hh404520.aspx
        // The pointer type is a monochrome mouse pointer, which is a monochrome bitmap.
        // The bitmap's size is specified by width and height in a 1 bits per pixel (bpp) device independent bitmap (DIB) format AND mask
        //  that is followed by another 1 bpp DIB format XOR mask of the same size.
        private static IntPtr ComputeMonochromePointerShape(OutputDuplicatePointerShapeInformation shapeInfo, IntPtr inBuffer, out int size)
        {
            const int bpp = 4;
            size = shapeInfo.Width * (shapeInfo.Height / 2) * bpp;
            var ptr = Marshal.AllocHGlobal(size);
            for (int row = 0; row < shapeInfo.Height / 2; row++)
            {
                int mask = 0x80;
                for (int col = 0; col < shapeInfo.Width; col++)
                {
                    var and = (Marshal.ReadByte(inBuffer, col / 8 + row * shapeInfo.Pitch) & mask) != 0;
                    var xor = (Marshal.ReadByte(inBuffer, col / 8 + (row + (shapeInfo.Height / 2)) * shapeInfo.Pitch) & mask) != 0;

                    uint value;
                    if (and)
                    {
                        if (xor)
                        {
                            value = 0xFF000000;
                        }
                        else
                        {
                            value = 0x00000000;
                        }
                    }
                    else
                    {
                        if (xor)
                        {
                            value = 0xFFFFFFFF;
                        }
                        else
                        {
                            value = 0xFF000000;
                        }
                    }
                    Marshal.WriteInt32(ptr, row * shapeInfo.Width * bpp + col * bpp, (int)value);

                    if (mask == 0x01)
                    {
                        mask = 0x80;
                    }
                    else
                    {
                        mask = mask >> 1;
                    }
                }
            }
            return ptr;
        }

        internal static void Trace(object value, [CallerMemberName] string methodName = null) => _provider.WriteMessageEvent("#Duplicator(" + Thread.CurrentThread.ManagedThreadId + ")::" + methodName + " " + string.Format("{0}", value), 0, 0);

        // a multi thread version is:
        // disposable = Interlocked.Exchange(ref disposable, null)?.Dispose();
        private static T Dispose<T>(T disposable) where T : IDisposable
        {
            disposable?.Dispose();
            return default(T);
        }

        private static Lazy<T> Reset<T>(Lazy<T> disposable, Func<T> valueFactory) where T : IDisposable
        {
            if (disposable != null && disposable.IsValueCreated)
            {
                disposable.Value?.Dispose();
            }
            return new Lazy<T>(valueFactory);
        }

        [DllImport("kernel32")]
        private static extern void CopyMemory(IntPtr Destination, IntPtr Source, int Length);

#if DEBUG
        private static void DXGIReportLiveObjects() => DXGIReportLiveObjects(DXGI_DEBUG_ALL, DXGI_DEBUG_RLO_FLAGS.DXGI_DEBUG_RLO_ALL);
        private static void DXGIReportLiveObjects(Guid apiid) => DXGIReportLiveObjects(apiid, DXGI_DEBUG_RLO_FLAGS.DXGI_DEBUG_RLO_ALL);
        private static void DXGIReportLiveObjects(Guid apiid, DXGI_DEBUG_RLO_FLAGS flags)
        {
            DXGIGetDebugInterface(typeof(IDXGIDebug).GUID, out IDXGIDebug debug);
            if (debug == null)
                return;

            debug.ReportLiveObjects(apiid, flags);
            Marshal.ReleaseComObject(debug);
        }

        [DllImport("Dxgidebug")]
        private static extern int DXGIGetDebugInterface([MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IDXGIDebug debug);

        private enum DXGI_DEBUG_RLO_FLAGS
        {
            DXGI_DEBUG_RLO_SUMMARY = 0x1,
            DXGI_DEBUG_RLO_DETAIL = 0x2,
            DXGI_DEBUG_RLO_IGNORE_INTERNAL = 0x4,
            DXGI_DEBUG_RLO_ALL = 0x7
        }

        private static Guid DXGI_DEBUG_ALL = new Guid("e48ae283-da80-490b-87e6-43e9a9cfda08");
        private static Guid DXGI_DEBUG_DX = new Guid("35cdd7fc-13b2-421d-a5d7-7e4451287d64");
        private static Guid DXGI_DEBUG_DXGI = new Guid("25cddaa4-b1c6-47e1-ac3e-98875b5a2e2a");
        private static Guid DXGI_DEBUG_APP = new Guid("06cd6e01-4219-4ebd-8709-27ed23360c62");
        private static Guid DXGI_DEBUG_D3D10 = new Guid("243b4c52-3606-4d3a-99d7-a7e7b33ed706");
        private static Guid DXGI_DEBUG_D3D11 = new Guid("4b99317b-ac39-4aa6-bb0b-baa04784798f");
        private static Guid DXGI_DEBUG_D3D12 = new Guid("cf59a98c-a950-4326-91ef-9bbaa17bfd95");

        [Guid("119e7452-de9e-40fe-8806-88f90c12b441"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIDebug
        {
            void ReportLiveObjects(Guid apiid, DXGI_DEBUG_RLO_FLAGS flags);
        }
#endif
    }
}
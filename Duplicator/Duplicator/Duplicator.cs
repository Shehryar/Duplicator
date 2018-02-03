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
    public class Duplicator : IDisposable
    {
        // use that guid in TraceSpy's ETW Trace Provider (https://github.com/smourier/TraceSpy)
        private static EventProvider _provider = new EventProvider(new Guid("964d4572-adb9-4f3a-8170-fcbecec27465"));

        // recording
        private bool _recordingEnabled;
        private Lazy<SinkWriter> _sinkWriter;
        private int _videoOutputIndex;
        private Lazy<DXGIDeviceManager> _devManager;
        private Stopwatch _watch = new Stopwatch();
        private long _lastNs;

        // duplicating
        private bool _duplicationEnabled;
        private Lazy<SharpDX.Direct3D11.Device> _device;
        private Lazy<OutputDuplication> _outputDuplication;
        private Lazy<Output1> _output;
        private Lazy<TextFormat> _diagsTextFormat;
        private Lazy<DeviceContextRenderTarget> _renderTarget;
        private Lazy<Brush> _diagsBrush;
        private Lazy<Texture2D> _frameCopy;
        private Lazy<Size2> _desktopSize;
        private Bitmap _pointerBitmap;
        private System.Threading.Timer _frameRateTimer;
        private Thread _duplicationThread;
        private int _accumulatedFramesCount;
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
            _renderTarget = new Lazy<DeviceContextRenderTarget>(CreateRenderTarget);
            _frameCopy = new Lazy<Texture2D>(CreateFrameCopy);
            _diagsTextFormat = new Lazy<TextFormat>(CreateDiagsTextFormat);
            _diagsBrush = new Lazy<Brush>(CreateDiagsBrush);
            _frameRateTimer = new System.Threading.Timer((state) =>
            {
                _currentFrameRate = _currentFrameNumber;
                _currentFrameNumber = 0;
            }, null, 0, 1000);
        }

        public DuplicatorOptions Options { get; }
        public SharpDX.DXGI.Resource Frame { get => _frame; set => _frame = value; }
        public IntPtr Hdc { get; set; }
        public string RecordFilePath { get; set; }
        public Size2 DesktopSize => _desktopSize.Value;
        public Size2 RenderSize { get; private set; }
        public Size2 Size
        {
            get => _size;
            set
            {
                _size = value;
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
            }
        }

        public bool IsRecording { get => _sinkWriter.IsValueCreated; set { _recordingEnabled = value; } }

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
                    _duplicationThread = new Thread(WorkThreadCallback);
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

        private void WriteFrame()
        {
            var frame = _frame;
            if (frame == null)
                return;

            using (var sample = MediaFactory.CreateSample())
            {
                MediaFactory.CreateDXGISurfaceBuffer(typeof(Texture2D).GUID, frame, 0, new RawBool(true), out MediaBuffer buffer);
                using (buffer)
                using (var buffer2 = buffer.QueryInterface<Buffer2D>())
                {
                    // in 100-nanosecond units
                    // 1 ns = 1/1000000000s;
                    // 100 ns = 100/1000000000s = 1/10000000s

                    var elapsedTicks = _watch.ElapsedTicks;
                    var elapsedNs = (10000000 * elapsedTicks) / Stopwatch.Frequency;
                    sample.SampleDuration = elapsedNs - _lastNs;
                    sample.SampleTime = elapsedNs;
                    _lastNs = elapsedNs;
                    buffer.CurrentLength = buffer2.ContiguousLength;
                    sample.AddBuffer(buffer);
                    _sinkWriter.Value.WriteSample(_videoOutputIndex, sample);
                }
            }
        }

        private bool TryGetFrame()
        {
            var od = _outputDuplication.Value; // can be null if adapter is not connected
            if (od == null)
                return false;

            var frame = Frame;
            if (frame != null)
            {
                Frame = null;
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

            Frame = resource;
            return true;
        }

        private void WorkThreadCallback()
        {
            do
            {
                if (_duplicationEnabled)
                {
                    if (TryGetFrame())
                    {
                        if (_duplicationEnabled)
                        {
                            RenderFrame();
                        }

                        if (_recordingEnabled)
                        {
                            WriteFrame();
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

        public void RenderFrame()
        {
            if (Hdc == IntPtr.Zero || RenderSize.Width == 0 || RenderSize.Height == 0)
                return;

            var frame = Frame;
            if (frame == null)
                return;

            using (var surface = _frameCopy.Value.QueryInterface<Surface>())
            {
                using (var res = frame.QueryInterface<SharpDX.Direct3D11.Resource>())
                {
                    _device.Value.ImmediateContext.CopyResource(res, _frameCopy.Value);
                }

                var map = surface.Map(SharpDX.DXGI.MapFlags.Read, out DataStream ds);
                using (ds)
                {
                    var bprops = new BitmapProperties();
                    bprops.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore);
                    _renderTarget.Value.BindDeviceContext(Hdc, new RawRectangle(0, 0, Size.Width, Size.Height));
                    _renderTarget.Value.BeginDraw();
                    _renderTarget.Value.Clear(new RawColor4(1, 1, 1, 1));

                    var renderX = (Size.Width - RenderSize.Width) / 2;
                    var renderY = (Size.Height - RenderSize.Height) / 2;
                    using (var bmp = new Bitmap(_renderTarget.Value, DesktopSize, ds, map.Pitch, bprops))
                    {
                        _renderTarget.Value.DrawBitmap(bmp, new RawRectangleF(renderX, renderY, renderX + RenderSize.Width, renderY + RenderSize.Height), 1, BitmapInterpolationMode.Linear);
                    }

                    var diags = new List<string>();
                    if (Options.ShowInputFps)
                    {
                        diags.Add(_currentFrameRate + " fps");
                    }

                    if (Options.ShowAccumulatedFrames)
                    {
                        diags.Add(_accumulatedFramesCount + " af");
                    }

                    if (Options.ShowCursor && _pointerVisible)
                    {
                        var pb = _pointerBitmap;
                        if (pb != null)
                        {
                            RawRectangleF rect;

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

                            _renderTarget.Value.DrawBitmap(pb, rect, 1, BitmapInterpolationMode.NearestNeighbor);
                        }
                    }

                    if (diags.Count > 0)
                    {
                        _renderTarget.Value.DrawText(string.Join(Environment.NewLine, diags), _diagsTextFormat.Value, new RawRectangleF(0, 0, Size.Width, Size.Height), _diagsBrush.Value);
                    }

                    _renderTarget.Value.EndDraw();
                }
            }
        }

        public void Dispose()
        {
            IsDuplicating = false;
            _frameRateTimer = Dispose(_frameRateTimer);
#if DEBUG
            DXGIReportLiveObjects();
#endif
        }

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

        private void DisposeRecording()
        {
            if (!_sinkWriter.IsValueCreated)
                return;

            _sinkWriter.Value.Finalize();

            _sinkWriter = Reset(_sinkWriter, CreateSinkWriter);
            _devManager = Reset(_devManager, CreateDeviceManager);
            _watch.Stop();
            _lastNs = 0;
            _videoOutputIndex = 0;
        }

        private void DisposeDuplication()
        {
            _currentFrameRate = 0;
            _currentFrameNumber = 0;
            _desktopSize = new Lazy<Size2>(CreateDesktopSize);
            _pointerBitmap = Dispose(_pointerBitmap);
            _diagsTextFormat = Reset(_diagsTextFormat, CreateDiagsTextFormat);
            _diagsBrush = Reset(_diagsBrush, CreateDiagsBrush);
            _frame = Dispose(_frame);
            _renderTarget = Reset(_renderTarget, CreateRenderTarget);
            _frameCopy = Reset(_frameCopy, CreateFrameCopy);
            _output = Reset(_output, CreateOutput);
            _outputDuplication = Reset(_outputDuplication, CreateOutputDuplication);
#if DEBUG
            _deviceDebug = Reset(_deviceDebug, CreateDeviceDebug);
#endif
            _device = Reset(_device, CreateDevice);
        }

        private void OnOptionsChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DuplicatorOptions.Adapter):
                case nameof(DuplicatorOptions.Output):
                    var duplicating = IsDuplicating;
                    IsDuplicating = false;
                    DisposeDuplication();
                    IsDuplicating = duplicating;
                    break;

                case nameof(DuplicatorOptions.PreserveRatio):
                    Size = Size;
                    break;
            }
        }

#if DEBUG
        private DeviceDebug CreateDeviceDebug()
        {
            return _device.Value.QueryInterface<DeviceDebug>();
        }
#endif

        private SharpDX.Direct3D11.Device CreateDevice()
        {
            using (var fac = new SharpDX.DXGI.Factory1())
            {
                using (var adapter = Options.GetAdapter())
                {
                    if (adapter == null)
                        return null;

                    var flags = DeviceCreationFlags.BgraSupport;
                    //flags |= DeviceCreationFlags.VideoSupport;
#if DEBUG
                    flags |= DeviceCreationFlags.Debug;
#endif
                    return new SharpDX.Direct3D11.Device(adapter, flags);
                }
            }
        }

        private Size2 CreateDesktopSize()
        {
            return _output.Value != null ? new Size2(
                _output.Value.Description.DesktopBounds.Right - _output.Value.Description.DesktopBounds.Left,
                _output.Value.Description.DesktopBounds.Bottom - _output.Value.Description.DesktopBounds.Top) : new Size2();
        }

        private Output1 CreateOutput()
        {
            return Options.GetOutput();
        }

        private Texture2D CreateFrameCopy()
        {
            var desc = new Texture2DDescription()
            {
                CpuAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = Format.B8G8R8A8_UNorm,
                Width = DesktopSize.Width,
                Height = DesktopSize.Height,
                OptionFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = { Count = 1, Quality = 0 },
                Usage = ResourceUsage.Staging
            };

            return new Texture2D(_device.Value, desc);
        }

        private Brush CreateDiagsBrush()
        {
            return new SolidColorBrush(_renderTarget.Value, new RawColor4(1, 0, 0, 1));
        }

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

        private OutputDuplication CreateOutputDuplication()
        {
            return _output.Value?.DuplicateOutput(_device.Value);
        }

        private DXGIDeviceManager CreateDeviceManager()
        {
            var devManager = new DXGIDeviceManager();
            MediaFactory.CreateDXGIDeviceManager(out int token, devManager);
            devManager.ResetDevice(_device.Value);
            return devManager;
        }

        private SinkWriter CreateSinkWriter()
        {
            MediaManager.Startup(); // this can be called more than one time

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
                writer = MediaFactory.CreateSinkWriterFromURL(RecordFilePath, IntPtr.Zero, ma);
            }

            using (var output = new MediaType())
            {
                // avg bitrate is mandatory for builtin encoder, not for some others like Intel Media SDK
                // in fact, what will that be used for? anyway, here is a standard formula from here
                // https://stackoverflow.com/questions/5024114/suggested-compression-ratio-with-h-264
                //
                // [image width] x [image height] x [framerate] x [motion rank] x 0.07 = [desired bitrate]
                //

                int motionRank = 1;
                int bitrate = (int)(width * height * Options.RecordingFrameRate * motionRank);
                if (bitrate <= 0)
                    throw new InvalidOperationException();

                output.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.FromFourCC(new FourCC("H264")));
                output.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                output.Set(MediaTypeAttributeKeys.FrameRate, ((long)Options.RecordingFrameRate << 32) | 1);
                output.Set(MediaTypeAttributeKeys.FrameSize, ((long)width << 32) | (uint)height);
                writer.AddStream(output, out _videoOutputIndex);
            }

            using (var input = new MediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
                input.Set(MediaTypeAttributeKeys.FrameSize, ((long)width << 32) | (uint)height);
                input.Set(MediaTypeAttributeKeys.FrameRate, ((long)Options.RecordingFrameRate << 32) | 1);
                writer.SetInputMediaType(_videoOutputIndex, input, null);
            }

            bool bi = H264Encoder.IsBuiltinEncoder(writer, _videoOutputIndex);
            writer.BeginWriting();
            _watch.Start();
            return writer;
        }

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

                var bprops = new BitmapProperties();
                bprops.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied);
                _pointerBitmap = new Bitmap(_renderTarget.Value, size, new DataPointer(pointerShapeBuffer, bufferSize), pitch, bprops);
            }
            finally
            {
                Marshal.FreeHGlobal(pointerShapeBuffer);
            }
        }

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

        private static void Trace(object value, [CallerMemberName] string methodName = null) => _provider.WriteMessageEvent(methodName + ": " + string.Format("{0}", value), 0, 0);

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
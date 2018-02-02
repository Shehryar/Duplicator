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
        private volatile bool _recording;
        private SinkWriter _sinkWriter;
        private static bool _mfStarted;
        private int _videoOutputIndex;
        private DXGIDeviceManager _devManager;
        private Stopwatch _watch = new Stopwatch();
        private long _lastNs;

        // duplicating
        private volatile bool _duplicating;
        private System.Threading.Timer _frameRateTimer;
        private Thread _duplicationThread;
        private TextFormat _diagsTextFormat;
        private Brush _diagsBrush;
        private int _accumulatedFramesCount;
        private int _currentFrameNumber;
        private int _currentFrameRate;
        private DeviceContextRenderTarget _renderTarget;
        private Texture2D _copy;
        private SharpDX.Direct3D11.Device _device;
        private Output1 _output;
        private OutputDuplication _outputDuplication;
        private Bitmap _pointerBitmap;
        private RawPoint _pointerPosition;
        private RawPoint _pointerHotspot;
        private bool _pointerVisible;
        private int _pointerType;
        private Size2 _size;
        private SharpDX.DXGI.Resource _frame;
#if DEBUG
        private DeviceDebug _deviceDebug;
#endif

        public event EventHandler<CancelEventArgs> FrameAcquired;

        public Duplicator(DuplicatorOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            Options = options;
            Options.PropertyChanged += OnOptionsChanged;
        }

        public DuplicatorOptions Options { get; }
        public SharpDX.DXGI.Resource Frame { get => _frame; set => _frame = value; }
        public IntPtr Hdc { get; set; }
        public string RecordFilePath { get; set; }
        public Size2 DesktopSize { get; private set; }
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

        public bool IsRecording
        {
            get => _recording;
            set
            {
                if (_recording == value)
                    return;

                _recording = value;
                if (value)
                {
                    DisposeRecording();
                    InitRecording();
                }
                else
                {
                    if (_sinkWriter != null)
                    {
                        _sinkWriter.NotifyEndOfSegment(_videoOutputIndex);
                        _sinkWriter.Finalize();
                    }
                    DisposeRecording();
                }
            }
        }

        public bool IsDuplicating
        {
            get => _duplicating;
            set
            {
                if (_duplicating == value)
                    return;

                DisposeDuplication();
                _duplicating = value;
                if (value)
                {
                    InitDuplication();
                    Size = Size;
                    _duplicationThread = new Thread(DuplicateThreadCallback);
                    _duplicationThread.Name = nameof(Duplicator) + Environment.TickCount;
                    _duplicationThread.IsBackground = true;
                    _duplicationThread.Start();
                }
                else
                {
                    IsRecording = false;
                }
            }
        }

        private void InitRecording()
        {
            if (!_mfStarted)
            {
                MediaManager.Startup();
                _mfStarted = true;
            }

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
            _devManager = new DXGIDeviceManager();
            MediaFactory.CreateDXGIDeviceManager(out int token, _devManager);
            _devManager.ResetDevice(_device);

            using (var ma = new MediaAttributes())
            {
                // note this doesn't mean you *will* have a hardware transform. Intel Media SDK sometimes is not happy with configuration for HDCP issues.
                ma.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, Options.EnableHardwareTransforms ? 1 : 0);
                ma.Set(SinkWriterAttributeKeys.D3DManager, _devManager);
                ma.Set(SinkWriterAttributeKeys.DisableThrottling, Options.DisableThrottling ? 1 : 0);
                ma.Set(SinkWriterAttributeKeys.LowLatency, Options.LowLatency);
                _sinkWriter = MediaFactory.CreateSinkWriterFromURL(RecordFilePath, IntPtr.Zero, ma);
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
                _sinkWriter.AddStream(output, out _videoOutputIndex);
            }

            using (var input = new MediaType())
            {
                input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
                input.Set(MediaTypeAttributeKeys.FrameSize, ((long)width << 32) | (uint)height);
                input.Set(MediaTypeAttributeKeys.FrameRate, ((long)Options.RecordingFrameRate << 32) | 1);
                _sinkWriter.SetInputMediaType(_videoOutputIndex, input, null);
            }

            bool bi = H264Encoder.IsBuiltinEncoder(_sinkWriter, _videoOutputIndex);
            _sinkWriter.BeginWriting();
            _watch.Start();
        }

        private void WriteFrame()
        {
            if (!_recording || !_watch.IsRunning)
                return;

            var frame = _frame;
            if (frame == null)
                return;

            var sw = _sinkWriter;
            if (sw == null)
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
                    sw.WriteSample(_videoOutputIndex, sample);
                }
            }
        }

        private void InitDuplication()
        {
            _frameRateTimer = new System.Threading.Timer((state) =>
            {
                _currentFrameRate = _currentFrameNumber;
                _currentFrameNumber = 0;
            }, null, 0, 1000);

            using (var fac = new SharpDX.DXGI.Factory1())
            {
                using (var adapter = Options.GetAdapter())
                {
                    if (adapter == null)
                        return;

                    var flags = DeviceCreationFlags.BgraSupport;
                    //flags |= DeviceCreationFlags.VideoSupport;
#if DEBUG
                    flags |= DeviceCreationFlags.Debug;
#endif
                    _device = new SharpDX.Direct3D11.Device(adapter, flags);
#if DEBUG
                    _deviceDebug = _device.QueryInterface<DeviceDebug>();
#endif
                    _output = Options.GetOutput();
                    DesktopSize = _output != null ? new Size2(
                        _output.Description.DesktopBounds.Right - _output.Description.DesktopBounds.Left,
                        _output.Description.DesktopBounds.Bottom - _output.Description.DesktopBounds.Top) : new Size2();
                    _outputDuplication = _output?.DuplicateOutput(_device);
                }
            }

            using (var fac = new SharpDX.DirectWrite.Factory1())
            {
                _diagsTextFormat = new TextFormat(fac, "Lucida Console", 16);
                _diagsTextFormat.WordWrapping = WordWrapping.Character;
            }
        }

        private bool TryGetFrame()
        {
            var od = _outputDuplication;
            var frame = Frame;
            if (frame != null)
            {
                Frame = null;
                frame.Dispose();
                od?.ReleaseFrame();
            }
            if (od == null)
                return false;

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
                    InitDuplication();
                    return false;
                }

                return false;
            }

            Frame = resource;
            return true;
        }

        private void DuplicateThreadCallback()
        {
            var sw = new Stopwatch();
            sw.Start();
            do
            {
                if (!_duplicating)
                    return;

                try
                {
                    if (TryGetFrame())
                    {
                        var e = new CancelEventArgs();
                        FrameAcquired?.Invoke(this, e);
                        if (e.Cancel)
                        {
                            IsDuplicating = false;
                            return;
                        }

                        WriteFrame();
                    }
                }
                catch (SharpDXException ex)
                {
                    System.Diagnostics.Trace.WriteLine("An error occured: " + ex);
                    // continue
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

            var output = _output;
            if (output == null)
                return;

            var device = _device;
            if (device == null)
                return;

            var dcrt = _renderTarget;
            var copy = _copy;
            if (copy == null)
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

                copy = new Texture2D(device, desc);
                _copy = copy;
            }

            if (dcrt == null)
            {
                using (var fac = new SharpDX.Direct2D1.Factory1())
                {
                    var props = new RenderTargetProperties();
                    props.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore);
                    dcrt = new DeviceContextRenderTarget(fac, props);
                    _renderTarget = dcrt;

                    _diagsBrush = new SolidColorBrush(dcrt, new RawColor4(1, 0, 0, 1));
                }
            }

            using (var surface = copy.QueryInterface<Surface>())
            {
                using (var res = frame.QueryInterface<SharpDX.Direct3D11.Resource>())
                {
                    device.ImmediateContext.CopyResource(res, copy);
                }

                var map = surface.Map(SharpDX.DXGI.MapFlags.Read, out DataStream ds);
                using (ds)
                {
                    var bprops = new BitmapProperties();
                    bprops.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore);
                    dcrt.BindDeviceContext(Hdc, new RawRectangle(0, 0, Size.Width, Size.Height));
                    dcrt.BeginDraw();
                    dcrt.Clear(new RawColor4(1, 1, 1, 1));

                    var renderX = (Size.Width - RenderSize.Width) / 2;
                    var renderY = (Size.Height - RenderSize.Height) / 2;
                    using (var bmp = new Bitmap(dcrt, DesktopSize, ds, map.Pitch, bprops))
                    {
                        dcrt.DrawBitmap(bmp, new RawRectangleF(renderX, renderY, renderX + RenderSize.Width, renderY + RenderSize.Height), 1, BitmapInterpolationMode.Linear);
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

                            dcrt.DrawBitmap(pb, rect, 1, BitmapInterpolationMode.NearestNeighbor);
                        }
                    }

                    if (diags.Count > 0)
                    {
                        dcrt.DrawText(string.Join(Environment.NewLine, diags), _diagsTextFormat, new RawRectangleF(0, 0, Size.Width, Size.Height), _diagsBrush);
                    }

                    dcrt.EndDraw();
                }
            }
        }

        public void Dispose()
        {
            DisposeRecording();
            DisposeDuplication();
#if DEBUG
            DXGIReportLiveObjects();
#endif
        }

        private void DisposeRecording()
        {
            Interlocked.Exchange(ref _sinkWriter, null)?.Dispose();
            Interlocked.Exchange(ref _devManager, null)?.Dispose();
            _watch.Stop();
            _lastNs = 0;
            _videoOutputIndex = 0;
        }

        private void DisposeDuplication()
        {
            _currentFrameRate = 0;
            _currentFrameNumber = 0;
            _duplicating = false;
            Interlocked.Exchange(ref _frameRateTimer, null)?.Dispose();
            Interlocked.Exchange(ref _duplicationThread, null)?.Join((int)Math.Min(Options.FrameAcquisitionTimeout * 2L, int.MaxValue));
            Interlocked.Exchange(ref _pointerBitmap, null)?.Dispose();
            Interlocked.Exchange(ref _diagsTextFormat, null)?.Dispose();
            Interlocked.Exchange(ref _diagsBrush, null)?.Dispose();
            Interlocked.Exchange(ref _frame, null)?.Dispose();
            Interlocked.Exchange(ref _renderTarget, null)?.Dispose();
            Interlocked.Exchange(ref _copy, null)?.Dispose();
            Interlocked.Exchange(ref _output, null)?.Dispose();
            Interlocked.Exchange(ref _outputDuplication, null)?.Dispose();
#if DEBUG
            Interlocked.Exchange(ref _deviceDebug, null)?.Dispose();
#endif
            Interlocked.Exchange(ref _device, null)?.Dispose();
        }

        private void OnOptionsChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(DuplicatorOptions.Adapter):
                case nameof(DuplicatorOptions.Output):
                    var duplicating = _duplicating;
                    IsDuplicating = false;
                    IsDuplicating = duplicating;
                    break;

                case nameof(DuplicatorOptions.PreserveRatio):
                    Size = Size;
                    break;
            }
        }

        private void ComputePointerBitmap(ref OutputDuplicateFrameInformation frameInfo)
        {
            var od = _outputDuplication;
            if (od == null)
                return;

            var dcrt = _renderTarget;
            if (dcrt == null)
                return;

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
                od.GetFramePointerShape(frameInfo.PointerShapeBufferSize, pointerShapeBuffer, out int shapeInfoSize, out shapeInfo);
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
                _pointerBitmap = new Bitmap(dcrt, size, new DataPointer(pointerShapeBuffer, bufferSize), pitch, bprops);
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
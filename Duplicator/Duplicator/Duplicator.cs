using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.Direct3D11;
using SharpDX.DirectWrite;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace Duplicator
{
    public class Duplicator : IDisposable
    {
        // use that guid in TraceSpy's ETW Trace Provider (https://github.com/smourier/TraceSpy)
        private static EventProvider _provider = new EventProvider(new Guid("964d4572-adb9-4f3a-8170-fcbecec27465"));

        private Timer _timer;
        private Thread _thread;
        private TextFormat _textFormat;
        private Brush _brush;
        private int _accumulatedFrames;
        private int _frameNumber;
        private int _frameRate;
        private volatile bool _duplicating;
        private DeviceContextRenderTarget _dcrt;
        private Texture2D _dest;
        private SharpDX.Direct3D11.Device _device;
        private Output1 _output;
        private OutputDuplication _outputDuplication;
        private OutputDuplicatePointerShapeInformation _pointerShapeInfo;
        private OutputDuplicateFrameInformation _frameInfo;
        private int _pointerShapeBufferSize;
        private IntPtr _pointerShapeBuffer;
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
        public int Width { get; set; }
        public int Height { get; set; }

        public bool IsDuplicating
        {
            get => _duplicating;
            set
            {
                if (_duplicating == value)
                    return;

                Dispose();
                _duplicating = value;
                if (value)
                {
                    Init();
                    _thread = new Thread(Duplicate);
                    _thread.Name = nameof(Duplicator) + Environment.TickCount;
                    _thread.IsBackground = true;
                    _thread.Start();
                }
            }
        }

        private void Init()
        {
            _timer = new Timer((state) =>
            {
                _frameRate = _frameNumber;
                _frameNumber = 0;
            }, null, 0, 1000);

            using (var fac = new SharpDX.DXGI.Factory1())
            {
                using (var adapter = Options.GetAdapter())
                {
                    if (adapter == null)
                        return;

                    var flags = DeviceCreationFlags.BgraSupport;
#if DEBUG
                    flags |= DeviceCreationFlags.Debug;
#endif
                    _device = new SharpDX.Direct3D11.Device(adapter, flags);
#if DEBUG
                    _deviceDebug = _device.QueryInterface<DeviceDebug>();
#endif
                    _output = Options.GetOutput();
                    _outputDuplication = _output.DuplicateOutput(_device);
                }
            }

            using (var fac = new SharpDX.DirectWrite.Factory1())
            {
                _textFormat = new TextFormat(fac, "Lucida Console", 20);
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
                _frameNumber++;
                _accumulatedFrames = _frameInfo.AccumulatedFrames;
                if (frameInfo.LastMouseUpdateTime != 0)
                {
                    _frameInfo = frameInfo;
                    if (_frameInfo.PointerShapeBufferSize > 0)
                    {
                        if (_pointerShapeBuffer != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(_pointerShapeBuffer);
                        }

                        _pointerShapeBufferSize = _frameInfo.PointerShapeBufferSize;
                        _pointerShapeBuffer = Marshal.AllocHGlobal(_pointerShapeBufferSize);
                        od.GetFramePointerShape(_pointerShapeBufferSize, _pointerShapeBuffer, out int size, out _pointerShapeInfo);
                    }
                }
                else
                {
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
                    Init();
                    return false;
                }

                return false;
            }

            Frame = resource;
            return true;
        }

        private void Duplicate()
        {
            var sw = new Stopwatch();
            sw.Start();
            do
            {
                if (!_duplicating)
                    return;

                if (TryGetFrame())
                {
                    var e = new CancelEventArgs();
                    FrameAcquired?.Invoke(this, e);
                    if (e.Cancel)
                    {
                        _duplicating = false;
                        return;
                    }
                }
            }
            while (true);
        }

        public void RenderFrame()
        {
            if (Hdc == IntPtr.Zero || Width == 0 || Height == 0)
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

            var dcrt = _dcrt;
            var dest = _dest;
            if (dest == null)
            {
                var desc = new Texture2DDescription()
                {
                    CpuAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    Format = Format.B8G8R8A8_UNorm,
                    Width = output.Description.DesktopBounds.Right - output.Description.DesktopBounds.Left,
                    Height = output.Description.DesktopBounds.Bottom - output.Description.DesktopBounds.Top,
                    OptionFlags = ResourceOptionFlags.None,
                    MipLevels = 1,
                    ArraySize = 1,
                    SampleDescription = { Count = 1, Quality = 0 },
                    Usage = ResourceUsage.Staging
                };

                dest = new Texture2D(device, desc);
                _dest = dest;

            }

            if (dcrt == null)
            {
                using (var fac = new SharpDX.Direct2D1.Factory1())
                {
                    var props = new RenderTargetProperties();
                    props.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore);
                    dcrt = new DeviceContextRenderTarget(fac, props);
                    _dcrt = dcrt;

                    _brush = new SolidColorBrush(dcrt, new RawColor4(1, 0, 0, 1));
                }
            }

            using (var surface = dest.QueryInterface<Surface>())
            {
                using (var res = frame.QueryInterface<SharpDX.Direct3D11.Resource>())
                {
                    device.ImmediateContext.CopyResource(res, dest);
                }

                var map = surface.Map(SharpDX.DXGI.MapFlags.Read, out DataStream ds);
                using (ds)
                {
                    var bprops = new BitmapProperties();
                    bprops.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore);
                    var size = new Size2(
                        output.Description.DesktopBounds.Right - output.Description.DesktopBounds.Left,
                        output.Description.DesktopBounds.Bottom - output.Description.DesktopBounds.Top
                        );

                    dcrt.BindDeviceContext(Hdc, new RawRectangle(0, 0, Width, Height));
                    dcrt.BeginDraw();
                    using (var bmp = new Bitmap(dcrt, size, ds, map.Pitch, bprops))
                    {
                        dcrt.DrawBitmap(bmp, new RawRectangleF(0, 0, Width, Height), 1, BitmapInterpolationMode.Linear);
                    }

                    var diags = new List<string>();
                    if (Options.ShowInputFps)
                    {
                        diags.Add(_frameRate + " fps");
                    }

                    if (Options.ShowAccumulatedFrames)
                    {
                        diags.Add(_accumulatedFrames + " af");
                    }

                    if (Options.ShowCursor && _frameInfo.PointerPosition.Visible &&
                        _pointerShapeInfo.Type != 2 && _pointerShapeBuffer != IntPtr.Zero)
                    {
                        diags.Add(_pointerShapeInfo.Type + " t " + _pointerShapeInfo.HotSpot.X + "x" + _pointerShapeInfo.HotSpot.Y);
                        size = new Size2(_pointerShapeInfo.Width, _pointerShapeInfo.Height);
                        if (_pointerShapeInfo.Type == 2)
                        {
                            bprops.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied);
                        }
                        else
                        {
                            // TODO
                        }

                        using (var bmp = new Bitmap(dcrt, size, new DataPointer(_pointerShapeBuffer, _pointerShapeBufferSize), _pointerShapeInfo.Pitch, bprops))
                        {
                            int desktopWidth = output.Description.DesktopBounds.Right - output.Description.DesktopBounds.Left;
                            int desktopHeight = output.Description.DesktopBounds.Bottom - output.Description.DesktopBounds.Top;
                            int captureX = (_frameInfo.PointerPosition.Position.X * Width) / desktopWidth;
                            int captureY = (_frameInfo.PointerPosition.Position.Y * Height) / desktopHeight;
                            var rect = new RawRectangleF(
                                captureX - _pointerShapeInfo.HotSpot.X,
                                captureY - _pointerShapeInfo.HotSpot.Y,
                                captureX + _pointerShapeInfo.Width,
                                captureY + _pointerShapeInfo.Height);
                            dcrt.DrawBitmap(bmp, rect, 1, BitmapInterpolationMode.Linear);
                        }
                    }

                    if (diags.Count > 0)
                    {
                        dcrt.DrawText(string.Join(", ", diags), _textFormat, new RawRectangleF(0, 0, Width, 0), _brush);
                    }

                    dcrt.EndDraw();
                }
            }
        }

        public void Dispose()
        {
            _pointerShapeInfo = new OutputDuplicatePointerShapeInformation();
            var ptr = Interlocked.Exchange(ref _pointerShapeBuffer, IntPtr.Zero);
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ptr);
            }
            _pointerShapeBufferSize = 0;
            _frameRate = 0;
            _frameNumber = 0;
            _duplicating = false;
            Interlocked.Exchange(ref _timer, null)?.Dispose();
            Interlocked.Exchange(ref _thread, null)?.Join((int)Math.Min(Options.FrameAcquisitionTimeout * 2L, int.MaxValue));
            Interlocked.Exchange(ref _textFormat, null)?.Dispose();
            Interlocked.Exchange(ref _brush, null)?.Dispose();
            Interlocked.Exchange(ref _frame, null)?.Dispose();
            Interlocked.Exchange(ref _dcrt, null)?.Dispose();
            Interlocked.Exchange(ref _dest, null)?.Dispose();
            Interlocked.Exchange(ref _output, null)?.Dispose();
            Interlocked.Exchange(ref _outputDuplication, null)?.Dispose();
#if DEBUG
            Interlocked.Exchange(ref _deviceDebug, null)?.Dispose();
#endif
            Interlocked.Exchange(ref _device, null)?.Dispose();
        }

        private void OnOptionsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DuplicatorOptions.Adapter) ||
                e.PropertyName == nameof(DuplicatorOptions.Output))
            {
                var duplicating = _duplicating;
                IsDuplicating = false;
                IsDuplicating = duplicating;
            }
        }

        private static void Trace(object value, [CallerMemberName] string methodName = null) => _provider.WriteMessageEvent(string.Format("{0}", value), 0, 0);
    }
}
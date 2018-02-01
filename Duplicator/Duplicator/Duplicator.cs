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
        private Brush _diagsBrush;
        private int _accumulatedFrames;
        private int _frameNumber;
        private int _frameRate;
        private volatile bool _duplicating;
        private DeviceContextRenderTarget _dcrt;
        private Texture2D _dest;
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
                    Size = Size;
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
                    DesktopSize = _output != null ? new Size2(
                        _output.Description.DesktopBounds.Right - _output.Description.DesktopBounds.Left,
                        _output.Description.DesktopBounds.Bottom - _output.Description.DesktopBounds.Top) : new Size2();
                    _outputDuplication = _output?.DuplicateOutput(_device);
                }
            }

            using (var fac = new SharpDX.DirectWrite.Factory1())
            {
                _textFormat = new TextFormat(fac, "Lucida Console", 16);
                _textFormat.WordWrapping = WordWrapping.Character;
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
                _accumulatedFrames = frameInfo.AccumulatedFrames;

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

            var dcrt = _dcrt;
            var dest = _dest;
            if (dest == null)
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

                dest = new Texture2D(device, desc);
                _dest = dest;
                Trace("dest created");
            }

            if (dcrt == null)
            {
                using (var fac = new SharpDX.Direct2D1.Factory1())
                {
                    var props = new RenderTargetProperties();
                    props.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore);
                    dcrt = new DeviceContextRenderTarget(fac, props);
                    _dcrt = dcrt;
                    Trace("dcrt created");

                    _diagsBrush = new SolidColorBrush(dcrt, new RawColor4(1, 0, 0, 1));
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
                    //diags.Add(_pointerPosition.X + "x" + _pointerPosition.Y + " hs: " + _pointerHotspot.X + "x" + _pointerHotspot.Y);
                    if (Options.ShowInputFps)
                    {
                        diags.Add(_frameRate + " fps");
                    }

                    if (Options.ShowAccumulatedFrames)
                    {
                        diags.Add(_accumulatedFrames + " af");
                    }

                    if (Options.ShowCursor && _pointerVisible)
                    {
                        diags.Add("T " + _pointerType + " Pt " + _pointerHotspot.X + "x" + _pointerHotspot.Y);
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
                        dcrt.DrawText(string.Join(Environment.NewLine, diags), _textFormat, new RawRectangleF(0, 0, Size.Width, Size.Height), _diagsBrush);
                    }

                    dcrt.EndDraw();
                }
            }
        }

        public void Dispose()
        {
            _frameRate = 0;
            _frameNumber = 0;
            _duplicating = false;
            Interlocked.Exchange(ref _timer, null)?.Dispose();
            Interlocked.Exchange(ref _thread, null)?.Join((int)Math.Min(Options.FrameAcquisitionTimeout * 2L, int.MaxValue));
            Interlocked.Exchange(ref _pointerBitmap, null)?.Dispose();
            Interlocked.Exchange(ref _textFormat, null)?.Dispose();
            Interlocked.Exchange(ref _diagsBrush, null)?.Dispose();
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

            var dcrt = _dcrt;
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
    }
}
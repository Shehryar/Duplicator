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
        private OutputDuplicateFrameInformation _frameInfo;
        private Bitmap _pointerBitmap;
        private RawPoint _pointerHotspot;
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
                    _outputDuplication = _output?.DuplicateOutput(_device);
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

                // mouse moved?
                if (frameInfo.LastMouseUpdateTime != 0)
                {
                    _frameInfo = frameInfo;
                    ComputePointerBitmap();
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
                    props.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied);
                    dcrt = new DeviceContextRenderTarget(fac, props);
                    _dcrt = dcrt;

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
                    bprops.PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied);
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

                    if (Options.ShowCursor && _frameInfo.PointerPosition.Visible)
                    {
                        diags.Add("Pt " + _pointerHotspot.X + "x" + _pointerHotspot.Y);
                        var pb = _pointerBitmap;
                        if (pb != null)
                        {
                            int desktopWidth = output.Description.DesktopBounds.Right - output.Description.DesktopBounds.Left;
                            int desktopHeight = output.Description.DesktopBounds.Bottom - output.Description.DesktopBounds.Top;
                            RawRectangleF rect;

                            if (Options.IsCursorProportional)
                            {
                                int captureX = ((_frameInfo.PointerPosition.Position.X - _pointerHotspot.X) * Width) / desktopWidth;
                                int captureY = ((_frameInfo.PointerPosition.Position.Y - _pointerHotspot.Y) * Height) / desktopHeight;
                                rect = new RawRectangleF(
                                    captureX,
                                    captureY,
                                    captureX + (pb.Size.Width * Width) / desktopWidth,
                                    captureY + (pb.Size.Height * Height) / desktopHeight);
                            }
                            else
                            {
                                int captureX = (_frameInfo.PointerPosition.Position.X * Width) / desktopWidth - _pointerHotspot.X;
                                int captureY = (_frameInfo.PointerPosition.Position.Y * Height) / desktopHeight - _pointerHotspot.Y;
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
                        dcrt.DrawText(string.Join(", ", diags), _textFormat, new RawRectangleF(0, 0, Width, 0), _diagsBrush);
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
            if (e.PropertyName == nameof(DuplicatorOptions.Adapter) ||
                e.PropertyName == nameof(DuplicatorOptions.Output))
            {
                var duplicating = _duplicating;
                IsDuplicating = false;
                IsDuplicating = duplicating;
            }
        }

        private void ComputePointerBitmap()
        {
            if (_frameInfo.PointerShapeBufferSize == 0)
                return; // nothing to compute

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

            var pointerShapeBuffer = Marshal.AllocHGlobal(_frameInfo.PointerShapeBufferSize);
            OutputDuplicatePointerShapeInformation shapeInfo;
            try
            {
                od.GetFramePointerShape(_frameInfo.PointerShapeBufferSize, pointerShapeBuffer, out int shapeInfoSize, out shapeInfo);
            }
            catch
            {
                Marshal.FreeHGlobal(pointerShapeBuffer);
                throw;
            }

            try
            {
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
                    bufferSize = _frameInfo.PointerShapeBufferSize;
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
            int xorOffset = shapeInfo.Pitch * (shapeInfo.Height / 2);
            var andPtr = inBuffer;
            var xorPtr = inBuffer + xorOffset;
            size = shapeInfo.Width * shapeInfo.Height * 4;
            var ptr = Marshal.AllocHGlobal(size);
            int bytesWidth = (shapeInfo.Width + 7) / 8;
            int height = shapeInfo.Height / 2;
            int width = shapeInfo.Width;

            for (int j = 0; j < height; ++j)
            {
                int bit = 0x80;
                for (int i = 0; i < shapeInfo.Width; ++i)
                {
                    int andByte = Marshal.ReadInt32(andPtr, j * bytesWidth + i / 8);
                    int xorByte = Marshal.ReadInt32(xorPtr, j * bytesWidth + i / 8);
                    int andBit = (andByte & bit) != 0 ? 1 : 0;
                    int xorBit = (xorByte & bit) != 0 ? 1 : 0;
                    int index = j * shapeInfo.Width * 4 + i * 4;

                    if (0 == andBit)
                    {
                        if (0 == xorBit)
                        {
                            Marshal.WriteInt32(ptr, index + 0, 0);
                            Marshal.WriteInt32(ptr, index + 1, 0);
                            Marshal.WriteInt32(ptr, index + 2, 0);
                            Marshal.WriteInt32(ptr, index + 3, 0xff);
                        }
                        else
                        {
                            Marshal.WriteInt32(ptr, index + 0, 0xff);
                            Marshal.WriteInt32(ptr, index + 1, 0xff);
                            Marshal.WriteInt32(ptr, index + 2, 0xff);
                            Marshal.WriteInt32(ptr, index + 3, 0xff);
                        }
                    }
                    else
                    {
                        if (0 == xorBit)
                        {
                            Marshal.WriteInt32(ptr, index + 0, 0);
                            Marshal.WriteInt32(ptr, index + 1, 0);
                            Marshal.WriteInt32(ptr, index + 2, 0);
                            Marshal.WriteInt32(ptr, index + 3, 0);
                        }
                        else
                        {
                            Marshal.WriteInt32(ptr, index + 0, 0);
                            Marshal.WriteInt32(ptr, index + 1, 0);
                            Marshal.WriteInt32(ptr, index + 2, 0);
                            Marshal.WriteInt32(ptr, index + 3, 0xff);
                        }
                    }

                    if (bit == 1)
                    {
                        bit = 0x80;
                    }
                    else
                    {
                        bit = bit >> 1;
                    }
                }
            }
            return ptr;
        }

        private static void Trace(object value, [CallerMemberName] string methodName = null) => _provider.WriteMessageEvent(string.Format("{0}", value), 0, 0);
    }
}
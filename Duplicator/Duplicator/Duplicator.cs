using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing;
using System.Runtime.CompilerServices;
using System.Threading;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace Duplicator
{
    public class Duplicator : IDisposable
    {
        // use that guid in TraceSpy's ETW Trace Provider (https://github.com/smourier/TraceSpy)
        private static EventProvider _provider = new EventProvider(new Guid("964d4572-adb9-4f3a-8170-fcbecec27465"));

        private Thread _thread;
        private volatile bool _duplicating;
        private SharpDX.Direct2D1.DeviceContextRenderTarget _dcrt;
        private Texture2D _dest;
        private SharpDX.Direct3D11.Device _device;
        private Output1 _output;
        private OutputDuplication _outputDuplication;
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
        public SharpDX.DXGI.Resource Frame { get; private set; }

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
            using (var fac = new Factory1())
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
                od.AcquireNextFrame(Options.FrameAcquisitionTimeout, out OutputDuplicateFrameInformation info, out resource);
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

                //Trace("Elapsed 0: " + sw.Elapsed);
                if (TryGetFrame())
                {
                    var e = new CancelEventArgs();
                    //Trace("Elapsed 1: " + sw.Elapsed);
                    FrameAcquired?.Invoke(this, e);
                    //Trace("Elapsed 2: " + sw.Elapsed);
                    if (e.Cancel)
                    {
                        _duplicating = false;
                        return;
                    }
                }
            }
            while (true);
        }

        public void RenderFrame(IntPtr hdc, int width, int height)
        {
            if (Frame == null)
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
                    Width = _output.Description.DesktopBounds.Right - _output.Description.DesktopBounds.Left,
                    Height = _output.Description.DesktopBounds.Bottom - _output.Description.DesktopBounds.Top,
                    OptionFlags = ResourceOptionFlags.None,
                    MipLevels = 1,
                    ArraySize = 1,
                    SampleDescription = { Count = 1, Quality = 0 },
                    Usage = ResourceUsage.Staging
                };

                dest = new Texture2D(_device, desc);
                _dest = dest;

            }

            if (dcrt == null)
            {
                using (var fac = new SharpDX.Direct2D1.Factory1())
                {
                    var props = new SharpDX.Direct2D1.RenderTargetProperties();
                    props.PixelFormat = new SharpDX.Direct2D1.PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore);
                    dcrt = new SharpDX.Direct2D1.DeviceContextRenderTarget(fac, props);
                    _dcrt = dcrt;
                }
            }

            using (var surface = dest.QueryInterface<Surface>())
            {
                using (var res = Frame.QueryInterface<SharpDX.Direct3D11.Resource>())
                {
                    _device.ImmediateContext.CopyResource(res, dest);
                }

                var map = surface.Map(SharpDX.DXGI.MapFlags.Read, out DataStream ds);
                using (ds)
                {
                    var bprops = new SharpDX.Direct2D1.BitmapProperties();
                    bprops.PixelFormat = new SharpDX.Direct2D1.PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Ignore);
                    var size = new Size2(
                        _output.Description.DesktopBounds.Right - _output.Description.DesktopBounds.Left,
                        _output.Description.DesktopBounds.Bottom - _output.Description.DesktopBounds.Top
                        );

                    using (var bmp = new SharpDX.Direct2D1.Bitmap(dcrt, size, ds, map.Pitch, bprops))
                    {
                        dcrt.BindDeviceContext(hdc, new RawRectangle(0, 0, width, height));
                        dcrt.BeginDraw();
                        dcrt.DrawBitmap(bmp, new RawRectangleF(0, 0, width, height), 1, SharpDX.Direct2D1.BitmapInterpolationMode.Linear);
                        dcrt.EndDraw();
                    }
                }
            }
        }

        public void Dispose()
        {
            _duplicating = false;
            Interlocked.Exchange(ref _thread, null)?.Join((int)Math.Min(Options.FrameAcquisitionTimeout * 2L, int.MaxValue));
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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Duplicator;

namespace WinDuplicator
{
    public partial class Main : Form
    {
        private static Lazy<Icon> _embeddedIcon = new Lazy<Icon>(() => { using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(typeof(Main), "Duplicator.ico")) return new Icon(stream); });
        public static Icon EmbeddedIcon => _embeddedIcon.Value;

        private WinDuplicatorOptions _options;
        private Duplicator.Duplicator _duplicator;
        private Graphics _graphics;

        public Main()
        {
            InitializeComponent();
            MinimumSize = Size;
            Icon = EmbeddedIcon;
            _options = new WinDuplicatorOptions();
            propertyGridMain.SelectedObject = _options;
            _duplicator = new Duplicator.Duplicator(_options);
            _duplicator.FrameAcquired += OnFrameAcquired;
            _duplicator.Width = splitContainerMain.Panel1.Width;
            _duplicator.Height = splitContainerMain.Panel1.Height;

            splitContainerMain.Panel1.SizeChanged += (sender, e) =>
            {
                _duplicator.Width = splitContainerMain.Panel1.Width;
                _duplicator.Height = splitContainerMain.Panel1.Height;
            };

            splitContainerMain.Panel1.HandleCreated += (sender, e) =>
            {
                _graphics = Graphics.FromHwnd(splitContainerMain.Panel1.Handle);
                _duplicator.Hdc = _graphics.GetHdc();
            };

            splitContainerMain.Panel1.HandleDestroyed += (sender, e) =>
            {
                _duplicator.Hdc = IntPtr.Zero;
                _graphics.ReleaseHdc();
                _graphics.Dispose();
            };
        }

        private void OnFrameAcquired(object sender, CancelEventArgs e)
        {
            _duplicator.RenderFrame();
        }

        protected override void OnClosed(EventArgs e)
        {
            _duplicator?.Dispose();
        }

        private void buttonQuit_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void checkBoxDuplicate_CheckedChanged(object sender, EventArgs e)
        {
            _duplicator.IsDuplicating = checkBoxDuplicate.Checked;
        }
    }
}

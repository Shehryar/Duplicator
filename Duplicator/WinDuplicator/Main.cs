using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

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
            _duplicator.Size = new SharpDX.Size2(splitContainerMain.Panel1.Width, splitContainerMain.Panel1.Height);

            splitContainerMain.Panel1.SizeChanged += (sender, e) =>
            {
                _duplicator.Size = new SharpDX.Size2(splitContainerMain.Panel1.Width, splitContainerMain.Panel1.Height);
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
            checkBoxRecord.Enabled = _duplicator.IsDuplicating;
            if (!_duplicator.IsDuplicating)
            {
                checkBoxRecord.Checked = false;
            }
        }

        private void buttonAbout_Click(object sender, EventArgs e)
        {
            string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>().Version;
            string cr = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>().Copyright;
            MessageBox.Show(this, "Duplicator V" + version + Environment.NewLine + cr, "About Duplicator", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void checkBoxRecord_CheckedChanged(object sender, EventArgs e)
        {
            _duplicator.IsRecording = checkBoxRecord.Checked;
            if (!string.IsNullOrEmpty(_duplicator.RecordFilePath))
            {
                Text = "Duplicator - Recording " + Path.GetFileName(_duplicator.RecordFilePath);
            }
        }
    }
}

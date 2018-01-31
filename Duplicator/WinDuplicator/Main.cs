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

        public Main()
        {
            InitializeComponent();
            Icon = EmbeddedIcon;
            _options = new WinDuplicatorOptions();
            propertyGridMain.SelectedObject = _options;
            _duplicator = new Duplicator.Duplicator(_options);
            _duplicator.FrameAcquired += OnFrameAcquired;
        }

        private void OnFrameAcquired(object sender, CancelEventArgs e)
        {
            using (var g = Graphics.FromHwnd(splitContainerMain.Panel1.Handle))
            {
                var hdc = g.GetHdc();
                _duplicator.RenderFrame(hdc, splitContainerMain.Panel1.Width, splitContainerMain.Panel1.Height);
                g.ReleaseHdc(hdc);
            }
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

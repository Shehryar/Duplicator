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

        public Main()
        {
            InitializeComponent();
            MinimumSize = Size;
            Icon = EmbeddedIcon;
            _options = new WinDuplicatorOptions();
            propertyGridMain.SelectedObject = _options;

            _duplicator = new Duplicator.Duplicator(_options);
            _duplicator.PropertyChanged += OnDuplicatorPropertyChanged;
            _duplicator.Size = new SharpDX.Size2(splitContainerMain.Panel1.Width, splitContainerMain.Panel1.Height);

            splitContainerMain.Panel1.SizeChanged += (sender, e) =>
            {
                _duplicator.Size = new SharpDX.Size2(splitContainerMain.Panel1.Width, splitContainerMain.Panel1.Height);
            };

            splitContainerMain.Panel1.HandleCreated += (sender, e) =>
            {
                _duplicator.Hwnd = splitContainerMain.Panel1.Handle;
            };

            splitContainerMain.Panel1.HandleDestroyed += (sender, e) =>
            {
                _duplicator.Hwnd = IntPtr.Zero;

            };
        }

        private void OnDuplicatorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // update chekboxes accordingly with duplicator state
            // note: these events arrive on another thread
            switch (e.PropertyName)
            {
                case nameof(_duplicator.DuplicatingState):
                    BeginInvoke((Action)(() =>
                    {
                        bool dup = _duplicator.DuplicatingState == Duplicator.DuplicatorState.Started || _duplicator.DuplicatingState == Duplicator.DuplicatorState.Starting;
                        checkBoxDuplicate.Checked = dup;
                        checkBoxRecord.Enabled = dup;
                    }));
                    break;

                case nameof(_duplicator.RecordingState):
                    BeginInvoke((Action)(() =>
                    {
                        checkBoxRecord.Checked = _duplicator.RecordingState == Duplicator.DuplicatorState.Started || _duplicator.RecordingState == Duplicator.DuplicatorState.Starting;
                    }));
                    break;

                case nameof(_duplicator.RecordFilePath):
                    string mode = _duplicator.IsUsingHardwareBasedEncoder ? "Hardware" : "Software";
                    BeginInvoke((Action)(() =>
                    {
                        if (_duplicator.RecordingState == Duplicator.DuplicatorState.Started && !string.IsNullOrEmpty(_duplicator.RecordFilePath))
                        {
                            Text = "Duplicator - " + mode + " Recording " + Path.GetFileName(_duplicator.RecordFilePath);
                        }
                        else
                        {
                            Text = "Duplicator";
                        }
                    }));
                    break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _duplicator?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void buttonQuit_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void checkBoxDuplicate_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBoxDuplicate.Checked)
            {
                _duplicator.StartDuplicating();
            }
            else
            {
                _duplicator.StopDuplicating();
            }
        }

        private void buttonAbout_Click(object sender, EventArgs e)
        {
            var about = new About();
            about.ShowDialog(this);
        }

        private void checkBoxRecord_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBoxRecord.Checked)
            {
                _duplicator.StartRecording();
            }
            else
            {
                _duplicator.StopRecording();
            }
        }
    }
}

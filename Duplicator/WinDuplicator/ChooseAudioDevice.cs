using System.Linq;
using System.Windows.Forms;
using Duplicator;

namespace WinDuplicator
{
    public partial class ChooseAudioDevice : Form
    {
        public ChooseAudioDevice(string selectedAudioDevice, AudioCapture.DataFlow flow)
        {
            InitializeComponent();
            Icon = Main.EmbeddedIcon;

            listViewMain.Select();
            foreach (var device in AudioCapture.GetDevices(flow).Where(d => d.State == AudioCapture.AudioDeviceState.Active))
            {
                var item = listViewMain.Items.Add(device.FriendlyName);
                item.Tag = device;
                if (selectedAudioDevice != null && device.FriendlyName == selectedAudioDevice)
                {
                    Device = device;
                    item.Selected = true;
                }
            }

            listViewMain.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
            UpdateControls();
        }

        public AudioCapture.AudioDevice Device { get; private set; }

        private void ChooseAdapter_FormClosing(object sender, FormClosingEventArgs e)
        {
            if ((e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None) && DialogResult == DialogResult.OK)
            {
                if (listViewMain.SelectedItems.Count > 0)
                {
                    Device = (AudioCapture.AudioDevice)listViewMain.SelectedItems[0].Tag;
                }
            }
        }

        private void UpdateControls()
        {
            buttonOk.Enabled = listViewMain.SelectedItems.Count > 0;
        }
    }
}

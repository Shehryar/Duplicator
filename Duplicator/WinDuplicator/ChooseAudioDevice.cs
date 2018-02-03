using System.Windows.Forms;
using NAudio.CoreAudioApi;

namespace WinDuplicator
{
    public partial class ChooseAudioDevice : Form
    {
        public ChooseAudioDevice(string selectedAudioDevice)
        {
            InitializeComponent();
            Icon = Main.EmbeddedIcon;

            using (var enumerator = new MMDeviceEnumerator())
            {
                listViewMain.Select();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    var item = listViewMain.Items.Add(device.FriendlyName);
                    item.Tag = device;
                    if (selectedAudioDevice != null && device.FriendlyName == selectedAudioDevice)
                    {
                        Device = device;
                        item.Selected = true;
                    }
                }
            }

            listViewMain.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
            UpdateControls();
        }

        public MMDevice Device { get; private set; }

        private void ChooseAdapter_FormClosing(object sender, FormClosingEventArgs e)
        {
            if ((e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None) && DialogResult == DialogResult.OK)
            {
                if (listViewMain.SelectedItems.Count > 0)
                {
                    Device = (MMDevice)listViewMain.SelectedItems[0].Tag;
                }
            }
        }

        private void UpdateControls()
        {
            buttonOk.Enabled = listViewMain.SelectedItems.Count > 0;
        }
    }
}

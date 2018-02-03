using System.Windows.Forms;
using Duplicator;
using SharpDX.DXGI;

namespace WinDuplicator
{
    public partial class ChooseOutput : Form
    {
        public ChooseOutput(Adapter1 adapter, string selectedDeviceName)
        {
            InitializeComponent();
            Icon = Main.EmbeddedIcon;

            listViewMain.Select();
            foreach (var output in adapter.Outputs)
            {
                string name = DuplicatorOptions.GetDisplayDeviceName(output.Description.DeviceName);
                var item = listViewMain.Items.Add(name);
                item.Tag = output.Description.DeviceName;
                int width = output.Description.DesktopBounds.Right - output.Description.DesktopBounds.Left;
                int height = output.Description.DesktopBounds.Bottom - output.Description.DesktopBounds.Top;
                item.SubItems.Add(width + " x " + height);
                item.SubItems.Add(output.Description.DesktopBounds.Left + ", " + output.Description.DesktopBounds.Top);
                item.SubItems.Add(output.Description.Rotation.ToString());
                if (selectedDeviceName != null && output.Description.DeviceName == selectedDeviceName)
                {
                    DeviceName = name;
                    item.Selected = true;
                }
            }

            listViewMain.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
            UpdateControls();
            Text = "Choose Monitor for " + adapter.Description.Description;
        }

        public string DeviceName { get; private set; }

        private void ChooseOutput_FormClosing(object sender, FormClosingEventArgs e)
        {
            if ((e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None) && DialogResult == DialogResult.OK)
            {
                if (listViewMain.SelectedItems.Count > 0)
                {
                    DeviceName = (string)listViewMain.SelectedItems[0].Tag;
                }
            }
        }

        private void UpdateControls()
        {
            buttonOk.Enabled = listViewMain.SelectedItems.Count > 0;
        }

        private void listViewMain_SelectedIndexChanged(object sender, System.EventArgs e)
        {
            UpdateControls();
        }
    }
}

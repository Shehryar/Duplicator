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
        }

        public string DeviceName { get; private set; }

        private void ChooseOutput_FormClosing(object sender, FormClosingEventArgs e)
        {
            if ((e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None) && DialogResult == DialogResult.OK)
            {
                DeviceName = (string)listViewMain.SelectedItems[0].Tag;
            }
        }
    }
}

using System.Windows.Forms;
using SharpDX.DXGI;

namespace WinDuplicator
{
    public partial class ChooseAdapter : Form
    {
        public ChooseAdapter(string selectedAdapter)
        {
            InitializeComponent();
            Icon = Main.EmbeddedIcon;

            using (var fac = new Factory1())
            {
                listViewMain.Select();
                foreach (var adapter in fac.Adapters1)
                {
                    var item = listViewMain.Items.Add(adapter.Description1.Description);
                    item.Tag = adapter;
                    item.SubItems.Add(adapter.Description1.Flags.ToString());
                    item.SubItems.Add(adapter.Description1.Revision.ToString());
                    item.SubItems.Add(adapter.Description1.DedicatedVideoMemory.ToString());
                    if (selectedAdapter != null && adapter.Description.Description == selectedAdapter)
                    {
                        Adapter = adapter;
                        item.Selected = true;
                    }
                }
            }

            listViewMain.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
        }

        public Adapter1 Adapter { get; private set; }

        private void ChooseAdapter_FormClosing(object sender, FormClosingEventArgs e)
        {
            if ((e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None) && DialogResult == DialogResult.OK)
            {
                Adapter = (Adapter1)listViewMain.SelectedItems[0].Tag;
            }
        }
    }
}

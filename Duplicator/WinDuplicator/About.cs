using System;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Duplicator;

namespace WinDuplicator
{
    public partial class About : Form
    {
        public About()
        {
            InitializeComponent();
            Icon = Main.EmbeddedIcon;
            string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>().Version;
            string cr = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>().Copyright;
            labelCopyright.Text = "Duplicator V" + version + Environment.NewLine + cr;

            var sb = new StringBuilder();
            sb.AppendLine("* System:");
            sb.AppendLine();
            sb.AppendLine(" OS: " + Environment.OSVersion);
            sb.AppendLine(" Processors: " + Environment.ProcessorCount);
            sb.AppendLine();
            sb.AppendLine("* Detected H264 encoders:");
            sb.AppendLine();
            foreach (var dec in H264Encoder.Enumerate())
            {
                sb.AppendLine(" " + dec.FriendlyName);
                sb.AppendLine("  Clsid: " + dec.Clsid);
                sb.AppendLine("  Flags: " + dec.Flags);
                sb.AppendLine("  D3D11 Aware: " + dec.IsDirect3D11Aware);
                sb.AppendLine("  Supported Input types:");
                foreach (var type in dec.InputTypes)
                {
                    sb.AppendLine("   " + type);
                }
                sb.AppendLine();
            }

            textBoxInfo.Text = sb.ToString();
        }

        private void buttonOk_Click(object sender, EventArgs e)
        {
            Close();
        }
    }
}

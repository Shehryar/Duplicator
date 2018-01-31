using System;
using System.ComponentModel;
using System.Drawing.Design;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using Duplicator;
using SharpDX.DXGI;

namespace WinDuplicator
{
    public class WinDuplicatorOptions : DuplicatorOptions
    {
        [Editor(typeof(OutputEditor), typeof(UITypeEditor))]
        public override string Output { get => base.Output; set => base.Output = value; }

        [Editor(typeof(AdapterEditor), typeof(UITypeEditor))]
        public override string Adapter { get => base.Adapter; set => base.Adapter = value; }

        private class OutputEditor : UITypeEditor
        {
            public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

            public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
            {
                var editorService = (IWindowsFormsEditorService)provider.GetService(typeof(IWindowsFormsEditorService));
                if (editorService == null)
                    return base.EditValue(context, provider, value);

                var options = context.Instance as DuplicatorOptions;
                if (options == null)
                    return base.EditValue(context, provider, value);

                var adapter = options.GetAdapter();
                if (adapter == null)
                    return base.EditValue(context, provider, value);

                var form = new ChooseOutput(adapter, value as string);
                if (editorService.ShowDialog(form) == DialogResult.OK)
                    return form.DeviceName;

                return base.EditValue(context, provider, value);
            }
        }

        private class AdapterEditor : UITypeEditor
        {
            public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

            public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
            {
                var editorService = (IWindowsFormsEditorService)provider.GetService(typeof(IWindowsFormsEditorService));
                if (editorService == null)
                    return base.EditValue(context, provider, value);

                var form = new ChooseAdapter(value as string);
                if (editorService.ShowDialog(form) == DialogResult.OK)
                    return form.Adapter.Description1.Description;

                return base.EditValue(context, provider, value);
            }
        }
    }
}

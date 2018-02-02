using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.Globalization;
using System.Windows.Forms;
using System.Windows.Forms.Design;
using Duplicator;

namespace WinDuplicator
{
    public class WinDuplicatorOptions : DuplicatorOptions
    {
        [Editor(typeof(OutputEditor), typeof(UITypeEditor))]
        public override string Output { get => base.Output; set => base.Output = value; }

        [Editor(typeof(AdapterEditor), typeof(UITypeEditor))]
        public override string Adapter { get => base.Adapter; set => base.Adapter = value; }

        [TypeConverter(typeof(FrameRateConverter))]
        public override float RecordingFrameRate { get => base.RecordingFrameRate; set => base.RecordingFrameRate = value; }

        private class FrameRateConverter : TypeConverter
        {
            public override bool CanConvertFrom(ITypeDescriptorContext context, Type sourceType) => true;
            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) => float.Parse((string)value);

            public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;
            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
            {
                var list = new List<float>();
                list.Add(23.976f);
                list.Add(24f);
                list.Add(25);
                list.Add(29.97f);
                list.Add(30);
                list.Add(60);
                return new StandardValuesCollection(list);
            }
        }

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

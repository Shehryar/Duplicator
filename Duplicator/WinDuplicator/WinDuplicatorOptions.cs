using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.Globalization;
using System.Runtime.InteropServices;
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

        [Editor(typeof(RenderAudioDeviceEditor), typeof(UITypeEditor))]
        public override string SoundDevice { get => base.SoundDevice; set => base.SoundDevice = value; }

        [Editor(typeof(CaptureAudioDeviceEditor), typeof(UITypeEditor))]
        public override string MicrophoneDevice { get => base.MicrophoneDevice; set => base.MicrophoneDevice = value; }

        [TypeConverter(typeof(FrameRateConverter))]
        public override float RecordingFrameRate { get => base.RecordingFrameRate; set => base.RecordingFrameRate = value; }

        [Editor(typeof(FolderNameEditor), typeof(UITypeEditor))]
        public override string OutputDirectoryPath { get => base.OutputDirectoryPath; set => base.OutputDirectoryPath = value; }

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

        private class RenderAudioDeviceEditor : UITypeEditor
        {
            public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

            public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
            {
                var editorService = (IWindowsFormsEditorService)provider.GetService(typeof(IWindowsFormsEditorService));
                if (editorService == null)
                    return base.EditValue(context, provider, value);

                var form = new ChooseAudioDevice(value as string, AudioCapture.DataFlow.Render);
                if (editorService.ShowDialog(form) == DialogResult.OK)
                    return form.Device.FriendlyName;

                return base.EditValue(context, provider, value);
            }
        }

        private class CaptureAudioDeviceEditor : UITypeEditor
        {
            public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

            public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
            {
                var editorService = (IWindowsFormsEditorService)provider.GetService(typeof(IWindowsFormsEditorService));
                if (editorService == null)
                    return base.EditValue(context, provider, value);

                var form = new ChooseAudioDevice(value as string, AudioCapture.DataFlow.Capture);
                if (editorService.ShowDialog(form) == DialogResult.OK)
                    return form.Device.FriendlyName;

                return base.EditValue(context, provider, value);
            }
        }

        private class FolderNameEditor : UITypeEditor
        {
            public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context) => UITypeEditorEditStyle.Modal;

            public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
            {
                var browser = new FolderBrowser();
                if (value != null)
                {
                    browser.DirectoryPath = string.Format("{0}", value);
                }

                if (browser.ShowDialog(null) == DialogResult.OK)
                    return browser.DirectoryPath;

                return value;
            }
        }

        public class FolderBrowser
        {
            public string DirectoryPath { get; set; }

            public DialogResult ShowDialog(IWin32Window owner)
            {
                var hwndOwner = owner != null ? owner.Handle : GetActiveWindow();
                var dialog = (IFileOpenDialog)new FileOpenDialog();
                IShellItem item;
                if (!string.IsNullOrEmpty(DirectoryPath))
                {
                    SHCreateItemFromParsingName(DirectoryPath, IntPtr.Zero, typeof(IShellItem).GUID, out item);
                    if (item != null)
                    {
                        dialog.SetFolder(item);
                    }
                }

                dialog.SetOptions(FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM);
                int hr = dialog.Show(hwndOwner);
                if (hr == ERROR_CANCELLED)
                    return DialogResult.Cancel;

                if (hr != 0)
                    return DialogResult.Abort;

                dialog.GetResult(out item);
                item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out string path);
                DirectoryPath = path;
                return DialogResult.OK;
            }

            [DllImport("shell32")]
            private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

            [DllImport("user32")]
            private static extern IntPtr GetActiveWindow();

            private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

            [ComImport]
            [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
            private class FileOpenDialog { }

            [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IFileOpenDialog
            {
                [PreserveSig]
                int Show(IntPtr parent); // IModalWindow
                void SetFileTypes();  // not fully defined
                void SetFileTypeIndex([In] uint iFileType);
                void GetFileTypeIndex(out uint piFileType);
                void Advise(); // not fully defined
                void Unadvise();
                void SetOptions(FOS fos);
                void GetOptions(out FOS pfos);
                void SetDefaultFolder(IShellItem psi);
                void SetFolder(IShellItem psi);
                void GetFolder(out IShellItem ppsi);
                void GetCurrentSelection(out IShellItem ppsi);
                void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
                void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
                void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
                void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
                void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
                void GetResult(out IShellItem ppsi);
                void AddPlace(IShellItem psi, int alignment);
                void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
                void Close(int hr);
                void SetClientGuid();  // not fully defined
                void ClearClientData();
                void SetFilter([MarshalAs(UnmanagedType.Interface)] IntPtr pFilter);
                void GetResults([MarshalAs(UnmanagedType.Interface)] out IntPtr ppenum); // not fully defined
                void GetSelectedItems([MarshalAs(UnmanagedType.Interface)] out IntPtr ppsai); // not fully defined
            }

            [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IShellItem
            {
                void BindToHandler(); // not fully defined
                void GetParent(); // not fully defined
                void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
                void GetAttributes();  // not fully defined
                void Compare();  // not fully defined
            }

            private enum SIGDN : uint
            {
                SIGDN_FILESYSPATH = 0x80058000,
                SIGDN_NORMALDISPLAY = 0,
                SIGDN_PARENTRELATIVE = 0x80080001,
                SIGDN_PARENTRELATIVEEDITING = 0x80031001,
                SIGDN_PARENTRELATIVEFORADDRESSBAR = 0x8007c001,
                SIGDN_PARENTRELATIVEPARSING = 0x80018001,
                SIGDN_URL = 0x80068000
            }

            [Flags]
            private enum FOS
            {
                FOS_ALLNONSTORAGEITEMS = 0x80,
                FOS_ALLOWMULTISELECT = 0x200,
                FOS_CREATEPROMPT = 0x2000,
                FOS_DEFAULTNOMINIMODE = 0x20000000,
                FOS_DONTADDTORECENT = 0x2000000,
                FOS_FILEMUSTEXIST = 0x1000,
                FOS_FORCEFILESYSTEM = 0x40,
                FOS_FORCESHOWHIDDEN = 0x10000000,
                FOS_HIDEMRUPLACES = 0x20000,
                FOS_HIDEPINNEDPLACES = 0x40000,
                FOS_NOCHANGEDIR = 8,
                FOS_NODEREFERENCELINKS = 0x100000,
                FOS_NOREADONLYRETURN = 0x8000,
                FOS_NOTESTFILECREATE = 0x10000,
                FOS_NOVALIDATE = 0x100,
                FOS_OVERWRITEPROMPT = 2,
                FOS_PATHMUSTEXIST = 0x800,
                FOS_PICKFOLDERS = 0x20,
                FOS_SHAREAWARE = 0x4000,
                FOS_STRICTFILETYPES = 4
            }
        }
    }
}

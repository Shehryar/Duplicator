using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SharpDX.MediaFoundation;
using SharpDX.Multimedia;

namespace Duplicator
{
    public class H264Encoder
    {
        private H264Encoder(Activate activate)
        {
            FriendlyName = activate.Get(TransformAttributeKeys.MftFriendlyNameAttribute);
            Clsid = activate.Get(TransformAttributeKeys.MftTransformClsidAttribute);
            Flags = (TransformEnumFlag)activate.Get(TransformAttributeKeys.TransformFlagsAttribute);
            var list = new List<string>();
            var inputTypes = activate.Get(TransformAttributeKeys.MftInputTypesAttributes);
            for (int j = 0; j < inputTypes.Length; j += 32) // two guids
            {
                var majorType = new Guid(Enumerable.Range(0, 16).Select(index => Marshal.ReadByte(inputTypes, j + index)).ToArray()); // Should be video in this context
                var subType = new Guid(Enumerable.Range(0, 16).Select(index => Marshal.ReadByte(inputTypes, j + 16 + index)).ToArray());
                list.Add(GetFourCC(subType));
            }

            list.Sort();
            InputTypes = list;
            try
            {
                using (var tf = activate.ActivateObject<Transform>())
                {
                    IsBuiltin = IsBuiltinEncoder(tf);
                }
            }
            catch
            {
                // do nothing
            }
        }

        public string FriendlyName { get; }
        public Guid Clsid { get; }
        public IEnumerable<string> InputTypes { get; }
        public TransformEnumFlag Flags { get; }
        public bool IsBuiltin { get; }
        public override string ToString() => FriendlyName;

        public static bool IsBuiltinEncoder(SinkWriter writer, int streamIndex)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            IntPtr tf;
            try
            {
                writer.GetServiceForStream(streamIndex, Guid.Empty, typeof(Transform).GUID, out tf);
            }
            catch
            {
                return false;
            }
            if (tf == IntPtr.Zero)
                return false;

            return Marshal.GetObjectForIUnknown(tf) as IMFObjectInformation != null;
        }

        public static bool IsBuiltinEncoder(Transform transform)
        {
            if (transform == null)
                throw new ArgumentNullException(nameof(transform));

            return Marshal.GetObjectForIUnknown(transform.NativePointer) as IMFObjectInformation != null;
        }

        public static IEnumerable<H264Encoder> Enumerate() => Enumerate(TransformEnumFlag.All);
        public static IEnumerable<H264Encoder> Enumerate(TransformEnumFlag flags)
        {
            var output = new TRegisterTypeInformation();
            output.GuidMajorType = MediaTypeGuids.Video;
            output.GuidSubtype = VideoFormatGuids.FromFourCC(new FourCC("H264"));
            foreach (var activate in MediaFactory.FindTransform(TransformCategoryGuids.VideoEncoder, flags, null, output))
            {
                yield return new H264Encoder(activate);
            }
        }

        private static string GetFourCC(Guid guid)
        {
            var s = guid.ToString();
            if (s.EndsWith("0000-0010-8000-00aa00389b71"))
            {
                var bytes = guid.ToByteArray();
                if (bytes.Take(4).Any(b => b < 32 || b > 127))
                    return s;

                return new string(bytes.Take(4).Select(b => (char)b).ToArray());
            }

            return s;
        }

        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("CE6BE8E7-D757-435F-9DE9-BE3EF330B805")]
        private interface IMFObjectInformation { }
    }
}

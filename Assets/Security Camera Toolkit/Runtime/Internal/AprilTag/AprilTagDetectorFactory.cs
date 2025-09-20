using System;
using System.Reflection;
using System.Runtime.InteropServices;
using AprilTag;
using AprilTag.Interop;
using UnityEngine;

namespace SecurityCameraToolkit.Runtime.Internal.AprilTag
{
    public enum AprilTagFamilyType
    {
        TagStandard41h12 = 0,
        Tag36h11 = 1
    }

    static class AprilTagDetectorFactory
    {
        static readonly FieldInfo DetectorField = typeof(TagDetector).GetField("_detector", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo FamilyField = typeof(TagDetector).GetField("_family", BindingFlags.NonPublic | BindingFlags.Instance);

        public static TagDetector Create(int width, int height, int decimation, AprilTagFamilyType family)
        {
            var detector = new TagDetector(width, height, decimation);
            ConfigureFamily(detector, family);
            return detector;
        }

        public static void ConfigureFamily(TagDetector detector, AprilTagFamilyType family)
        {
            if (detector == null)
                throw new ArgumentNullException(nameof(detector));

            if (DetectorField == null || FamilyField == null)
                throw new InvalidOperationException("AprilTag TagDetector internals not found. Package layout may have changed.");

            var interopDetector = DetectorField.GetValue(detector) as Detector;
            var currentFamily = FamilyField.GetValue(detector) as Family;

            if (interopDetector == null)
                throw new InvalidOperationException("Failed to access AprilTag detector handle through reflection.");

            var newFamily = AprilTagFamilyHandleFactory.CreateFamily(family);

            if (currentFamily != null)
            {
                interopDetector.RemoveFamily(currentFamily);
                currentFamily.Dispose();
            }

            interopDetector.AddFamily(newFamily);
            FamilyField.SetValue(detector, newFamily);
        }
    }

    static class AprilTagFamilyHandleFactory
    {
        public static Family CreateFamily(AprilTagFamilyType family)
        {
            switch (family)
            {
                case AprilTagFamilyType.Tag36h11:
                    return CreateTag36h11();
                case AprilTagFamilyType.TagStandard41h12:
                    return Family.CreateTagStandard41h12();
                default:
                    throw new ArgumentOutOfRangeException(nameof(family), family, null);
            }
        }

#if UNITY_IOS && !UNITY_EDITOR
        const string DllName = "__Internal";
#else
        const string DllName = "AprilTag";
#endif

        [DllImport(DllName, EntryPoint = "tag36h11_create")]
        static extern Family _CreateTag36h11();

        static Family CreateTag36h11()
        {
            try
            {
                return _CreateTag36h11();
            }
            catch (EntryPointNotFoundException ex)
            {
                throw new NotSupportedException("AprilTag native plugin does not expose tag36h11_create. Rebuild the plugin with tag36h11 enabled or switch the calibrator to TagStandard41h12.", ex);
            }
            catch (DllNotFoundException ex)
            {
                throw new NotSupportedException("AprilTag native plugin is missing tag36h11 symbols. Rebuild the plugin or switch the calibrator to TagStandard41h12.", ex);
            }
        }
    }
}

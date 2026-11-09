using System.Runtime.InteropServices;

namespace FatzMedia.Video.Windows.Audio;

internal static partial class CoreAudioDefaultEndpointReader
{
    private static readonly Guid MMDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly PropertyKey PkeyDeviceFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static bool TryGetDefaultRenderEndpoint(out string? endpointId, out string? endpointName)
    {
        endpointId = null;
        endpointName = null;

        return TryGetDefaultRenderEndpoint(ERole.Multimedia, out endpointId, out endpointName)
            || TryGetDefaultRenderEndpoint(ERole.Console, out endpointId, out endpointName);
    }

    private static bool TryGetDefaultRenderEndpoint(ERole role, out string? endpointId, out string? endpointName)
    {
        endpointId = null;
        endpointName = null;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(MMDeviceEnumeratorClassId, throwOnError: true);
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType!)!;
            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out device);
            if (hr < 0 || device == null)
                return false;

            hr = device.GetId(out endpointId);
            if (hr < 0 || string.IsNullOrWhiteSpace(endpointId))
                return false;

            endpointName = TryGetFriendlyName(device);
            return true;
        }
        catch
        {
            endpointId = null;
            endpointName = null;
            return false;
        }
        finally
        {
            ReleaseCom(device);
            ReleaseCom(enumerator);
        }
    }

    private static string? TryGetFriendlyName(IMMDevice device)
    {
        IPropertyStore? propertyStore = null;
        PropVariant variant = default;
        try
        {
            var hr = device.OpenPropertyStore(0, out propertyStore);
            if (hr < 0 || propertyStore == null)
                return null;

            PropertyKey key = PkeyDeviceFriendlyName;
            hr = propertyStore.GetValue(ref key, out variant);
            return hr < 0 || variant.VarType != VarEnum.VT_LPWSTR || variant.Pointer == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(variant.Pointer);
        }
        catch
        {
            return null;
        }
        finally
        {
            _ = PropVariantClear(ref variant);
            ReleaseCom(propertyStore);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
            _ = Marshal.ReleaseComObject(value);
    }

    [LibraryImport("Ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant pvar);

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

#pragma warning disable SYSLIB1096 // ComImport → GeneratedComInterface is non-trivial for proven COM interop.

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(int accessMode, out IPropertyStore properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

#pragma warning restore SYSLIB1096

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, int propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly int PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public int Reserved4;

        public readonly VarEnum VarType => (VarEnum)VariantType;
    }
}

using SharpGen.Runtime;
using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FatzBomb.MediaControl.Core.Video;

internal static class D3D11ProfileEnumerator
{
    public static HashSet<Guid> EnumerateProfiles()
    {
        var profiles = new HashSet<Guid>();

        try
        {
            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;

            try
            {
                Result result = D3D11.D3D11CreateDevice(
                    adapter: null,
                    driverType: DriverType.Hardware,
                    flags: DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
                    featureLevels: [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                    device: out device,
                    featureLevel: out _,
                    immediateContext: out context);

                if (result.Failure || device == null)
                {
                    Debug.WriteLine($"[D3D11ProfileEnumerator] D3D11 device creation failed: {result}");
                    return profiles;
                }

                using ID3D11VideoDevice? videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
                if (videoDevice == null)
                {
                    Debug.WriteLine("[D3D11ProfileEnumerator] ID3D11VideoDevice not available on this device");
                    return profiles;
                }

                var count = videoDevice.VideoDecoderProfileCount;
                Debug.WriteLine($"[D3D11ProfileEnumerator] Found {count} decoder profiles");
                for (uint i = 0; i < count; i++)
                {
                    Guid profile = videoDevice.GetVideoDecoderProfile(i);
                    if (profile != Guid.Empty)
                    {
                        _ = profiles.Add(profile);
                        Debug.WriteLine($"[D3D11ProfileEnumerator]   [{i}] {profile:B}");
                    }
                }
            }
            finally
            {
                context?.Dispose();
                device?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D11ProfileEnumerator] Failed to enumerate profiles: {ex.Message}");
        }

        return profiles;
    }
}

$ErrorActionPreference = 'Stop'

# This script is CDG-name agnostic; it validates presentation fan-out, not CDG engine symbol names.
$root = Split-Path -Parent $PSScriptRoot

function Read-Text {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $path = Join-Path $root $RelativePath
    if (-not (Test-Path $path)) {
        throw "Presentation parity check failed: missing file $RelativePath"
    }

    return Get-Content -Path $path -Raw
}

function Assert-Match {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Text,

        [Parameter(Mandatory = $true)]
        [string] $Pattern,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if ($Text -notmatch $Pattern) {
        throw "Presentation parity check failed: $Message"
    }
}

$mediaSurface = Read-Text 'FatzBomb.MediaControl.Wpf/MediaSurface.cs'
$compositedSurface = Read-Text 'FatzBomb.MediaControl.Wpf/WpfCompositedSurfaceHost.cs'
$secondaryWindow = Read-Text 'FatzBomb.MediaControl.Wpf/SecondaryPresentationWindow.cs'
$sessionContract = Read-Text 'FatzBomb.MediaControl/Api/IMediaControlSession.cs'
$coordinator = Read-Text 'FatzBomb.MediaControl/Controllers/MediaPresentationCoordinator.cs'
$backendSet = Read-Text 'FatzBomb.MediaControl.Rendering.Direct3D11/Graphics/D3D/Presentation/D3D11PresentationBackendSet.cs'
$surfaceController = Read-Text 'FatzBomb.MediaControl.Rendering.Direct3D11/Graphics/D3D/Presentation/D3D11PresentationSurfaceController.cs'
$presentationServices = Read-Text 'FatzBomb.MediaControl.Rendering.Direct3D11/Graphics/D3D/Presentation/D3D11RendererPresentationServices.cs'
$compositionPresenter = Read-Text 'FatzBomb.MediaControl.Rendering.Direct3D11/Graphics/Composition/D3D11MediaCompositionPresenter.cs'
$targetRegistry = Read-Text 'FatzBomb.MediaControl/Core/Graphics/Composition/Planning/CompositionTargetRegistry.cs'

Assert-Match $sessionContract 'bool\s+AttachPresentationSurface\s*\(\s*MediaPresentationSurfaceDescriptor\s+surface\s*\)' 'IMediaControlSession must expose neutral presentation-surface attach.'
Assert-Match $sessionContract 'bool\s+ResizePresentationSurface\s*\(\s*string\s+surfaceId,\s*int\s+pixelWidth,\s*int\s+pixelHeight\s*\)' 'IMediaControlSession must expose neutral presentation-surface resize.'
Assert-Match $sessionContract 'bool\s+DetachPresentationSurface\s*\(\s*string\s+surfaceId\s*\)' 'IMediaControlSession must expose neutral presentation-surface detach.'

Assert-Match $mediaSurface 'DefaultSecondarySurfaceId\s*=\s*"secondary"' 'MediaSurface must keep a stable default secondary surface id.'
Assert-Match $mediaSurface 'new\s+SecondaryPresentationWindow\s*\(\s*(Player|session),\s*DefaultSecondarySurfaceId' 'MediaSurface must open secondary output through SecondaryPresentationWindow using the shared session.'
Assert-Match $mediaSurface 'nativeSurface\s*=\s*MediaNativeSurfaceDescriptor\.Win32Hwnd' 'Native-HWND WPF mode must create a neutral Win32 surface payload.'
Assert-Match $mediaSurface 'nativeSurface\s*=\s*host\.CreateSurfaceDescriptor\s*\(\s*\)' 'WPF-composited mode must create a neutral managed frame-sink payload.'
Assert-Match $mediaSurface 'new\s+MediaPresentationSurfaceDescriptor\s*\(\s*MediaPresentationSurfaceDescriptor\.PrimarySurfaceId\s*,\s*MediaPresentationSurfaceRole\.Primary\s*,\s*nativeSurface' 'Primary WPF modes must attach their neutral payload with the Primary role.'
Assert-Match $mediaSurface 'ResizePresentationSurface\s*\(\s*MediaPresentationSurfaceDescriptor\.PrimarySurfaceId' 'Primary WPF surface must resize through the session surface API.'
Assert-Match $mediaSurface 'DetachPresentationSurface\s*\(\s*MediaPresentationSurfaceDescriptor\.PrimarySurfaceId' 'Primary WPF surface must detach through the session surface API.'
Assert-Match $compositedSurface 'WpfCompositedSurfaceHost\s*:\s*Grid,\s*IMediaPresentationFrameSink' 'WPF-composited output must consume final renderer frames through the neutral frame-sink contract.'

Assert-Match $secondaryWindow 'MediaPresentationSurfaceRole\s+role\s*=\s*MediaPresentationSurfaceRole\.Projector' 'Secondary window must default to the Projector role.'
Assert-Match $secondaryWindow 'nativeSurface\s*=\s*MediaNativeSurfaceDescriptor\.Win32Hwnd' 'Native-HWND secondary window must create a neutral Win32 surface payload.'
Assert-Match $secondaryWindow 'nativeSurface\s*=\s*_compositedHost\.CreateSurfaceDescriptor\s*\(\s*\)' 'WPF-composited secondary window must create a neutral managed frame-sink payload (ENG-016).'
Assert-Match $secondaryWindow 'new\s+MediaPresentationSurfaceDescriptor\s*\(\s*SurfaceId\s*,\s*_role\s*,\s*nativeSurface' 'Secondary window must attach its neutral payload with its role.'
Assert-Match $secondaryWindow 'ResizePresentationSurface\s*\(\s*SurfaceId' 'Secondary window must resize through the session surface API.'
Assert-Match $secondaryWindow 'DetachPresentationSurface\s*\(\s*SurfaceId' 'Secondary window must detach through the session surface API.'

Assert-Match $coordinator 'Dictionary<string,\s*MediaPresentationSurfaceDescriptor>\s+_surfaces' 'Core coordinator must retain all attached surface descriptors in one registry.'
Assert-Match $coordinator '_surfaces\s*\[\s*surface\.Id\s*\]\s*=\s*surface' 'Core coordinator must store attached/resized surfaces by id.'
Assert-Match $coordinator '_surfaces\.Remove\s*\(\s*surfaceId,\s*out\s+MediaPresentationSurfaceDescriptor\?\s+surface\s*\)' 'Core coordinator must detach surfaces from the shared registry by id.'
Assert-Match $coordinator 'foreach\s*\(\s*MediaPresentationSurfaceDescriptor\s+surface\s+in\s+_surfaces\.Values\s*\)' 'Core coordinator must recreate all registered surfaces from the shared registry.'
Assert-Match $coordinator '_rendererBackend\.AttachSurface\s*\(\s*surface\s*\)' 'Core coordinator must route all neutral surface descriptors through the selected renderer backend.'

Assert-Match $backendSet '_secondaryNativeHwndTargets' 'D3D11 backend set must own secondary native HWND targets.'
Assert-Match $backendSet 'CreateSecondaryNativeHwndTarget' 'D3D11 backend set must create secondary native HWND targets.'
# ENG-026: the per-frame enumeration and present loops are index loops over the sorted target lists.
Assert-Match $backendSet '_secondaryNativeHwndTargets\.Values' 'D3D11 backend set must enumerate secondary targets.'
Assert-Match $backendSet 'targets\.Add\s*\(\s*new\s+D3D11PresentationBackendTarget\s*\(\s*nativeIds\[i\],\s*natives\[i\],\s*natives\[i\]\.Hwnd,\s*"NativeHwndSwapChain"' 'Secondary targets must enter the same active target snapshot list as native HWND swap-chain outputs.'
Assert-Match $backendSet 'NotifyTargetsPresented\s*\(\s*IReadOnlyList<D3D11PresentationTargetSnapshot>\s+targets' 'D3D11 backend set must notify/present a list of target snapshots.'
Assert-Match $backendSet 'D3D11PresentationTargetSnapshot\s+target\s*=\s*targets\[i\]' 'D3D11 backend set must notify each presented target.'
Assert-Match $backendSet 'NormalizeSecondaryNativeHwndTargetId' 'D3D11 backend set must normalize secondary target ids.'

Assert-Match $surfaceController 'backends\.CollectActiveTargetBackends\s*\(\s*_scratchBackends\s*\)' 'Surface controller must build snapshots from all active primary and secondary backends.'
Assert-Match $surfaceController 'snapshots\.Add\s*\(\s*new\s+D3D11PresentationTargetSnapshot' 'Surface controller must return target snapshots for compositor fan-out.'

Assert-Match $presentationServices 'Surfaces\.GetActiveTargetSnapshots\s*\(\s*applyPendingNativeResize:\s*true' 'Renderer presentation services must collect all active final targets before visible composition.'
Assert-Match $presentationServices 'MediaCompositor\.PresentToTargets' 'Visible composition must render through the multi-target compositor path.'
Assert-Match $presentationServices 'Surfaces\.NotifyTargetsPresented' 'Visible composition must notify/present all compositor-presented targets.'

Assert-Match $compositionPresenter '_targetRegistry\.Capture\s*\(\s*validTargets\.Select' 'D3D11 composition presenter must capture all valid presentation targets.'
Assert-Match $compositionPresenter 'foreach\s*\(\s*D3D11PresentationTargetSnapshot\s+target\s+in\s+presentationTargets\s*\)' 'D3D11 composition presenter must draw each selected presentation target.'
Assert-Match $targetRegistry 'Capture\s*\(\s*IEnumerable<CompositionTargetDescriptor>\s+targets\s*\)' 'Core target registry must support renderer-neutral multi-target capture.'

Write-Host 'Presentation parity check passed: WPF primary/secondary surfaces use the same neutral session contract, and Core fan-out captures/draws/presents active primary and secondary targets through one target plan.'

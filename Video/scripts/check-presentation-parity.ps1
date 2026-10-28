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
$wpfAutoHide = Read-Text 'FatzBomb.MediaControl.Wpf/MediaSurface.AutoHide.cs'
$wpfPath = Read-Text 'FatzBomb.MediaControl.Wpf/MediaSurface.PresentationPath.cs'
$avaloniaSurface = Read-Text 'FatzBomb.MediaControl.Avalonia/MediaSurface.cs'
$avaloniaSecondary = Read-Text 'FatzBomb.MediaControl.Avalonia/SecondaryPresentationWindow.cs'
$winFormsSurface = Read-Text 'FatzBomb.MediaControl.WinForms/MediaSurface.cs'
$winFormsSecondary = Read-Text 'FatzBomb.MediaControl.WinForms/SecondaryPresentationWindow.cs'
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
# Stage 046A: the WPF surface has a Role and SurfaceId like the Avalonia and WinForms surfaces. It
# attaches as Primary by default, with the id the role implies, and resizes/detaches the id it
# attached with.
Assert-Match $mediaSurface 'RoleProperty\s*=\s*DependencyProperty\.Register\s*\([^;]*new\s+PropertyMetadata\s*\(\s*MediaPresentationSurfaceRole\.Primary' 'WPF MediaSurface must default to the Primary role.'
Assert-Match $mediaSurface 'new\s+MediaPresentationSurfaceDescriptor\s*\(\s*surfaceId\s*,\s*role\s*,\s*nativeSurface' 'WPF modes must attach their neutral payload with the surface''s role and effective id.'
Assert-Match $mediaSurface 'var\s+surfaceId\s*=\s*EffectiveSurfaceId' 'WPF surface must attach with its effective surface id.'
Assert-Match $mediaSurface 'ResizePresentationSurface\s*\(\s*_attachedSurfaceId' 'WPF surface must resize through the session surface API.'
Assert-Match $mediaSurface 'DetachPresentationSurface\s*\(\s*_attachedSurfaceId' 'WPF surface must detach through the session surface API.'
Assert-Match $compositedSurface 'WpfCompositedSurfaceHost\s*:\s*Grid,\s*IMediaPresentationFrameSink' 'WPF-composited output must consume final renderer frames through the neutral frame-sink contract.'

Assert-Match $secondaryWindow 'MediaPresentationSurfaceRole\s+role\s*=\s*MediaPresentationSurfaceRole\.Projector' 'Secondary window must default to the Projector role.'
# Stage 046A: the WPF secondary window hosts a MediaSurface in its role (as Avalonia and WinForms
# do), so it attaches, resizes and detaches through the checks above and shares auto-hide.
Assert-Match $secondaryWindow 'new\s+MediaSurface\s*\{[^}]*PresentationMode\s*=\s*presentationMode[^}]*Role\s*=\s*role' 'Secondary window must host a MediaSurface in its role and the primary''s presentation mode (ENG-016).'
Assert-Match $secondaryWindow 'Surface\.Player\s*=\s*null' 'Secondary window must detach its surface when closed.'

# Stage 046A adapter parity: every adapter shares Core's content tracker (auto-hide and the
# Audio-mode clear), reports Core's presentation path, and offers the same second-screen window.
foreach ($adapter in @(
        @{ Name = 'WPF'; Surface = $wpfAutoHide + $wpfPath + $mediaSurface; Secondary = $secondaryWindow },
        @{ Name = 'Avalonia'; Surface = $avaloniaSurface; Secondary = $avaloniaSecondary },
        @{ Name = 'WinForms'; Surface = $winFormsSurface; Secondary = $winFormsSecondary })) {
    $name = $adapter.Name
    Assert-Match $adapter.Surface 'MediaSurfaceContentTracker' "$name MediaSurface must use Core's MediaSurfaceContentTracker."
    Assert-Match $adapter.Surface 'public\s+bool\s+AutoHide' "$name MediaSurface must expose AutoHide."
    Assert-Match $adapter.Surface 'event\s+EventHandler\?\s+ContentVisibilityChanged' "$name MediaSurface must expose ContentVisibilityChanged."
    Assert-Match $adapter.Surface 'public\s+bool\s+IsPresentationSuspended' "$name MediaSurface must expose IsPresentationSuspended (Audio mode)."
    Assert-Match $adapter.Surface 'public\s+MediaSurfacePresentationPath\s+PresentationPath' "$name MediaSurface must report Core's MediaSurfacePresentationPath."
    Assert-Match $adapter.Surface 'event\s+EventHandler\?\s+PresentationPathChanged' "$name MediaSurface must expose PresentationPathChanged."
    Assert-Match $adapter.Surface 'DescribeAttachRefused' "$name MediaSurface must warn when the session refuses its surface."
    Assert-Match $adapter.Surface 'public\s+MediaPresentationSurfaceRole\s+Role' "$name MediaSurface must expose Role."
    Assert-Match $adapter.Surface 'public\s+string\s+EffectiveSurfaceId' "$name MediaSurface must expose EffectiveSurfaceId."
    Assert-Match $adapter.Secondary 'public\s+bool\s+IsFullScreen' "$name SecondaryPresentationWindow must expose IsFullScreen."
    Assert-Match $adapter.Secondary 'public\s+void\s+ShowOnScreen\s*\(' "$name SecondaryPresentationWindow must offer ShowOnScreen."
    Assert-Match $adapter.Secondary 'F11' "$name SecondaryPresentationWindow must toggle full screen with F11."
}

# Stage 046A slice 2: every adapter offers the karaoke and background controls over Core's
# lightweight player controller (the rules live in Core's LightweightPlayerPolicy), the same image
# conversion helper, and a surface overlay; the WinForms surface falls back to a frame sink.
foreach ($adapter in @(
        @{ Name = 'WPF'; Folder = 'FatzBomb.MediaControl.Wpf'; Inputs = 'WpfMediaInputSources.cs'; InputMethod = 'FromImageSource' },
        @{ Name = 'Avalonia'; Folder = 'FatzBomb.MediaControl.Avalonia'; Inputs = 'AvaloniaMediaInputSources.cs'; InputMethod = 'FromBitmap' },
        @{ Name = 'WinForms'; Folder = 'FatzBomb.MediaControl.WinForms'; Inputs = 'WinFormsMediaInputSources.cs'; InputMethod = 'FromBitmap' })) {
    $name = $adapter.Name
    $karaoke = Read-Text "$($adapter.Folder)/KaraokeSurface.cs"
    $background = Read-Text "$($adapter.Folder)/BackgroundSurface.cs"
    $inputs = Read-Text "$($adapter.Folder)/$($adapter.Inputs)"
    $lightweightHost = $karaoke + $background
    if ($name -ne 'WPF') {
        $lightweightHost += Read-Text "$($adapter.Folder)/LightweightSurfaceHost.cs"
    }

    Assert-Match $karaoke 'public\s+(sealed\s+)?class\s+KaraokeSurface' "$name must offer KaraokeSurface."
    Assert-Match $background 'public\s+(sealed\s+)?class\s+BackgroundSurface' "$name must offer BackgroundSurface."
    Assert-Match $lightweightHost 'new\s+LightweightPlayerController\s*\(\s*(LightweightPlayerKind\.\w+|kind)' "$name lightweight controls must use Core's LightweightPlayerController."
    Assert-Match $karaoke 'LightweightPlayerKind\.Karaoke' "$name KaraokeSurface must use Core's karaoke rules."
    Assert-Match $background 'LightweightPlayerKind\.Background' "$name BackgroundSurface must use Core's background rules (Background mode)."
    Assert-Match $background 'public\s+(object|Control)\?\s+OverlayContent' "$name BackgroundSurface must expose OverlayContent."
    foreach ($control in @($karaoke, $background)) {
        Assert-Match $control 'public\s+bool\s+AutoPlay' "$name lightweight controls must expose AutoPlay."
        Assert-Match $control 'public\s+bool\s+AutoHide' "$name lightweight controls must expose AutoHide."
        Assert-Match $control 'event\s+EventHandler\?\s+ContentVisibilityChanged' "$name lightweight controls must expose ContentVisibilityChanged."
        Assert-Match $control 'public\s+IMediaControlPlatformFactory\?\s+PlatformFactory' "$name lightweight controls must expose PlatformFactory."
        Assert-Match $control 'public\s+IReadOnlyList<string>\?\s+NativeLibraryPaths' "$name lightweight controls must expose NativeLibraryPaths."
        Assert-Match $control 'public\s+Task<bool>\s+LoadAsync\s*\(\s*string\s+path\s*\)' "$name lightweight controls must load a path."
    }

    Assert-Match $background 'public\s+Task<bool>\s+LoadAsync\s*\(\s*MediaInputSource\s+source\s*\)' "$name BackgroundSurface must load a Core source."
    Assert-Match $inputs "public\s+static\s+(unsafe\s+)?MediaInputSource\s+$($adapter.InputMethod)\s*\(" "$name must convert its framework's image with a static $($adapter.InputMethod)."
}

Assert-Match $winFormsSurface 'sink\.CreateSurfaceDescriptor\s*\(\s*\)' 'WinForms MediaSurface must fall back to a managed frame sink.'
Assert-Match $winFormsSurface 'MediaSurfacePresentationPath\.FrameSink' 'WinForms MediaSurface must report the frame-sink path.'
Assert-Match $winFormsSurface 'public\s+Control\?\s+OverlayContent' 'WinForms MediaSurface must expose OverlayContent (like the WPF surface).'
Assert-Match $winFormsSurface 'new\s+MediaOverlayForm' 'WinForms OverlayContent must be shown in the layered overlay window.'
Assert-Match $mediaSurface 'public\s+object\?\s+OverlayContent' 'WPF MediaSurface must keep OverlayContent.'

# Stage 046A slice 3: every adapter offers a Player facade with CreateAsync, and every test player
# binds the helpers in Player.Shared (chrome rule, stats line, screen choice, renderer request,
# --players) instead of its own copy.
foreach ($adapter in @('FatzBomb.MediaControl.Wpf', 'FatzBomb.MediaControl.Avalonia', 'FatzBomb.MediaControl.WinForms')) {
    $facade = Read-Text "$adapter/Player.cs"
    Assert-Match $facade 'public\s+sealed\s+class\s+Player\s*:\s*MediaControlPlayer' "$adapter must offer a Player facade over MediaControlPlayer."
    Assert-Match $facade 'public\s+static\s+Task<Player>\s+CreateAsync\s*\([^)]*IMediaControlPlatformFactory\s+platformFactory\s*\)' "$adapter Player must offer CreateAsync with a platform factory."
}

foreach ($player in @(
        @{ Name = 'WPF'; View = 'FatzBomb.MediaControl.Player/PlayerView.xaml.cs'; Window = 'FatzBomb.MediaControl.Player/MainWindow.xaml.cs'; Renderer = 'FatzBomb.MediaControl.Player/Configuration/PlayerRendererSelection.cs' },
        @{ Name = 'Avalonia'; View = 'FatzBomb.MediaControl.Player.Avalonia/PlayerPanel.axaml.cs'; Window = 'FatzBomb.MediaControl.Player.Avalonia/MainWindow.axaml.cs'; Renderer = 'FatzBomb.MediaControl.Player.Avalonia/Renderers.cs' },
        @{ Name = 'WinForms'; View = 'FatzBomb.MediaControl.Player.WinForms/PlayerPanel.cs'; Window = 'FatzBomb.MediaControl.Player.WinForms/MainForm.cs'; Renderer = 'FatzBomb.MediaControl.Player/Configuration/PlayerRendererSelection.cs' })) {
    $name = $player.Name
    $view = Read-Text $player.View
    $window = Read-Text $player.Window
    $renderer = Read-Text $player.Renderer
    Assert-Match $view 'new\s+FullScreenChromeController\s*\(' "$name player must use the shared full-screen chrome controller."
    Assert-Match $view 'PlayerScreenChooser\.Choose\s*\(' "$name player must place its second screen with the shared screen rule."
    Assert-Match $view 'UpdateStats\s*\(' "$name player must show the shared stats line."
    Assert-Match $view 'SecondScreen' "$name player must honour --second-screen=."
    # ENG-039 R1: the rebuild, full screen and keys, the stats tick (ProcessSampler) and closing.
    Assert-Match $window 'new\s+PlayerSetController<' "$name player must drive its players with the shared PlayerSetController."
    Assert-Match $view 'IPlayerSlot' "$name player's view must be the shared controller's IPlayerSlot."
    Assert-Match $window '\.Players\b|PlayersBox' "$name player must show --players=1..4."
    # The toolbar: player count and renderer change at run time in every player (owner, 046A smoke).
    Assert-Match $window '(?i)_?playersBox' "$name player must offer the player count in its toolbar."
    Assert-Match $window '(?i)_?rendererBox' "$name player must offer the renderer in its toolbar."
    Assert-Match $window 'RebuildAsync\s*\(' "$name player must recreate its players when the toolbar changes."
    Assert-Match $renderer 'PlayerRendererRequest' "$name player must map the shared renderer request."
}

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

Write-Host 'Presentation parity check passed: WPF primary/secondary surfaces use the same neutral session contract, the WPF, Avalonia and WinForms surfaces share Core auto-hide, path reporting and the second-screen window, every adapter offers KaraokeSurface and BackgroundSurface over Core''s lightweight player controller, every adapter offers a Player facade, every test player uses the Player.Shared helpers, and Core fan-out captures/draws/presents active primary and secondary targets through one target plan.'

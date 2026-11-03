# Stage 048A (idea) - Rename to FatzMedia.Video

Owner decision 2026-11-03: the product family becomes FatzMedia, published by FatzBomb
Entertainment. This engine (today FatzBomb.MediaControl) becomes `FatzMedia.Video`, next to
`FatzMedia.Audio` (today FatzAudio); `FatzMedia` alone is the suite (Audio + Video). A player we
may sell later is an application with its own name and namespace, not a `FatzMedia.*` library.
See `docs/fatzaudio/2026-11-03_MediaControl_Reply_K4K6K8.md`.

When: a stage of its own after the 037A decision (slice 6), when the audio packages have
settled; earlier only if a package is published before then. Not mixed with other work.

| Today | New |
|---|---|
| `FatzBomb.MediaControl` (Core) | `FatzMedia.Video` |
| `.Rendering.Direct3D11` / `.Vulkan` / `.Software` | `FatzMedia.Video.Direct3D11` / `.Vulkan` / `.Software` ("Rendering." dropped) |
| `.Rendering.Text` | `FatzMedia.Video.Text` |
| `.Windows`, `.Windows.Direct3D11`, `.Linux` | `FatzMedia.Video.Windows`, `.Windows.Direct3D11`, `.Linux` |
| `.Wpf`, `.Avalonia`, `.WinForms` | `FatzMedia.Video.Wpf`, `.Avalonia`, `.WinForms` |
| `.Player`, `.Player.Avalonia`, `.Player.WinForms`, `.Player.Shared` | `FatzMedia.Video.Sample.Player` (WPF), `.Avalonia`, `.WinForms`, `.Shared` |
| `.Audio.FatzAudio` (BASS-era bridge) | Decided at 037A slice 6: folded in if FatzAudio is the only engine, or BASS moves out instead |
| Test projects | `FatzMedia.Video.Tests` and so on |

Scope:
- Project, package, assembly and namespace names as above. Public types keep their names: the
  control stays `Player` (owner: it tells developers they add a control, not an app). Type
  names that carry "MediaControl" (`MediaControlPlayer`, `MediaControlServiceProvider`,
  `IMediaControlSession`) are renamed in the same stage; names chosen then.
- FatzBomb Entertainment in Authors, Company, copyright, LICENSE and assembly metadata.
- Boundary scripts, CI, docs site, README, AGENTS.md, native folder docs.
- `FatzAudio` to `FatzMedia.Audio` in the pin and `nuget.config` source mapping, once FatzAudio
  publishes under the new ID.

# Stage 048A (idea) - Rename to FatzMedia.Video

Owner decision 2026-11-03: the product family becomes FatzMedia, published by FatzBomb
Entertainment. This engine (today FatzBomb.MediaControl) becomes `FatzMedia.Video`, next to
`FatzMedia.Audio` (today FatzAudio). `FatzMedia.Player` stays free for the player controls and
app, and `FatzMedia` is the suite (Audio + Video). See
`docs/fatzaudio/2026-11-03_MediaControl_Reply_K4K6K8.md`.

Scope when selected (a stage of its own, not mixed with 037A slices, before any package is
published):
- Project, package, assembly and namespace names: `FatzBomb.MediaControl[.X]` to
  `FatzMedia.Video[.X]`; the test players to `FatzMedia.Player.*` only if the owner wants them
  there.
- FatzBomb Entertainment in Authors, Company, copyright, LICENSE and assembly metadata.
- Boundary scripts, CI, docs site, README, AGENTS.md and native folder docs.
- `FatzAudio` to `FatzMedia.Audio` in the pin and `nuget.config` source mapping, once FatzAudio
  publishes under the new ID.

To decide first: the public type `Player` (WPF and WinForms adapters). C# finds a namespace
before an imported type, so inside any `FatzMedia.*` namespace (our own code and the future
player app) a bare `Player` would mean the `FatzMedia.Player` namespace. Either rename the
type during this stage, while names are breaking anyway, or accept qualified names in our code.
Apps in their own namespaces are not affected.

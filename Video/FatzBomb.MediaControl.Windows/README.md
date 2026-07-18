# FatzBomb.MediaControl.Windows

Windows platform implementation for `FatzBomb.MediaControl`.

This package supplies Windows-native library loading, endpoint integration,
high-resolution waiting, and FFmpeg hardware-decode policy used by WPF and other
Windows hosts. It references Core only; hosts select a renderer separately. It does
not bundle BASS or FFmpeg native libraries, which hosts provide through configured
native-library paths. Existing conventional paths are `native` and
`runtimes/win-x64/native`; callers may instead pass any existing directories in
search-priority order. See `docs/NATIVE_DEPENDENCIES.md` in the source repository.

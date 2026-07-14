# FatzBomb.MediaControl.Windows

Windows platform implementation for `FatzBomb.MediaControl`.

This package supplies the Windows-native media composition used by WPF and other
Windows hosts. It requires the Core package and does not bundle BASS or FFmpeg
native libraries; hosts provide those libraries through their configured native
library paths.

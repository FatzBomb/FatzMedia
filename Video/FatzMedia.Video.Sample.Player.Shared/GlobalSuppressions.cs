using System.Diagnostics.CodeAnalysis;

// "Shared" is a reserved word only in VB; this sample-player library is consumed from C#, and the
// project is named after its role, so renaming the namespaces would only churn every host.
[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:FatzMedia.Video.Sample.Player.Shared", Justification = "C#-only sample library.")]
[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:FatzMedia.Video.Sample.Player.Shared.Hosting", Justification = "C#-only sample library.")]
[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:FatzMedia.Video.Sample.Player.Shared.Presentation", Justification = "C#-only sample library.")]
[assembly: SuppressMessage("Naming", "CA1716", Scope = "namespace", Target = "~N:FatzMedia.Video.Sample.Player.Shared.ViewModels", Justification = "C#-only sample library.")]

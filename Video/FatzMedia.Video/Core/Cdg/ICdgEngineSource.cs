using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Core.Cdg;

/// <summary>
/// An audio engine that brings its own CD+G engine (037A slice 3: the FatzAudio engine draws CD+G
/// with FatzAudio). Without it CD+G is not shown (<c>UnavailableCdgEngine</c>).
/// </summary>
interface ICdgEngineSource
{
    /// <summary>Creates the player's CD+G engine; the player owns and disposes it.</summary>
    ICdgEngine CreateCdgEngine();
}

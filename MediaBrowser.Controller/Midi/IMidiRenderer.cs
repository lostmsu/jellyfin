using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.Midi;

/// <summary>
/// Renders MIDI files to PCM audio so that the regular ffmpeg-based playback
/// pipeline (which cannot synthesize MIDI) can serve them.
/// </summary>
public interface IMidiRenderer
{
    /// <summary>
    /// Synthesizes the MIDI file behind the media source and rewrites the source in
    /// place to point at PCM WAV audio (a named pipe streamed on the fly where
    /// supported, a rendered temporary file otherwise).
    /// </summary>
    /// <param name="mediaSource">The media source of a MIDI item.</param>
    /// <param name="startTimeTicks">The requested playback start position, used to skip synthesis of the seeked-over part.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task ApplyRenderedSourceAsync(MediaSourceInfo mediaSource, long? startTimeTicks, CancellationToken cancellationToken);
}

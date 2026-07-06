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
    /// Renders a MIDI file to a cached PCM WAV file.
    /// </summary>
    /// <param name="midiPath">The path of the MIDI file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path of the rendered WAV file.</returns>
    Task<string> RenderAsync(string midiPath, CancellationToken cancellationToken);

    /// <summary>
    /// Renders the MIDI file behind the media source and rewrites the source in place
    /// to point at the rendered PCM WAV file.
    /// </summary>
    /// <param name="mediaSource">The media source of a MIDI item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task ApplyRenderedSourceAsync(MediaSourceInfo mediaSource, CancellationToken cancellationToken);
}

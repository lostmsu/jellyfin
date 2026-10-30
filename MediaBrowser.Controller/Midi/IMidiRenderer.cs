using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.Midi;

/// <summary>
/// Renders MIDI files to cached audio so clients can play them.
/// </summary>
public interface IMidiRenderer
{
    /// <summary>
    /// Synthesizes the MIDI file behind the media source and rewrites the source in
    /// place to point at its cached Opus rendering.
    /// </summary>
    /// <param name="mediaSource">The media source of a MIDI item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task ApplyRenderedSourceAsync(MediaSourceInfo mediaSource, CancellationToken cancellationToken);
}

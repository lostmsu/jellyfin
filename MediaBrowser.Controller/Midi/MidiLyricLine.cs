using System;

namespace MediaBrowser.Controller.Midi;

/// <summary>
/// A single line of lyrics extracted from a MIDI (karaoke) file.
/// </summary>
/// <param name="Start">The playback time at which the line starts.</param>
/// <param name="Text">The text of the line.</param>
public readonly record struct MidiLyricLine(TimeSpan Start, string Text);

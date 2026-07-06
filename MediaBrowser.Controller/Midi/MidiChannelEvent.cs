namespace MediaBrowser.Controller.Midi;

/// <summary>
/// A timed MIDI channel voice message (note, controller, program change, pitch bend, ...).
/// </summary>
/// <param name="TimeMicroseconds">The absolute playback time of the event in microseconds.</param>
/// <param name="Status">The status byte (command in the high nibble, channel in the low nibble).</param>
/// <param name="Data1">The first data byte.</param>
/// <param name="Data2">The second data byte, or zero for two-byte messages.</param>
public readonly record struct MidiChannelEvent(long TimeMicroseconds, byte Status, byte Data1, byte Data2);

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MediaBrowser.Controller.Midi;

/// <summary>
/// The information extracted from a MIDI file by <see cref="MidiFileParser"/>.
/// </summary>
public class MidiFileInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MidiFileInfo"/> class.
    /// </summary>
    /// <param name="duration">The playback duration.</param>
    /// <param name="title">The title, if the file contains one.</param>
    /// <param name="lyricLines">The embedded lyric lines, ordered by start time.</param>
    public MidiFileInfo(TimeSpan duration, string? title, IReadOnlyList<MidiLyricLine> lyricLines)
    {
        Duration = duration;
        Title = title;
        LyricLines = lyricLines;
    }

    /// <summary>
    /// Gets the playback duration of the file.
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// Gets the title of the sequence, taken from a karaoke <c>@T</c> tag or the sequence name, if present.
    /// </summary>
    public string? Title { get; }

    /// <summary>
    /// Gets the embedded (karaoke) lyric lines, ordered by start time.
    /// </summary>
    public IReadOnlyList<MidiLyricLine> LyricLines { get; }

    /// <summary>
    /// Gets a value indicating whether the file contains timestamped lyrics.
    /// </summary>
    public bool HasLyrics => LyricLines.Count > 0;

    /// <summary>
    /// Builds an LRC document from the embedded lyrics.
    /// </summary>
    /// <returns>The LRC text, or <c>null</c> when the file contains no lyrics.</returns>
    public string? BuildLrc()
    {
        if (LyricLines.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var line in LyricLines)
        {
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "[{0:00}:{1:00}.{2:00}]{3}",
                (int)line.Start.TotalMinutes,
                line.Start.Seconds,
                line.Start.Milliseconds / 10,
                line.Text)
                .AppendLine();
        }

        return sb.ToString();
    }
}

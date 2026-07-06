using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UtfUnknown;

namespace MediaBrowser.Controller.Midi;

/// <summary>
/// Minimal Standard MIDI File (SMF) parser extracting the information Jellyfin needs at
/// library scan time: the playback duration, an optional title and any embedded
/// (karaoke) lyrics with their timestamps.
/// </summary>
/// <remarks>
/// The parser is intentionally lenient: real-world <c>.kar</c> files from the 90s are
/// frequently truncated or slightly malformed. Anything that can be salvaged is used;
/// only a missing header is fatal.
/// </remarks>
public static class MidiFileParser
{
    /// <summary>
    /// The file extensions handled by this parser.
    /// </summary>
    public static readonly string[] MidiFileExtensions = [".mid", ".midi", ".kar"];

    // Microseconds per quarter note when no tempo event is present (120 BPM).
    private const int DefaultTempo = 500_000;
    private const int MaxFileSize = 16 * 1024 * 1024;

    static MidiFileParser()
    {
        // Karaoke files predate Unicode and use regional code pages; make those
        // resolvable regardless of how the host process was started.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Checks whether the path points to a MIDI file, based on its extension.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns><c>true</c> if the file is a MIDI file.</returns>
    public static bool IsMidiFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return MidiFileExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Parses a MIDI file.
    /// </summary>
    /// <param name="path">The path of the file to parse.</param>
    /// <returns>The extracted <see cref="MidiFileInfo"/>.</returns>
    public static MidiFileInfo Parse(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream);
    }

    /// <summary>
    /// Parses a MIDI file.
    /// </summary>
    /// <param name="stream">A stream positioned at the start of the file.</param>
    /// <returns>The extracted <see cref="MidiFileInfo"/>.</returns>
    /// <exception cref="InvalidDataException">The data is not a MIDI file.</exception>
    public static MidiFileInfo Parse(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory, 81920);
        if (memory.Length > MaxFileSize)
        {
            throw new InvalidDataException("File is too large to be a MIDI file");
        }

        var data = memory.GetBuffer().AsSpan(0, (int)memory.Length).ToArray();

        // Locate the header chunk. Searching instead of requiring it at offset 0 also
        // covers RIFF-wrapped files (RMID) and files with junk prepended.
        var headerPos = IndexOf(data, "MThd"u8);
        if (headerPos < 0 || headerPos + 14 > data.Length)
        {
            throw new InvalidDataException("MThd chunk not found; not a MIDI file");
        }

        var headerLength = ReadUInt32(data, headerPos + 4);
        var ntrks = ReadUInt16(data, headerPos + 10);
        var division = ReadUInt16(data, headerPos + 12);

        var tempoChanges = new List<(long Tick, int MicrosecondsPerQuarter)>();
        var textEvents = new List<(long Tick, int Track, byte[] Payload)>();
        var lyricEvents = new List<(long Tick, int Track, byte[] Payload)>();
        string? sequenceName = null;
        long maxTick = 0;

        var pos = headerPos + 8 + (int)Math.Min(headerLength, int.MaxValue);
        var trackIndex = 0;
        while (pos + 8 <= data.Length && trackIndex < Math.Max((int)ntrks, 1024))
        {
            var chunkLength = (int)Math.Min(ReadUInt32(data, pos + 4), int.MaxValue);
            var chunkStart = pos + 8;
            var chunkEnd = (int)Math.Min((long)chunkStart + chunkLength, data.Length);
            var isTrack = data.AsSpan(pos, 4).SequenceEqual("MTrk"u8);
            pos = chunkEnd;
            if (!isTrack)
            {
                continue;
            }

            var trackTicks = ParseTrack(
                data,
                chunkStart,
                chunkEnd,
                trackIndex,
                tempoChanges,
                textEvents,
                lyricEvents,
                ref sequenceName);
            maxTick = Math.Max(maxTick, trackTicks);
            trackIndex++;
        }

        if (trackIndex == 0)
        {
            throw new InvalidDataException("MIDI file contains no track chunks");
        }

        var tempoMap = BuildTempoMap(tempoChanges, division);
        var duration = tempoMap.TickToTime(maxTick);

        string? title = null;
        var lines = BuildLyricLines(lyricEvents.Count > 0 ? lyricEvents : SelectBestTextTrack(textEvents), tempoMap, ref title);
        title ??= string.IsNullOrWhiteSpace(sequenceName) ? null : sequenceName.Trim();

        return new MidiFileInfo(duration, title, lines);
    }

    private static long ParseTrack(
        byte[] data,
        int start,
        int end,
        int trackIndex,
        List<(long Tick, int MicrosecondsPerQuarter)> tempoChanges,
        List<(long Tick, int Track, byte[] Payload)> textEvents,
        List<(long Tick, int Track, byte[] Payload)> lyricEvents,
        ref string? sequenceName)
    {
        var pos = start;
        long tick = 0;
        byte runningStatus = 0;

        while (pos < end)
        {
            if (!TryReadVariableLength(data, ref pos, end, out var delta))
            {
                break;
            }

            tick += delta;

            if (pos >= end)
            {
                break;
            }

            var b = data[pos++];
            if (b == 0xFF)
            {
                // Meta event.
                runningStatus = 0;
                if (pos >= end)
                {
                    break;
                }

                var metaType = data[pos++];
                if (!TryReadVariableLength(data, ref pos, end, out var length) || pos + length > end)
                {
                    break;
                }

                switch (metaType)
                {
                    case 0x2F: // End of track
                        return tick;
                    case 0x51 when length >= 3: // Set tempo
                        tempoChanges.Add((tick, (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2]));
                        break;
                    case 0x01: // Text event (used by .kar karaoke files)
                        textEvents.Add((tick, trackIndex, data.AsSpan(pos, (int)length).ToArray()));
                        break;
                    case 0x05: // Lyric event
                        lyricEvents.Add((tick, trackIndex, data.AsSpan(pos, (int)length).ToArray()));
                        break;
                    case 0x03 when trackIndex == 0 && sequenceName is null: // Sequence/track name
                        sequenceName = DecodeText([data.AsSpan(pos, (int)length).ToArray()]).FirstOrDefault();
                        break;
                }

                pos += (int)length;
            }
            else if (b == 0xF0 || b == 0xF7)
            {
                // SysEx event.
                runningStatus = 0;
                if (!TryReadVariableLength(data, ref pos, end, out var length) || pos + length > end)
                {
                    break;
                }

                pos += (int)length;
            }
            else
            {
                // Channel message, possibly using running status.
                byte status;
                var remainingDataBytes = 0;
                if (b >= 0x80)
                {
                    status = b;
                }
                else
                {
                    if (runningStatus == 0)
                    {
                        // Malformed: data byte without a status byte. Salvage what we have.
                        break;
                    }

                    status = runningStatus;
                    remainingDataBytes = -1; // b already consumed the first data byte
                }

                runningStatus = status;
                remainingDataBytes += (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                pos += remainingDataBytes;
                if (pos > end)
                {
                    break;
                }
            }
        }

        return tick;
    }

    private static IReadOnlyList<MidiLyricLine> BuildLyricLines(
        List<(long Tick, int Track, byte[] Payload)> events,
        TempoMap tempoMap,
        ref string? title)
    {
        if (events.Count == 0)
        {
            return [];
        }

        events.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        var texts = DecodeText(events.Select(e => e.Payload));

        var lines = new List<MidiLyricLine>();
        var current = new StringBuilder();
        long currentStartTick = -1;

        void Flush()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0 && currentStartTick >= 0)
            {
                lines.Add(new MidiLyricLine(tempoMap.TickToTime(currentStartTick), text));
            }

            current.Clear();
            currentStartTick = -1;
        }

        for (var i = 0; i < events.Count; i++)
        {
            var text = texts[i].AsSpan();
            if (text.IsEmpty)
            {
                continue;
            }

            if (text[0] == '@')
            {
                // Karaoke metadata tag; the first @T tag is the song title.
                if (title is null && text.Length > 2 && (text[1] == 'T' || text[1] == 't'))
                {
                    title = text[2..].Trim().ToString();
                }

                continue;
            }

            // Karaoke line ('/') and paragraph ('\') markers start a new line.
            while (!text.IsEmpty && (text[0] == '/' || text[0] == '\\'))
            {
                Flush();
                text = text[1..];
            }

            // Some files separate lines with literal CR/LF characters instead.
            var first = true;
            foreach (var segment in text.ToString().Split('\r', '\n'))
            {
                if (!first)
                {
                    Flush();
                }

                first = false;
                if (segment.Length == 0)
                {
                    continue;
                }

                if (currentStartTick < 0)
                {
                    currentStartTick = events[i].Tick;
                }

                current.Append(segment);
            }
        }

        Flush();
        return lines;
    }

    /// <summary>
    /// Picks the track that most likely carries the lyrics: the one with the most text events.
    /// This skips tracks that only carry copyright notices or sequencer banners.
    /// </summary>
    private static List<(long Tick, int Track, byte[] Payload)> SelectBestTextTrack(
        List<(long Tick, int Track, byte[] Payload)> textEvents)
    {
        if (textEvents.Count == 0)
        {
            return textEvents;
        }

        var bestTrack = textEvents.GroupBy(e => e.Track)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
        return textEvents.Where(e => e.Track == bestTrack).ToList();
    }

    /// <summary>
    /// Decodes text payloads. Karaoke files predate Unicode and commonly use regional
    /// single-byte code pages (windows-1251/1252, ...), so the encoding is detected from
    /// the combined payload bytes.
    /// </summary>
    private static IReadOnlyList<string> DecodeText(IEnumerable<byte[]> payloads)
    {
        var list = payloads.ToList();
        var combined = new byte[list.Sum(p => p.Length)];
        var offset = 0;
        foreach (var payload in list)
        {
            payload.CopyTo(combined, offset);
            offset += payload.Length;
        }

        var encoding = DetectEncoding(combined);
        return list.ConvertAll(p => encoding.GetString(p));
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        try
        {
            var detected = CharsetDetector.DetectFromBytes(bytes).Detected;
            if (detected?.Encoding is not null && detected.Confidence > 0.5f)
            {
                return detected.Encoding;
            }
        }
        catch (Exception)
        {
            // Fall through to the UTF-8 attempt below.
        }

        try
        {
            new UTF8Encoding(false, true).GetString(bytes);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1;
        }
    }

    private static TempoMap BuildTempoMap(List<(long Tick, int MicrosecondsPerQuarter)> tempoChanges, ushort division)
    {
        if ((division & 0x8000) != 0)
        {
            // SMPTE time division: ticks map to wall-clock time directly.
            var framesPerSecond = -(sbyte)(division >> 8) switch
            {
                29 => 29.97,
                var fps => fps
            };
            var ticksPerFrame = division & 0xFF;
            return TempoMap.Smpte(framesPerSecond * Math.Max(ticksPerFrame, 1));
        }

        var ticksPerQuarter = division & 0x7FFF;
        return TempoMap.Metrical(Math.Max(ticksPerQuarter, 1), tempoChanges);
    }

    private static bool TryReadVariableLength(byte[] data, ref int pos, int end, out uint value)
    {
        value = 0;
        for (var i = 0; i < 4; i++)
        {
            if (pos >= end)
            {
                return false;
            }

            var b = data[pos++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static uint ReadUInt32(byte[] data, int pos)
        => ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16) | ((uint)data[pos + 2] << 8) | data[pos + 3];

    private static ushort ReadUInt16(byte[] data, int pos)
        => (ushort)((data[pos] << 8) | data[pos + 1]);

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> needle)
        => data.AsSpan().IndexOf(needle);

    /// <summary>
    /// Converts MIDI ticks to wall-clock time, honoring all tempo changes in the file.
    /// </summary>
    private sealed class TempoMap
    {
        // Metrical mode: segments of (startTick, cumulative microseconds, tempo).
        private (long Tick, double Microseconds, int Tempo)[] _segments = [];
        private int _ticksPerQuarter = 1;

        // SMPTE mode: fixed number of ticks per second.
        private double _ticksPerSecond;

        private TempoMap()
        {
        }

        public static TempoMap Smpte(double ticksPerSecond)
            => new() { _ticksPerSecond = ticksPerSecond };

        public static TempoMap Metrical(int ticksPerQuarter, List<(long Tick, int MicrosecondsPerQuarter)> tempoChanges)
        {
            var map = new TempoMap { _ticksPerQuarter = ticksPerQuarter };

            tempoChanges.Sort((a, b) => a.Tick.CompareTo(b.Tick));
            var segments = new List<(long Tick, double Microseconds, int Tempo)> { (0, 0, DefaultTempo) };
            foreach (var (tick, tempo) in tempoChanges)
            {
                if (tempo <= 0)
                {
                    continue;
                }

                var last = segments[^1];
                var microseconds = last.Microseconds + ((double)(tick - last.Tick) * last.Tempo / ticksPerQuarter);
                if (tick == last.Tick)
                {
                    segments[^1] = (tick, last.Microseconds, tempo);
                }
                else
                {
                    segments.Add((tick, microseconds, tempo));
                }
            }

            map._segments = segments.ToArray();
            return map;
        }

        public TimeSpan TickToTime(long tick)
        {
            if (tick <= 0)
            {
                return TimeSpan.Zero;
            }

            if (_ticksPerSecond > 0)
            {
                return TimeSpan.FromSeconds(tick / _ticksPerSecond);
            }

            var segment = _segments[0];
            foreach (var candidate in _segments)
            {
                if (candidate.Tick > tick)
                {
                    break;
                }

                segment = candidate;
            }

            var microseconds = segment.Microseconds + ((double)(tick - segment.Tick) * segment.Tempo / _ticksPerQuarter);
            return TimeSpan.FromMicroseconds(microseconds);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Text;
using MediaBrowser.Controller.Midi;
using Xunit;

namespace Jellyfin.Controller.Tests.Midi;

public class MidiFileParserTests
{
    private const int TicksPerQuarter = 480;

    static MidiFileParserTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Theory]
    [InlineData("song.mid", true)]
    [InlineData("song.MID", true)]
    [InlineData("song.midi", true)]
    [InlineData("song.kar", true)]
    [InlineData("/some/dir/song.KAR", true)]
    [InlineData("song.mp3", false)]
    [InlineData("song", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMidiFile_MatchesByExtension(string? path, bool expected)
    {
        Assert.Equal(expected, MidiFileParser.IsMidiFile(path));
    }

    [Fact]
    public void Parse_DefaultTempo_ComputesDuration()
    {
        // 960 ticks at 480 ticks per quarter and the default 500000 µs tempo = 1 second.
        var file = BuildMidi(TicksPerQuarter, Track(
            Event(0, 0x90, 0x3C, 0x64),
            Event(960, 0x80, 0x3C, 0x00)));

        var info = MidiFileParser.Parse(new MemoryStream(file));

        Assert.Equal(1.0, info.Duration.TotalSeconds, 3);
        Assert.False(info.HasLyrics);
        Assert.Null(info.BuildLrc());
    }

    [Fact]
    public void Parse_TempoChange_IsHonored()
    {
        // 480 ticks at 500000 µs/quarter (0.5s), then 480 ticks at 250000 µs/quarter (0.25s).
        var file = BuildMidi(TicksPerQuarter, Track(
            Meta(0, 0x51, 0x07, 0xA1, 0x20),
            Event(0, 0x90, 0x3C, 0x64),
            Meta(480, 0x51, 0x03, 0xD0, 0x90),
            Event(480, 0x80, 0x3C, 0x00)));

        var info = MidiFileParser.Parse(new MemoryStream(file));

        Assert.Equal(0.75, info.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Parse_MultipleTracks_UsesLongest()
    {
        var file = BuildMidi(
            TicksPerQuarter,
            Track(Event(480, 0x90, 0x3C, 0x64)),
            Track(Event(1920, 0x90, 0x40, 0x64)));

        var info = MidiFileParser.Parse(new MemoryStream(file));

        // 1920 ticks = 4 quarters = 2 seconds at the default tempo.
        Assert.Equal(2.0, info.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Parse_SmpteDivision_ComputesDuration()
    {
        // -25 fps, 40 ticks per frame: 1000 ticks per second.
        var division = (ushort)((0xE7 << 8) | 40);
        var file = BuildMidi(division, Track(Event(2000, 0x90, 0x3C, 0x64)));

        var info = MidiFileParser.Parse(new MemoryStream(file));

        Assert.Equal(2.0, info.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Parse_RunningStatus_IsSupported()
    {
        var file = BuildMidi(TicksPerQuarter, Track(
            Event(0, 0x90, 0x3C, 0x64),
            // Running status: two more note events without a status byte.
            Event(480, 0x3E, 0x64),
            Event(480, 0x3E, 0x00)));

        var info = MidiFileParser.Parse(new MemoryStream(file));

        Assert.Equal(1.0, info.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Parse_KaraokeTextEvents_BuildsLinesAndTitle()
    {
        var file = BuildMidi(
            TicksPerQuarter,
            Track(TextEvent(0, 0x03, "Words")),
            Track(
                TextEvent(0, 0x01, "@KMIDI KARAOKE FILE"),
                TextEvent(0, 0x01, "@TBrain Freeze"),
                TextEvent(0, 0x01, "@Tsome artist"),
                TextEvent(480, 0x01, "/First "),
                TextEvent(240, 0x01, "line"),
                TextEvent(240, 0x01, "\\Second "),
                TextEvent(240, 0x01, "line")));

        var info = MidiFileParser.Parse(new MemoryStream(file));

        Assert.Equal("Brain Freeze", info.Title);
        Assert.Equal(2, info.LyricLines.Count);
        Assert.Equal("First line", info.LyricLines[0].Text);
        Assert.Equal(0.5, info.LyricLines[0].Start.TotalSeconds, 3);
        Assert.Equal("Second line", info.LyricLines[1].Text);
        Assert.Equal(1.0, info.LyricLines[1].Start.TotalSeconds, 3);

        var lrc = info.BuildLrc();
        Assert.NotNull(lrc);
        var lines = lrc.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("[00:00.50]First line", lines[0]);
        Assert.Equal("[00:01.00]Second line", lines[1]);
    }

    [Fact]
    public void Parse_LyricEvents_TakePriorityOverTextEvents()
    {
        var file = BuildMidi(TicksPerQuarter, Track(
            TextEvent(0, 0x01, "/text event junk"),
            TextEvent(0, 0x05, "Lyric "),
            TextEvent(480, 0x05, "line\n")));

        var info = MidiFileParser.Parse(new MemoryStream(file));

        var line = Assert.Single(info.LyricLines);
        Assert.Equal("Lyric line", line.Text);
    }

    [Fact]
    public void Parse_TextEvents_PicksTrackWithMostEvents()
    {
        var file = BuildMidi(
            TicksPerQuarter,
            Track(TextEvent(0, 0x01, "Copyright 1997 Nobody")),
            Track(
                TextEvent(0, 0x01, "/Real "),
                TextEvent(120, 0x01, "ly"),
                TextEvent(120, 0x01, "rics")));

        var info = MidiFileParser.Parse(new MemoryStream(file));

        var line = Assert.Single(info.LyricLines);
        Assert.Equal("Real lyrics", line.Text);
    }

    [Fact]
    public void Parse_Windows1251Lyrics_AreDecoded()
    {
        var encoding = Encoding.GetEncoding(1251);
        var russian = new[]
        {
            "/Что сравнится с музыкой ветра",
            "/Когда она играет в проводах",
            "/Дорога дальняя лежит на север",
            "/Сквозь года и через города"
        };
        var events = russian.SelectMany((line, i) => new[] { TextEvent(i == 0 ? 0 : 480, 0x01, line, encoding) }).ToArray();
        var file = BuildMidi(TicksPerQuarter, Track(events));

        var info = MidiFileParser.Parse(new MemoryStream(file));

        Assert.Equal(4, info.LyricLines.Count);
        Assert.Equal("Что сравнится с музыкой ветра", info.LyricLines[0].Text);
    }

    [Fact]
    public void Parse_NotAMidiFile_Throws()
    {
        var garbage = Encoding.ASCII.GetBytes("ID3 this is definitely not a midi file at all");

        Assert.Throws<InvalidDataException>(() => MidiFileParser.Parse(new MemoryStream(garbage)));
    }

    [Fact]
    public void Parse_TruncatedTrack_StillReturnsParsedData()
    {
        var complete = BuildMidi(TicksPerQuarter, Track(
            Event(0, 0x90, 0x3C, 0x64),
            Event(960, 0x80, 0x3C, 0x00)));
        var truncated = complete.AsSpan(0, complete.Length - 3).ToArray();

        var info = MidiFileParser.Parse(new MemoryStream(truncated));

        Assert.True(info.Duration >= TimeSpan.Zero);
    }

    private static byte[] BuildMidi(ushort division, params byte[][] tracks)
    {
        using var stream = new MemoryStream();
        stream.Write("MThd"u8);
        WriteUInt32(stream, 6);
        WriteUInt16(stream, 1); // format
        WriteUInt16(stream, (ushort)tracks.Length);
        WriteUInt16(stream, division);

        foreach (var track in tracks)
        {
            stream.Write("MTrk"u8);
            WriteUInt32(stream, (uint)track.Length);
            stream.Write(track);
        }

        return stream.ToArray();
    }

    private static byte[] Track(params byte[][] events)
    {
        using var stream = new MemoryStream();
        foreach (var e in events)
        {
            stream.Write(e);
        }

        // End of track.
        stream.Write(Meta(0, 0x2F));
        return stream.ToArray();
    }

    private static byte[] Event(int delta, params byte[] message)
    {
        using var stream = new MemoryStream();
        WriteVariableLength(stream, delta);
        stream.Write(message);
        return stream.ToArray();
    }

    private static byte[] Meta(int delta, byte type, params byte[] payload)
    {
        using var stream = new MemoryStream();
        WriteVariableLength(stream, delta);
        stream.WriteByte(0xFF);
        stream.WriteByte(type);
        WriteVariableLength(stream, payload.Length);
        stream.Write(payload);
        return stream.ToArray();
    }

    private static byte[] TextEvent(int delta, byte type, string text, Encoding? encoding = null)
        => Meta(delta, type, (encoding ?? Encoding.UTF8).GetBytes(text));

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteVariableLength(Stream stream, int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var bytes = new byte[4];
        var count = 0;
        do
        {
            bytes[count++] = (byte)(value & 0x7F);
            value >>= 7;
        }
        while (value > 0);

        for (var i = count - 1; i > 0; i--)
        {
            stream.WriteByte((byte)(bytes[i] | 0x80));
        }

        stream.WriteByte(bytes[0]);
    }
}

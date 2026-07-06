using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Midi;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MeltySynth;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace MediaBrowser.MediaEncoding.Midi;

/// <summary>
/// Renders MIDI files to PCM WAV using the managed MeltySynth synthesizer, so the
/// ffmpeg-based playback pipeline can serve them like any other audio.
/// On Linux the audio is synthesized on the fly into a named pipe: playback starts
/// immediately and nothing is stored. Seeking is satisfied by fast-forwarding the
/// MIDI event state to the seek position and zero-filling the skipped part of the
/// pipe, which ffmpeg's own input seeking then discards.
/// </summary>
public sealed partial class MidiRenderer : IMidiRenderer, IDisposable
{
    private const int SampleRate = 44100;
    private const int ChannelCount = 2;
    private const int BytesPerSample = 2;
    private const int BytesPerFrame = ChannelCount * BytesPerSample;
    private const int WavHeaderSize = 44;
    private const double TailSeconds = 2.0;
    private const double MaxDurationSeconds = 4 * 60 * 60;
    private const int ChunkFrames = 4096;

    // TimGM6mb by Tim Brechbill (GPL-2.0, distributed with MuseScore 1.x) — a small
    // General MIDI soundfont used when the system provides none.
    private const string SoundFontDownloadUrl = "https://github.com/craffel/pretty-midi/raw/main/pretty_midi/TimGM6mb.sf2";
    private const string SoundFontDownloadSha256 = "82475b91a76de15cb28a104707d3247ba932e228bada3f47bba63c6b31aaf7a1";
    private const string SoundFontDownloadFileName = "TimGM6mb.sf2";

    private const int O_WRONLY = 0x1;
    private const int O_NONBLOCK = 0x800;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int EINTR = 4;
    private const int ENXIO = 6;

    private static readonly string[] _systemSoundFontDirectories =
    [
        "/run/current-system/sw/share/soundfonts", // NixOS
        "/usr/share/soundfonts", // Arch, Fedora
        "/usr/share/sounds/sf2", // Debian, Ubuntu
        "/usr/local/share/soundfonts"
    ];

    private static readonly string[] _soundFontExtensions = ["*.sf2", "*.SF2"];

    private readonly ILogger<MidiRenderer> _logger;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly SemaphoreSlim _soundFontLock = new(1, 1);
    private (string Path, long ModifiedTicks, SoundFont SoundFont)? _cachedSoundFont;

    private int _staleFifoSweepDone;

    /// <summary>
    /// Initializes a new instance of the <see cref="MidiRenderer"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public MidiRenderer(
        ILogger<MidiRenderer> logger,
        IServerConfigurationManager serverConfigurationManager,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _serverConfigurationManager = serverConfigurationManager;
        _httpClientFactory = httpClientFactory;
    }

    private string StreamDirectory => Path.Combine(_serverConfigurationManager.ApplicationPaths.CachePath, "midi-stream");

    private string DownloadedSoundFontPath => Path.Combine(_serverConfigurationManager.ApplicationPaths.DataPath, "soundfonts", SoundFontDownloadFileName);

    /// <inheritdoc />
    public void Dispose()
    {
        _soundFontLock.Dispose();
    }

    /// <inheritdoc />
    public async Task ApplyRenderedSourceAsync(MediaSourceInfo mediaSource, long? startTimeTicks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);

        var midiPath = mediaSource.Path;
        if (!File.Exists(midiPath))
        {
            throw new FileNotFoundException("MIDI file not found", midiPath);
        }

        var options = _serverConfigurationManager.GetEncodingOptions();
        var gain = GetGain(options);
        var soundFontPath = await ResolveSoundFontPathAsync(options, cancellationToken).ConfigureAwait(false);
        var soundFontModified = File.GetLastWriteTimeUtc(soundFontPath).Ticks;
        var soundFont = await GetSoundFontAsync(soundFontPath, soundFontModified, cancellationToken).ConfigureAwait(false);

        var midi = MidiFileParser.Parse(midiPath, collectChannelEvents: true);
        var totalSeconds = Math.Min(midi.Duration.TotalSeconds + TailSeconds, MaxDurationSeconds);
        var totalSamples = (long)(totalSeconds * SampleRate);
        var totalBytes = WavHeaderSize + (totalSamples * BytesPerFrame);

        var skipMicros = Math.Clamp((startTimeTicks ?? 0) / 10, 0, (long)(totalSeconds * 1_000_000));
        var skipSamples = skipMicros * SampleRate / 1_000_000;

        EnsureStreamDirectory();

        string outputPath;
        if (OperatingSystem.IsLinux())
        {
            outputPath = Path.Combine(StreamDirectory, Guid.NewGuid().ToString("N") + ".fifo");
            if (MkFifo(outputPath, 0x180 /* 0600 */) != 0)
            {
                throw new IOException($"mkfifo failed with errno {Marshal.GetLastPInvokeError()} for {outputPath}");
            }

            _ = Task.Run(() => StreamToFifoAsync(outputPath, midiPath, midi, soundFont, gain, totalSamples, skipSamples, skipMicros), CancellationToken.None);
        }
        else
        {
            // No named pipes: render the whole file up front. The file is transient
            // and aged out by the cache cleanup task.
            outputPath = Path.Combine(StreamDirectory, Guid.NewGuid().ToString("N") + ".wav");
            var stopwatch = Stopwatch.StartNew();
            await Task.Run(
                () =>
                {
                    using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    Synthesize(output, midi, soundFont, gain, totalSamples, 0, 0, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Rendered MIDI {Path} ({Seconds:F1}s of audio) in {Elapsed:F1}s", midiPath, totalSeconds, stopwatch.Elapsed.TotalSeconds);
        }

        mediaSource.Path = outputPath;
        mediaSource.Container = "wav";
        mediaSource.Size = totalBytes;
        mediaSource.Bitrate = SampleRate * BytesPerFrame * 8;
        mediaSource.RunTimeTicks = TimeSpan.FromSeconds(totalSeconds).Ticks;

        var audioStream = mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        if (audioStream is not null)
        {
            audioStream.Codec = "pcm_s16le";
            audioStream.Channels = ChannelCount;
            audioStream.SampleRate = SampleRate;
            audioStream.BitDepth = BytesPerSample * 8;
            audioStream.BitRate = mediaSource.Bitrate;
        }
    }

    /// <summary>
    /// Synthesizes into the named pipe as ffmpeg consumes it. Runs detached from the
    /// request: it ends when the whole stream has been written, when the reader goes
    /// away (broken pipe), or when no reader shows up at all.
    /// </summary>
    private async Task StreamToFifoAsync(string fifoPath, string midiPath, MidiFileInfo midi, SoundFont soundFont, double gain, long totalSamples, long skipSamples, long skipMicros)
    {
        try
        {
            using var handle = await OpenFifoForWriteAsync(fifoPath, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (handle is null)
            {
                _logger.LogDebug("No reader opened the MIDI stream pipe for {Path}; abandoning", midiPath);
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            using var output = new FileStream(handle, FileAccess.Write);
            Synthesize(output, midi, soundFont, gain, totalSamples, skipSamples, skipMicros, CancellationToken.None);
            _logger.LogInformation(
                "Streamed MIDI {Path} ({Seconds:F1}s of audio, {Skip:F1}s skipped) in {Elapsed:F1}s",
                midiPath,
                (double)(totalSamples - skipSamples) / SampleRate,
                (double)skipSamples / SampleRate,
                stopwatch.Elapsed.TotalSeconds);
        }
        catch (IOException ex)
        {
            // Broken pipe: the client stopped or seeked, and ffmpeg went away.
            _logger.LogDebug(ex, "MIDI stream for {Path} ended early", midiPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stream MIDI file {Path}", midiPath);
        }
        finally
        {
            TryDelete(fifoPath);
        }
    }

    /// <summary>
    /// Writes a WAV stream: header, zero-fill for the skipped prefix, then live
    /// synthesis from the seek position. The MIDI channel state (programs,
    /// controllers, pitch bends) is fast-forwarded across the skipped part, and
    /// notes still sounding at the seek position are re-triggered.
    /// </summary>
    private static void Synthesize(Stream output, MidiFileInfo midi, SoundFont soundFont, double gain, long totalSamples, long skipSamples, long skipMicros, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(output);
        WriteWavHeader(writer, totalSamples);

        if (skipSamples > 0)
        {
            var zeros = new byte[256 * 1024];
            var remaining = skipSamples * BytesPerFrame;
            while (remaining > 0)
            {
                var n = (int)Math.Min(zeros.Length, remaining);
                writer.Write(zeros, 0, n);
                remaining -= n;
            }
        }

        var synthesizer = new Synthesizer(soundFont, SampleRate)
        {
            MasterVolume = (float)gain
        };

        var events = midi.ChannelEvents;
        var index = 0;

        // Fast-forward the channel state across the skipped part.
        if (skipMicros > 0)
        {
            var activeNotes = new Dictionary<int, byte>();
            for (; index < events.Count && events[index].TimeMicroseconds < skipMicros; index++)
            {
                var e = events[index];
                var command = e.Status & 0xF0;
                var channel = e.Status & 0x0F;
                if (command == 0x90 && e.Data2 > 0)
                {
                    activeNotes[(channel << 8) | e.Data1] = e.Data2;
                }
                else if (command == 0x80 || (command == 0x90 && e.Data2 == 0))
                {
                    activeNotes.Remove((channel << 8) | e.Data1);
                }
                else
                {
                    synthesizer.ProcessMidiMessage(channel, command, e.Data1, e.Data2);
                }
            }

            foreach (var (key, velocity) in activeNotes)
            {
                synthesizer.NoteOn(key >> 8, key & 0xFF, velocity);
            }
        }

        var left = new float[ChunkFrames];
        var right = new float[ChunkFrames];
        var interleaved = new byte[ChunkFrames * BytesPerFrame];

        var cursor = skipSamples;
        while (cursor < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long nextBoundary;
            if (index < events.Count)
            {
                var eventSample = events[index].TimeMicroseconds * SampleRate / 1_000_000;
                if (eventSample <= cursor)
                {
                    var e = events[index++];
                    synthesizer.ProcessMidiMessage(e.Status & 0x0F, e.Status & 0xF0, e.Data1, e.Data2);
                    continue;
                }

                nextBoundary = Math.Min(eventSample, totalSamples);
            }
            else
            {
                nextBoundary = totalSamples;
            }

            var frames = (int)Math.Min(ChunkFrames, nextBoundary - cursor);
            synthesizer.Render(left.AsSpan(0, frames), right.AsSpan(0, frames));

            var offset = 0;
            for (var i = 0; i < frames; i++)
            {
                WriteSample(interleaved, ref offset, left[i]);
                WriteSample(interleaved, ref offset, right[i]);
            }

            writer.Write(interleaved, 0, offset);
            cursor += frames;
        }
    }

    private static void WriteSample(byte[] buffer, ref int offset, float value)
    {
        var sample = (short)Math.Clamp((int)(value * 32767f), short.MinValue, short.MaxValue);
        buffer[offset++] = (byte)sample;
        buffer[offset++] = (byte)(sample >> 8);
    }

    private static void WriteWavHeader(BinaryWriter writer, long totalSamples)
    {
        var dataBytes = totalSamples * BytesPerFrame;

        writer.Write("RIFF"u8);
        writer.Write((uint)(36 + dataBytes));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1); // PCM
        writer.Write((ushort)ChannelCount);
        writer.Write((uint)SampleRate);
        writer.Write((uint)(SampleRate * BytesPerFrame));
        writer.Write((ushort)BytesPerFrame);
        writer.Write((ushort)(BytesPerSample * 8));
        writer.Write("data"u8);
        writer.Write((uint)dataBytes);
    }

    private static double GetGain(EncodingOptions options)
    {
        var gain = options.MidiSynthesizerGain;
        return gain > 0 ? Math.Min(gain, 2.0) : 0.5;
    }

    /// <summary>
    /// Opens the write end of a FIFO without blocking forever: a non-blocking open
    /// fails with ENXIO until a reader (ffmpeg) opens the other end, so poll until
    /// it succeeds or it becomes clear that no reader is coming.
    /// </summary>
    private static async Task<SafeFileHandle?> OpenFifoForWriteAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var fd = OpenFile(path, O_WRONLY | O_NONBLOCK);
            if (fd >= 0)
            {
                var flags = Fcntl(fd, F_GETFL, 0);
                if (flags >= 0)
                {
                    _ = Fcntl(fd, F_SETFL, flags & ~O_NONBLOCK);
                }

                return new SafeFileHandle(fd, ownsHandle: true);
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno != ENXIO && errno != EINTR)
            {
                return null;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return null;
    }

    private void EnsureStreamDirectory()
    {
        Directory.CreateDirectory(StreamDirectory);

        if (Interlocked.Exchange(ref _staleFifoSweepDone, 1) == 0)
        {
            // Pipes from a previous server process can never gain a reader again.
            foreach (var stale in Directory.EnumerateFiles(StreamDirectory, "*.fifo"))
            {
                TryDelete(stale);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file: {FileName}.", path);
        }
    }

    private async Task<SoundFont> GetSoundFontAsync(string path, long modifiedTicks, CancellationToken cancellationToken)
    {
        var cached = _cachedSoundFont;
        if (cached is not null && string.Equals(cached.Value.Path, path, StringComparison.Ordinal) && cached.Value.ModifiedTicks == modifiedTicks)
        {
            return cached.Value.SoundFont;
        }

        await _soundFontLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _cachedSoundFont;
            if (cached is not null && string.Equals(cached.Value.Path, path, StringComparison.Ordinal) && cached.Value.ModifiedTicks == modifiedTicks)
            {
                return cached.Value.SoundFont;
            }

            _logger.LogInformation("Loading soundfont {Path}", path);
            var soundFont = await Task.Run(() => new SoundFont(path), cancellationToken).ConfigureAwait(false);
            _cachedSoundFont = (path, modifiedTicks, soundFont);
            return soundFont;
        }
        finally
        {
            _soundFontLock.Release();
        }
    }

    /// <summary>
    /// Finds a usable SoundFont: the configured path first, then the
    /// <c>JELLYFIN_MIDI_SOUNDFONT</c> environment variable, then well-known system
    /// directories, and finally a small General MIDI soundfont downloaded on demand.
    /// </summary>
    private async Task<string> ResolveSoundFontPathAsync(EncodingOptions options, CancellationToken cancellationToken)
    {
        var configured = options.MidiSoundFontPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return configured;
            }

            _logger.LogError("The configured MIDI soundfont does not exist: {Path}. Fix or clear MidiSoundFontPath in encoding.xml.", configured);
            throw new InvalidOperationException($"The configured MIDI soundfont does not exist: {configured}");
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("JELLYFIN_MIDI_SOUNDFONT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        foreach (var directory in _systemSoundFontDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var found = _soundFontExtensions
                .SelectMany(pattern => Directory.EnumerateFiles(directory, pattern))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        if (File.Exists(DownloadedSoundFontPath))
        {
            return DownloadedSoundFontPath;
        }

        if (!options.EnableMidiSoundFontDownload)
        {
            _logger.LogError("No SoundFont found for MIDI rendering and downloading one is disabled. Install a soundfont package, or set MidiSoundFontPath in encoding.xml.");
            throw new InvalidOperationException(
                "No SoundFont found for MIDI rendering. Install one (for example the fluid or TimGM6mb soundfont packages), "
                + "set MidiSoundFontPath in encoding.xml, or enable EnableMidiSoundFontDownload.");
        }

        return await DownloadSoundFontAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadSoundFontAsync(CancellationToken cancellationToken)
    {
        await _soundFontLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(DownloadedSoundFontPath))
            {
                return DownloadedSoundFontPath;
            }

            _logger.LogInformation("No system soundfont found, downloading {FileName} from {Url}", SoundFontDownloadFileName, SoundFontDownloadUrl);

            var directory = Path.GetDirectoryName(DownloadedSoundFontPath)!;
            Directory.CreateDirectory(directory);
            var tempPath = DownloadedSoundFontPath + ".tmp";

            try
            {
                var client = _httpClientFactory.CreateClient(NamedClient.Default);
                using (var response = await client.GetAsync(SoundFontDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using (fileStream.ConfigureAwait(false))
                    {
                        await response.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                    }
                }

                string actualHash;
                var readStream = File.OpenRead(tempPath);
                await using (readStream.ConfigureAwait(false))
                {
                    actualHash = Convert.ToHexString(await SHA256.HashDataAsync(readStream, cancellationToken).ConfigureAwait(false));
                }

                if (!string.Equals(actualHash, SoundFontDownloadSha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("The checksum of the downloaded soundfont ({Checksum}) did not match the expected value ({ExpectedChecksum}).", actualHash, SoundFontDownloadSha256);
                    throw new InvalidDataException($"The checksum of the downloaded soundfont ({actualHash}) did not match the expected value ({SoundFontDownloadSha256}).");
                }

                File.Move(tempPath, DownloadedSoundFontPath, true);
                return DownloadedSoundFontPath;
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
        finally
        {
            _soundFontLock.Release();
        }
    }

    [LibraryImport("libc", EntryPoint = "mkfifo", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MkFifo(string pathname, uint mode);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenFile(string pathname, int flags);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int fd, int cmd, int arg);
}

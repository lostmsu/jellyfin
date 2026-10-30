using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Midi;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MeltySynth;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Midi;

/// <summary>
/// Synthesizes MIDI with the managed MeltySynth synthesizer and encodes the result
/// as a cached 64 kbps Opus file. Clients that support Opus can direct play the
/// cache; Jellyfin can transcode it normally for other clients.
/// </summary>
public sealed class MidiRenderer : IMidiRenderer, IDisposable
{
    private const int SampleRate = 44100;
    private const int OpusSampleRate = 48000;
    private const int OpusBitrate = 64000;
    private const int OpusCacheVersion = 1;
    private const int ChannelCount = 2;
    private const int BytesPerSample = 2;
    private const int BytesPerFrame = ChannelCount * BytesPerSample;
    private const double TailSeconds = 2.0;
    private const double MaxDurationSeconds = 4 * 60 * 60;
    private const int ChunkFrames = 4096;

    // TimGM6mb by Tim Brechbill (GPL-2.0, distributed with MuseScore 1.x) — a small
    // General MIDI soundfont used when the system provides none.
    private const string SoundFontDownloadUrl = "https://github.com/craffel/pretty-midi/raw/main/pretty_midi/TimGM6mb.sf2";
    private const string SoundFontDownloadSha256 = "82475b91a76de15cb28a104707d3247ba932e228bada3f47bba63c6b31aaf7a1";
    private const string SoundFontDownloadFileName = "TimGM6mb.sf2";

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
    private readonly IMediaEncoder _mediaEncoder;

    private readonly AsyncKeyedLocker<string> _renderLocks = new(o =>
    {
        o.PoolSize = 20;
        o.PoolInitialFill = 1;
    });
    private readonly SemaphoreSlim _soundFontLock = new(1, 1);
    private (string Path, long ModifiedTicks, SoundFont SoundFont)? _cachedSoundFont;

    /// <summary>
    /// Initializes a new instance of the <see cref="MidiRenderer"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="mediaEncoder">The configured Jellyfin FFmpeg encoder.</param>
    public MidiRenderer(
        ILogger<MidiRenderer> logger,
        IServerConfigurationManager serverConfigurationManager,
        IHttpClientFactory httpClientFactory,
        IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _serverConfigurationManager = serverConfigurationManager;
        _httpClientFactory = httpClientFactory;
        _mediaEncoder = mediaEncoder;
    }

    private string RenderCachePath => Path.Combine(_serverConfigurationManager.ApplicationPaths.CachePath, "midi-render");

    private string DownloadedSoundFontPath => Path.Combine(_serverConfigurationManager.ApplicationPaths.DataPath, "soundfonts", SoundFontDownloadFileName);

    /// <inheritdoc />
    public void Dispose()
    {
        _renderLocks.Dispose();
        _soundFontLock.Dispose();
    }

    /// <inheritdoc />
    public async Task ApplyRenderedSourceAsync(MediaSourceInfo mediaSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaSource);

        var midiPath = mediaSource.Path;
        var outputPath = await RenderAsync(midiPath, cancellationToken).ConfigureAwait(false);
        var outputSize = new FileInfo(outputPath).Length;
        var sourceDuration = mediaSource.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(mediaSource.RunTimeTicks.Value)
            : MidiFileParser.Parse(midiPath).Duration;
        var totalSeconds = Math.Min(sourceDuration.TotalSeconds + TailSeconds, MaxDurationSeconds);

        mediaSource.Path = outputPath;
        mediaSource.Container = "opus";
        mediaSource.Size = outputSize;
        mediaSource.Bitrate = OpusBitrate;
        mediaSource.RunTimeTicks = TimeSpan.FromSeconds(totalSeconds).Ticks;
        mediaSource.IsSynthesizedMidi = true;
        mediaSource.SupportsTranscoding = true;

        var audioStream = mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        if (audioStream is not null)
        {
            audioStream.Codec = "opus";
            audioStream.Channels = ChannelCount;
            audioStream.SampleRate = OpusSampleRate;
            audioStream.BitDepth = null;
            audioStream.BitRate = OpusBitrate;
        }
    }

    /// <summary>
    /// Gets or creates the cached Opus rendering for a MIDI file.
    /// </summary>
    private async Task<string> RenderAsync(string midiPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(midiPath);

        var midiFile = new FileInfo(midiPath);
        if (!midiFile.Exists)
        {
            throw new FileNotFoundException("MIDI file not found", midiPath);
        }

        var options = _serverConfigurationManager.GetEncodingOptions();
        var gain = GetGain(options);
        var soundFontPath = await ResolveSoundFontPathAsync(options, cancellationToken).ConfigureAwait(false);
        var soundFontModified = File.GetLastWriteTimeUtc(soundFontPath).Ticks;
        var cacheKey = ComputeCacheKey(midiFile, soundFontPath, soundFontModified, gain);
        var outputPath = Path.Combine(RenderCachePath, cacheKey + ".opus");

        if (IsValidOpus(outputPath))
        {
            TouchCacheFile(outputPath);
            return outputPath;
        }

        using (await _renderLocks.LockAsync(cacheKey, cancellationToken).ConfigureAwait(false))
        {
            if (IsValidOpus(outputPath))
            {
                TouchCacheFile(outputPath);
                return outputPath;
            }

            var soundFont = await GetSoundFontAsync(soundFontPath, soundFontModified, cancellationToken).ConfigureAwait(false);
            var midi = MidiFileParser.Parse(midiPath, collectChannelEvents: true);
            var totalSeconds = Math.Min(midi.Duration.TotalSeconds + TailSeconds, MaxDurationSeconds);
            var totalSamples = (long)(totalSeconds * SampleRate);

            Directory.CreateDirectory(RenderCachePath);
            var tempPath = outputPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";

            try
            {
                var stopwatch = Stopwatch.StartNew();
                await RenderToOpusAsync(tempPath, midi, soundFont, gain, totalSamples, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, outputPath, true);
                _logger.LogInformation(
                    "Rendered and cached MIDI {Path} as 64 kbps Opus ({Seconds:F1}s, {Bytes} bytes) in {Elapsed:F1}s using soundfont {SoundFont}",
                    midiPath,
                    totalSeconds,
                    new FileInfo(outputPath).Length,
                    stopwatch.Elapsed.TotalSeconds,
                    soundFontPath);
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                _logger.LogError(ex, "Failed to render MIDI file {Path} to Opus", midiPath);
                throw;
            }
        }

        return outputPath;
    }

    private async Task RenderToOpusAsync(
        string outputPath,
        MidiFileInfo midi,
        SoundFont soundFont,
        double gain,
        long totalSamples,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(_mediaEncoder.EncoderPath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _mediaEncoder.EncoderPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            }
        };

        string[] arguments =
        [
            "-hide_banner",
            "-loglevel", "error",
            "-f", "wav",
            "-i", "pipe:0",
            "-map_metadata", "-1",
            "-vn",
            "-c:a", "libopus",
            "-b:a", OpusBitrate.ToString(CultureInfo.InvariantCulture),
            "-vbr", "on",
            "-application", "audio",
            "-ar", OpusSampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "opus",
            "-y",
            outputPath
        ];

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start FFmpeg for MIDI Opus rendering.");
        }

        var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await Task.Run(
                () => Synthesize(process.StandardInput.BaseStream, midi, soundFont, gain, totalSamples, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode} while rendering MIDI to Opus: {error}");
        }
    }

    /// <summary>
    /// Writes a complete PCM WAV stream into FFmpeg's standard input.
    /// </summary>
    private static void Synthesize(Stream output, MidiFileInfo midi, SoundFont soundFont, double gain, long totalSamples, CancellationToken cancellationToken)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        WriteWavHeader(writer, totalSamples);

        var synthesizer = new Synthesizer(soundFont, SampleRate)
        {
            MasterVolume = (float)gain
        };

        var events = midi.ChannelEvents;
        var index = 0;

        var left = new float[ChunkFrames];
        var right = new float[ChunkFrames];
        var interleaved = new byte[ChunkFrames * BytesPerFrame];

        var cursor = 0L;
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

    private static string ComputeCacheKey(FileInfo midiFile, string soundFontPath, long soundFontModified, double gain)
    {
        var key = string.Join(
            '|',
            OpusCacheVersion.ToString(CultureInfo.InvariantCulture),
            midiFile.FullName,
            midiFile.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            midiFile.Length.ToString(CultureInfo.InvariantCulture),
            soundFontPath,
            soundFontModified.ToString(CultureInfo.InvariantCulture),
            gain.ToString(CultureInfo.InvariantCulture),
            SampleRate.ToString(CultureInfo.InvariantCulture),
            OpusBitrate.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40].ToLowerInvariant();
    }

    private static bool IsValidOpus(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 64)
        {
            return false;
        }

        Span<byte> magic = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(magic) == magic.Length && magic.SequenceEqual("OggS"u8);
    }

    private void TouchCacheFile(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update MIDI cache timestamp for {Path}", path);
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

}

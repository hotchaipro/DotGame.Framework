// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace DotGame.Framework.ContentPipeline.Tasks;

// _ProbeMediaDurations – reads each input item's audio file and copies its
// duration (seconds, invariant-culture double, to the microsecond), sample
// rate (Hz, integer), and channel count (integer) onto the `_DurationSeconds`,
// `_SampleRate`, and `_Channels` metadata of the output item: the build's own
// values, which the manifest step copies into the content metadata as
// DurationSeconds, SampleRate and Channels.
// Failures are surfaced as MSBuild warnings and the offending item's
// metadata is left blank, so a missing or corrupt file cannot fail
// the build.
//
// The file is read in process (AudioFileReader, below), not by ffprobe,
// which DotGame doesn't ship: headers only, plus the last Ogg page for an
// Ogg file's length.  It knows the formats the pipeline writes: Ogg Vorbis,
// Ogg Opus, M4A (AAC), WAV and FLAC.  All three values are baked into the
// generated `ContentManifest.g.cs` so the runtime loader can construct a
// `NativeAudioDecoder` entirely from manifest metadata, skipping the
// extractor probe at load time entirely — important on Android where
// OGG duration scans are multi-second operations.
//
// By default the probe reads each item's `FullPath` (the source file).
// Callers that encode content first can set `ProbeFileMetadata` to the
// name of a metadata that points at the produced output file (e.g.
// `_ProbeFile`) so the baked metadata describes the ACTUAL encoded
// artifact.  This matters because the music encoder normalises the
// sample rate and (historically) the channel count, so probing the
// source could bake values that disagree with the encoded `.ogg` — a
// mono source whose manifest claims 1 channel while the encoded file is
// stereo makes the platform decoder misinterpret interleaved samples.
//
// Results are memoised in a `CacheFile` keyed by path + last-write time,
// so an incremental build reads only new or modified tracks.
//
// IMPORTANT: the task rewrites `CacheFile` with ONLY the entries for the
// `InputItems` of the current invocation (it prunes deleted sources, see
// the carry-forward pass below), so each distinct set of items MUST use
// its own `CacheFile`.  Two callers sharing one file would each evict the
// other's entries on every build, so both would see a 100% cache miss.
public sealed class _ProbeMediaDurations : Task
{
    [Required]
    public ITaskItem[] InputItems { get; set; }

    public string CacheFile { get; set; }

    public string ProbeFileMetadata { get; set; }

    [Output]
    public ITaskItem[] OutputItems { get; set; }

    public override bool Execute()
    {
        this.OutputItems = new ITaskItem[this.InputItems.Length];

        // Which metadata supplies the file path to probe. Defaults to
        // "FullPath" (the source item). Callers that encode content first
        // can pass the name of a metadata pointing at the produced output
        // file (e.g. "_ProbeFile") so the baked SampleRate/Channels/
        // DurationSeconds describe the ACTUAL encoded artifact rather than
        // the source, which may differ when the encoder resamples or
        // changes channel count (e.g. mono source upmixed to stereo).
        var probeMeta = string.IsNullOrEmpty(this.ProbeFileMetadata) ? "FullPath" : this.ProbeFileMetadata;

        // Cache entry: tab-separated "ticks\tdurationSeconds\tsampleRate\tchannels\tsourcePath"
        // (5 fields). Older 3-field entries (ticks\tduration\tsource) are ignored so a clean
        // re-probe happens once when upgrading from the previous cache format.
        var cache = new Dictionary<string, (long Ticks, string Duration, string SampleRate, string Channels)>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(this.CacheFile) && File.Exists(this.CacheFile))
        {
            try
            {
                foreach (var line in File.ReadAllLines(this.CacheFile))
                {
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }

                    var parts = line.Split(new[] { '\t' }, 5);
                    if (parts.Length != 5)
                    {
                        continue;
                    }

                    long cachedTicks;
                    if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out cachedTicks))
                    {
                        continue;
                    }

                    cache[parts[4]] = (cachedTicks, parts[1], parts[2], parts[3]);
                }
            }
            catch (Exception ex)
            {
                this.Log.LogMessage(MessageImportance.High, "Ignoring unreadable media probe cache '{0}': {1}", this.CacheFile, ex.Message);
                cache.Clear();
            }
        }

        // First pass: satisfy items from the cache and collect the ones that
        // still need a probe (new or modified sources).
        var sources = new string[this.InputItems.Length];
        var writeTicks = new long[this.InputItems.Length];
        var probeIndices = new List<int>();
        for (int i = 0; i < this.InputItems.Length; i++)
        {
            var item = new TaskItem(this.InputItems[i]);
            this.OutputItems[i] = item;
            var source = item.GetMetadata(probeMeta);
            sources[i] = source;
            if (string.IsNullOrEmpty(source))
            {
                this.Log.LogWarning("Skipping media probe for '{0}': no {1} metadata.", item.ItemSpec, probeMeta);
                continue;
            }
            long ticks = 0;
            try { if (File.Exists(source)) { ticks = File.GetLastWriteTimeUtc(source).Ticks; } } catch { }
            writeTicks[i] = ticks;
            (long Ticks, string Duration, string SampleRate, string Channels) cached;
            if (ticks != 0 && cache.TryGetValue(source, out cached) && cached.Ticks == ticks
                && !string.IsNullOrEmpty(cached.Duration) && !string.IsNullOrEmpty(cached.SampleRate) && !string.IsNullOrEmpty(cached.Channels))
            {
                item.SetMetadata("_DurationSeconds", cached.Duration);
                item.SetMetadata("_SampleRate", cached.SampleRate);
                item.SetMetadata("_Channels", cached.Channels);
            }
            else
            {
                probeIndices.Add(i);
            }
        }

        // Carry forward still-valid cached entries for sources present this
        // run so the rewritten cache prunes deletions automatically.
        var newCache = new SortedDictionary<string, (long Ticks, string Duration, string SampleRate, string Channels)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < this.InputItems.Length; i++)
        {
            if (string.IsNullOrEmpty(sources[i]) || writeTicks[i] == 0)
            {
                continue;
            }

            var d = this.OutputItems[i].GetMetadata("_DurationSeconds");
            var sr = this.OutputItems[i].GetMetadata("_SampleRate");
            var ch = this.OutputItems[i].GetMetadata("_Channels");
            if (!string.IsNullOrEmpty(d) && !string.IsNullOrEmpty(sr) && !string.IsNullOrEmpty(ch))
            {
                newCache[sources[i]] = (writeTicks[i], d, sr, ch);
            }
        }

        // Second pass: read only the new/changed tracks
        for (int p = 0; p < probeIndices.Count; p++)
        {
            int i = probeIndices[p];
            var item = this.OutputItems[i];
            var source = sources[i];
            AudioFileInfo info;
            try
            {
                info = AudioFileReader.Read(source);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                this.Log.LogWarning("Couldn't read the duration and format of '{0}': {1}", source, ex.Message);
                continue;
            }

            var secondsStr = Math.Round(info.DurationSeconds, 6).ToString("R", CultureInfo.InvariantCulture);
            var sampleRateStr = info.SampleRate.ToString(CultureInfo.InvariantCulture);
            var channelsStr = info.Channels.ToString(CultureInfo.InvariantCulture);

            item.SetMetadata("_DurationSeconds", secondsStr);
            item.SetMetadata("_SampleRate", sampleRateStr);
            item.SetMetadata("_Channels", channelsStr);

            if (writeTicks[i] != 0)
            {
                newCache[source] = (writeTicks[i], secondsStr, sampleRateStr, channelsStr);
            }

            this.Log.LogMessage(MessageImportance.Normal, "[{0}/{1}] {2} ({3}s, {4}Hz, {5}ch)",
              p + 1, probeIndices.Count,
              item.GetMetadata("Filename") + item.GetMetadata("Extension"),
              info.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture),
              sampleRateStr, channelsStr);
        }

        // Persist the refreshed cache only when a probe actually occurred.
        if (!string.IsNullOrEmpty(this.CacheFile) && probeIndices.Count > 0)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kvp in newCache)
                {
                    sb.Append(kvp.Value.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(kvp.Value.Duration).Append('\t')
                      .Append(kvp.Value.SampleRate).Append('\t')
                      .Append(kvp.Value.Channels).Append('\t')
                      .Append(kvp.Key).Append('\n');
                }
                var content = sb.ToString();
                var existing = File.Exists(this.CacheFile) ? File.ReadAllText(this.CacheFile) : null;
                if (!string.Equals(existing, content, StringComparison.Ordinal))
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(this.CacheFile));
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(this.CacheFile, content, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                this.Log.LogMessage(MessageImportance.High, "Could not write media probe cache '{0}': {1}", this.CacheFile, ex.Message);
            }
        }

        return !this.Log.HasLoggedErrors;
    }
}

internal struct AudioFileInfo
{
    public double DurationSeconds;
    public int SampleRate;
    public int Channels;
}

// The duration, sample rate and channels of the audio files the pipeline writes, from their
// headers: WAV, FLAC, Ogg (Vorbis or Opus), MP4 (AAC in .m4a) and CAF (Opus on iOS).  Throws InvalidDataException
// for anything else.  NOTE: The pipeline's own outputs only: one audio stream, no chained Ogg.
internal static class AudioFileReader
{
    public static AudioFileInfo Read(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            byte[] magic = new byte[12];
            if (ReadFully(stream, magic, 0, 12) < 12)
            {
                throw new InvalidDataException("the file is too short for audio");
            }
            stream.Position = 0;
            if (Ascii(magic, 0, 4) == "RIFF" && Ascii(magic, 8, 4) == "WAVE")
            {
                return ReadWav(stream);
            }

            if (Ascii(magic, 0, 4) == "fLaC")
            {
                return ReadFlac(stream);
            }

            if (Ascii(magic, 0, 4) == "OggS")
            {
                return ReadOgg(stream);
            }

            if (Ascii(magic, 4, 4) == "ftyp")
            {
                return ReadMp4(stream);
            }

            if (Ascii(magic, 0, 4) == "caff")
            {
                return ReadCaf(stream);
            }

            throw new InvalidDataException("not WAV, FLAC, Ogg, MP4 or CAF");
        }
    }

    // A Core Audio Format file (iOS's Opus): the format from its desc chunk, the length from its
    // packet table's valid frames
    private static AudioFileInfo ReadCaf(Stream stream)
    {
        stream.Position = 8;
        byte[] header = new byte[12];
        double sampleRate = 0;
        int channels = 0;
        long frames = -1;
        while (ReadFully(stream, header, 0, 12) == 12)
        {
            string type = Ascii(header, 0, 4);
            long size = (long)BigUInt64(header, 4);
            long next = stream.Position + size;
            if (type == "desc" && size >= 32)
            {
                byte[] desc = ReadExactly(stream, 32);
                sampleRate = BitConverter.Int64BitsToDouble((long)BigUInt64(desc, 0));
                channels = (int)BigUInt32(desc, 24);
            }
            else if (type == "pakt" && size >= 24)
            {
                byte[] pakt = ReadExactly(stream, 24);
                frames = (long)BigUInt64(pakt, 8);
            }
            else if (type == "data")
            {
                break;
            }
            if (size < 0 || next > stream.Length)
            {
                break;
            }

            stream.Position = next;
        }
        if (frames < 0 || sampleRate <= 0 || channels <= 0)
        {
            throw new InvalidDataException("a CAF file that doesn't give its format and length");
        }
        return Info(frames / sampleRate, (int)sampleRate, channels);
    }

    private static AudioFileInfo ReadWav(Stream stream)
    {
        byte[] header = new byte[8];
        stream.Position = 12;
        int channels = 0, sampleRate = 0, blockAlign = 0;
        while (ReadFully(stream, header, 0, 8) == 8)
        {
            string id = Ascii(header, 0, 4);
            long size = LittleUInt32(header, 4);
            long next = stream.Position + size + (size & 1);
            if (id == "fmt " && size >= 16)
            {
                byte[] format = ReadExactly(stream, 16);
                channels = LittleUInt16(format, 2);
                sampleRate = (int)LittleUInt32(format, 4);
                blockAlign = LittleUInt16(format, 12);
            }
            else if (id == "data")
            {
                if (channels <= 0 || sampleRate <= 0 || blockAlign <= 0)
                {
                    throw new InvalidDataException("a WAV file with no format before its data");
                }
                // A data chunk its writer couldn't size (0 or 0xFFFFFFFF) runs to the end of the file
                long data = (size == 0 || size == uint.MaxValue || stream.Position + size > stream.Length) ? stream.Length - stream.Position : size;
                return Info((double)(data / blockAlign) / sampleRate, sampleRate, channels);
            }
            stream.Position = next;
        }
        throw new InvalidDataException("a WAV file with no data chunk");
    }

    private static AudioFileInfo ReadFlac(Stream stream)
    {
        // STREAMINFO is always the first metadata block, after the 4-byte marker
        stream.Position = 4;
        byte[] block = ReadExactly(stream, 4 + 34);
        if ((block[0] & 0x7F) != 0)
        {
            throw new InvalidDataException("a FLAC file that doesn't start with STREAMINFO");
        }
        // 20 bits of sample rate, 3 of channels - 1, 5 of bits per sample - 1, 36 of total samples
        int at = 4 + 10;
        int sampleRate = (block[at] << 12) | (block[at + 1] << 4) | (block[at + 2] >> 4);
        int channels = ((block[at + 2] >> 1) & 0x7) + 1;
        long samples = ((long)(block[at + 3] & 0x0F) << 32) | ((long)block[at + 4] << 24) | ((long)block[at + 5] << 16) | ((long)block[at + 6] << 8) | block[at + 7];
        if (sampleRate <= 0 || samples <= 0)
        {
            throw new InvalidDataException("a FLAC file that doesn't give its sample rate and length");
        }
        return Info((double)samples / sampleRate, sampleRate, channels);
    }

    private static AudioFileInfo ReadOgg(Stream stream)
    {
        // The first page holds just the identification header
        byte[] page = ReadExactly(stream, 27);
        uint serial = LittleUInt32(page, 14);
        int segments = page[26];
        byte[] lacing = ReadExactly(stream, segments);
        int packetLength = 0;
        for (int i = 0; i < segments; i++)
        {
            packetLength += lacing[i];
            if (lacing[i] < 255)
            {
                break;
            }
        }
        byte[] packet = ReadExactly(stream, Math.Min(packetLength, 64));

        int sampleRate, channels;
        long preSkip = 0;
        if (packet.Length >= 19 && Ascii(packet, 0, 8) == "OpusHead")
        {
            // Opus always decodes at 48 kHz, whatever input rate the header records, and its
            // granule positions count from before the pre-skip the decoder drops
            channels = packet[9];
            preSkip = LittleUInt16(packet, 10);
            sampleRate = 48000;
        }
        else if (packet.Length >= 16 && packet[0] == 1 && Ascii(packet, 1, 6) == "vorbis")
        {
            channels = packet[11];
            sampleRate = (int)LittleUInt32(packet, 12);
        }
        else
        {
            throw new InvalidDataException("an Ogg file that isn't Vorbis or Opus");
        }

        long granule = LastGranule(stream, serial);
        if (channels <= 0 || sampleRate <= 0 || granule < preSkip)
        {
            throw new InvalidDataException("an Ogg file that doesn't give its format and length");
        }
        return Info((double)(granule - preSkip) / sampleRate, sampleRate, channels);
    }

    // The granule position of the stream's last page: the samples decoded by its end
    private static long LastGranule(Stream stream, uint serial)
    {
        // A page is at most 65,307 bytes, so the last one starts in the last 64 KB
        long window = Math.Min(stream.Length, 65536 + 27);
        stream.Position = stream.Length - window;
        byte[] tail = ReadExactly(stream, (int)window);
        for (int at = tail.Length - 27; at >= 0; at--)
        {
            if (tail[at] == (byte)'O' && tail[at + 1] == (byte)'g' && tail[at + 2] == (byte)'g' && tail[at + 3] == (byte)'S' && tail[at + 4] == 0 && LittleUInt32(tail, at + 14) == serial)
            {
                long granule = (long)(LittleUInt32(tail, at + 6) | ((ulong)LittleUInt32(tail, at + 10) << 32));
                if (granule >= 0)
                {
                    return granule;
                }
            }
        }
        throw new InvalidDataException("an Ogg file with no last page");
    }

    private sealed class Mp4State
    {
        public long MovieTimescale;
        public long MovieDuration;
        public long MediaTimescale;
        public long MediaDuration;
        public long Edited = -1;
        public long GaplessSamples = -1;
        public int ConfigChannels;
        public int SampleRate;
        public int Channels;
        public bool Audio;
    }

    private static AudioFileInfo ReadMp4(Stream stream)
    {
        var state = new Mp4State();
        WalkBoxes(stream, 0, stream.Length, state);
        if (!state.Audio || state.Channels <= 0)
        {
            throw new InvalidDataException("an MP4 file with no AAC track");
        }
        int sampleRate = state.SampleRate > 0 ? state.SampleRate : (int)state.MediaTimescale;

        // AAC's own configuration has the true channel count: Apple's writer always puts 2 in the
        // sample entry
        int channels = state.ConfigChannels > 0 ? state.ConfigChannels : state.Channels;

        // Apple's writer (afconvert) gives the length in iTunes' gapless tag, ffmpeg's in the edit
        // list; both leave out the encoder's priming and its padding to a whole frame, as players do
        double seconds;
        if (state.GaplessSamples > 0 && sampleRate > 0)
        {
            seconds = (double)state.GaplessSamples / sampleRate;
        }
        else if (state.Edited >= 0 && state.MovieTimescale > 0)
        {
            seconds = (double)state.Edited / state.MovieTimescale;
        }
        else if (state.MediaTimescale > 0)
        {
            seconds = (double)state.MediaDuration / state.MediaTimescale;
        }
        else if (state.MovieTimescale > 0)
        {
            seconds = (double)state.MovieDuration / state.MovieTimescale;
        }
        else
        {
            throw new InvalidDataException("an MP4 file that doesn't give its length");
        }

        return Info(seconds, sampleRate, channels);
    }

    private static void WalkBoxes(Stream stream, long start, long end, Mp4State state)
    {
        long at = start;
        while (at + 8 <= end)
        {
            stream.Position = at;
            byte[] header = ReadExactly(stream, 8);
            long size = BigUInt32(header, 0);
            string type = Ascii(header, 4, 4);
            long body = at + 8;
            if (size == 1)
            {
                size = (long)BigUInt64(ReadExactly(stream, 8), 0);
                body += 8;
            }
            else if (size == 0)
            {
                size = end - at;
            }
            if (size < body - at || at + size > end)
            {
                throw new InvalidDataException("a damaged MP4 box '" + type + "'");
            }
            long boxEnd = at + size;
            int length = (int)Math.Min(boxEnd - body, 4096);
            stream.Position = body;

            switch (type)
            {
                case "trak":
                    // The first audio track only
                    if (!state.Audio)
                    {
                        WalkBoxes(stream, body, boxEnd, state);
                    }

                    break;
                case "moov":
                case "udta":
                case "ilst":
                case "mdia":
                case "minf":
                case "stbl":
                case "edts":
                    WalkBoxes(stream, body, boxEnd, state);
                    break;
                case "mvhd":
                    {
                        byte[] box = ReadExactly(stream, Math.Min(length, 32));
                        bool wide = box[0] == 1;
                        state.MovieTimescale = BigUInt32(box, wide ? 20 : 12);
                        state.MovieDuration = wide ? (long)BigUInt64(box, 24) : BigUInt32(box, 16);
                        break;
                    }
                case "mdhd":
                    {
                        byte[] box = ReadExactly(stream, Math.Min(length, 32));
                        bool wide = box[0] == 1;
                        state.MediaTimescale = BigUInt32(box, wide ? 20 : 12);
                        state.MediaDuration = wide ? (long)BigUInt64(box, 24) : BigUInt32(box, 16);
                        break;
                    }
                case "elst":
                    {
                        byte[] box = ReadExactly(stream, length);
                        bool wide = box[0] == 1;
                        int entrySize = wide ? 20 : 12;
                        long count = BigUInt32(box, 4);
                        long total = 0;
                        int entry = 8;
                        for (long i = 0; i < count && entry + entrySize <= box.Length; i++)
                        {
                            long duration = wide ? (long)BigUInt64(box, entry) : BigUInt32(box, entry);
                            long mediaTime = wide ? (long)BigUInt64(box, entry + 8) : (int)BigUInt32(box, entry + 4);

                            // An entry with no media time is an empty edit, a pause before the track
                            if (mediaTime >= 0)
                            {
                                total += duration;
                            }

                            entry += entrySize;
                        }
                        state.Edited = total;
                        break;
                    }
                case "meta":
                    // A full box: version and flags before its children
                    WalkBoxes(stream, body + 4, boxEnd, state);
                    break;
                case "----":
                    ReadFreeformTag(stream, body, boxEnd, state);
                    break;
                case "esds":
                    if (state.Audio)
                    {
                        state.ConfigChannels = AacConfigChannels(ReadExactly(stream, length));
                    }

                    break;
                case "stsd":
                    // Version and flags and the entry count, then the sample entries
                    WalkBoxes(stream, body + 8, boxEnd, state);
                    break;
                case "mp4a":
                    {
                        // An AudioSampleEntry: 6 reserved bytes and the data reference index, 8 more
                        // reserved, the channel count, the sample size, 4 more, then the rate in 16.16
                        byte[] box = ReadExactly(stream, 28);
                        state.Channels = (box[16] << 8) | box[17];
                        state.SampleRate = (int)(BigUInt32(box, 24) >> 16);
                        state.Audio = true;

                        // Its own boxes (esds) follow the 28 bytes of the entry
                        WalkBoxes(stream, body + 28, boxEnd, state);
                        break;
                    }
            }
            at = boxEnd;
        }
    }

    // An iTunes freeform tag: mean, name and data. iTunSMPB's text is hex fields: 0, the encoder's
    // priming, its padding, then the number of samples
    private static void ReadFreeformTag(Stream stream, long start, long end, Mp4State state)
    {
        string name = null;
        string text = null;
        long at = start;
        while (at + 8 <= end)
        {
            stream.Position = at;
            byte[] header = ReadExactly(stream, 8);
            long size = BigUInt32(header, 0);
            if (size < 8 || at + size > end)
            {
                return;
            }

            string type = Ascii(header, 4, 4);
            int length = (int)Math.Min(size - 8, 1024);
            if (type == "name" && length > 4)
            {
                name = Ascii(ReadExactly(stream, length), 4, length - 4);
            }
            else if (type == "data" && length > 8)
            {
                text = Ascii(ReadExactly(stream, length), 8, length - 8);
            }

            at += size;
        }
        if (name != "iTunSMPB" || text == null)
        {
            return;
        }

        string[] fields = text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        long samples;
        if (fields.Length >= 4 && long.TryParse(fields[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out samples) && samples > 0)
        {
            state.GaplessSamples = samples;
        }
    }

    // The channel configuration of an esds box's AudioSpecificConfig, or 0
    private static int AacConfigChannels(byte[] box)
    {
        // Version and flags, then descriptors: ES (3) holds DecoderConfig (4), which holds the
        // decoder specific info (5), the AudioSpecificConfig
        int at = 4;
        while (at < box.Length)
        {
            int tag = box[at++];
            int size = 0;
            for (int i = 0; i < 4 && at < box.Length; i++)
            {
                int b = box[at++];
                size = (size << 7) | (b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }
            if (tag == 3)
            {
                if (at + 3 > box.Length)
                {
                    return 0;
                }

                int flags = box[at + 2];
                at += 3;
                if ((flags & 0x80) != 0)
                {
                    at += 2;
                }

                if ((flags & 0x40) != 0 && at < box.Length)
                {
                    at += 1 + box[at];
                }

                if ((flags & 0x20) != 0)
                {
                    at += 2;
                }
            }
            else if (tag == 4)
            {
                at += 13;
            }
            else if (tag == 5)
            {
                // 5 bits of object type, 4 of frequency index (and 24 more if it's 15), then 4 of channels
                if (at + 2 > box.Length)
                {
                    return 0;
                }

                int bits = (box[at] << 8) | box[at + 1];
                int frequencyIndex = (bits >> 7) & 0xF;
                if (frequencyIndex == 15)
                {
                    if (at + 5 > box.Length)
                    {
                        return 0;
                    }

                    return (box[at + 4] >> 3) & 0xF;
                }
                return (bits >> 3) & 0xF;
            }
            else
            {
                at += size;
            }
        }
        return 0;
    }

    private static AudioFileInfo Info(double seconds, int sampleRate, int channels)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || sampleRate <= 0 || channels <= 0)
        {
            throw new InvalidDataException("an audio file with no valid duration and format");
        }
        return new AudioFileInfo { DurationSeconds = seconds, SampleRate = sampleRate, Channels = channels };
    }

    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }
        return total;
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        byte[] buffer = new byte[count];
        if (ReadFully(stream, buffer, 0, count) < count)
        {
            throw new InvalidDataException("the file ends early");
        }
        return buffer;
    }

    private static string Ascii(byte[] bytes, int offset, int count) => Encoding.ASCII.GetString(bytes, offset, count);
    private static int LittleUInt16(byte[] b, int at) => b[at] | (b[at + 1] << 8);
    private static uint LittleUInt32(byte[] b, int at) => (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
    private static uint BigUInt32(byte[] b, int at) => (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);
    private static ulong BigUInt64(byte[] b, int at) => ((ulong)BigUInt32(b, at) << 32) | BigUInt32(b, at + 4);
}

// Copyright (c) 2026 Hot Chai Games LLC. Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;
using AVFoundation;
using DotGame.Framework.Audio;
using DotGame.Framework.Content;
using DotGame.Framework.iOS.Audio;
using DotGame.Framework.Platform.Audio;
using DotGame.Framework.Platform.Common.Audio;
using Foundation;

namespace DotGame.Framework.iOS.Content;

internal sealed class SoundLoader : IContentLoader<Sound>
{
    private readonly ContentServiceStrategy _contentService;

    public SoundLoader(
        ContentServiceStrategy contentService)
    {
        this._contentService = contentService;
    }

    public async Task<Sound> LoadAsync(
        string contentKey)
    {
        var resolvedPath = this._contentService.ResolveContentPath(contentKey);

        // A lossless sound is FLAC (or WAV), decoded by DotGame.AudioCodecs, as on desktop and Android:
        // its decoder takes a third of AVAudioFile's time for FLAC (60 against 187 ms for a 111 s bank
        // on an iPhone 12 mini, 2026-11-12), on the startup path. A lossy one is AAC (.m4a) or Opus in
        // CAF (LossyFormat="Opus"), which AVAudioFile decodes, as it does music.
        bool isLossless = resolvedPath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
            || resolvedPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        if (!isLossless)
        {
            var (decoded, decodedRate, decodedChannels) = this.Decode(resolvedPath);
            var decodedDuration = TimeSpan.FromSeconds((double)decoded.Length / (decodedRate * decodedChannels * sizeof(short)));
            return new Sound(new Strategy<ISoundStrategy>(new SoundStrategy(decoded, decodedRate, decodedChannels, decodedDuration)));
        }

        byte[] file;
        using (var stream = await this._contentService.OpenStreamAsync(resolvedPath).ConfigureAwait(false))
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms).ConfigureAwait(false);
            file = ms.ToArray();
        }

        var (pcm, sampleRate, channels) = SoundDecoder.Decode(file);
        var duration = TimeSpan.FromSeconds((double)pcm.Length / (sampleRate * channels * sizeof(short)));
        return new Sound(new Strategy<ISoundStrategy>(new SoundStrategy(pcm, sampleRate, channels, duration)));
    }

    // The whole file as 16-bit interleaved PCM, the layout SoundStrategy keeps. AVAudioFile leaves
    // out the encoder's priming and padding (the file's gapless length), so a sound bank's offsets
    // land on the same samples as in the source.
    private (byte[] Pcm, int SampleRate, int Channels) Decode(
        string resolvedPath)
    {
        using var url = NSUrl.FromFilename(this._contentService.GetFullPath(resolvedPath));
        using var file = new AVAudioFile(url, AVAudioCommonFormat.PCMInt16, interleaved: true, out var openError);
        if (openError is not null)
        {
            throw new InvalidDataException($"Couldn't open the sound '{resolvedPath}': {openError.LocalizedDescription}");
        }

        var format = file.ProcessingFormat;
        int channels = (int)format.ChannelCount;
        int sampleRate = (int)format.SampleRate;
        if ((file.Length <= 0) || (file.Length > (int.MaxValue / (channels * sizeof(short)))))
        {
            throw new InvalidDataException($"The sound '{resolvedPath}' has {file.Length} frames.");
        }

        using var buffer = new AVAudioPcmBuffer(format, (uint)file.Length);
        if (!file.ReadIntoBuffer(buffer, out var readError) || (readError is not null))
        {
            throw new InvalidDataException($"Couldn't decode the sound '{resolvedPath}': {readError?.LocalizedDescription}");
        }

        byte[] pcm = GC.AllocateUninitializedArray<byte>((int)buffer.FrameLength * channels * sizeof(short));
        unsafe
        {
            // Interleaved: one buffer, behind the first of the channel pointers
            short* samples = ((short**)buffer.Int16ChannelData)[0];
            new ReadOnlySpan<byte>(samples, pcm.Length).CopyTo(pcm);
        }

        return (pcm, sampleRate, channels);
    }
}

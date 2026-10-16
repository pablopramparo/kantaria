// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Pablo Pramparo
using Kantor.Core.Library;
using Xunit;

namespace Kantor.Tests;

public sealed class SongPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kantor-preview-{Guid.NewGuid():N}");

    public SongPreviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Song(string header, bool createAudio = true, string audioName = "a.mp3")
    {
        if (createAudio)
        {
            File.WriteAllBytes(Path.Combine(_dir, audioName), [0]);
        }

        var path = Path.Combine(_dir, "song.txt");
        File.WriteAllText(path, header + "\n: 0 4 0 La\nE\n");
        return path;
    }

    [Fact]
    public void Reads_audio_and_preview_start()
    {
        var path = Song("#TITLE:T\n#ARTIST:A\n#MP3:a.mp3\n#BPM:120\n#PREVIEWSTART:42.5");

        var info = SongHeaderReader.ReadPreview(path);

        Assert.NotNull(info);
        Assert.Equal(Path.Combine(_dir, "a.mp3"), info!.AudioPath);
        Assert.Equal(42.5, info.StartSeconds);
    }

    [Fact]
    public void Accepts_a_decimal_comma_and_the_audio_tag()
    {
        var path = Song("#TITLE:T\n#AUDIO:a.mp3\n#BPM:120\n#PREVIEWSTART:12,25");

        var info = SongHeaderReader.ReadPreview(path);

        Assert.Equal(12.25, info!.StartSeconds);
    }

    [Fact]
    public void Preview_start_is_optional_or_ignored_when_invalid()
    {
        Assert.Null(SongHeaderReader.ReadPreview(Song("#MP3:a.mp3\n#BPM:120"))!.StartSeconds);
        Assert.Null(SongHeaderReader.ReadPreview(Song("#MP3:a.mp3\n#BPM:120\n#PREVIEWSTART:abc"))!.StartSeconds);
        Assert.Null(SongHeaderReader.ReadPreview(Song("#MP3:a.mp3\n#BPM:120\n#PREVIEWSTART:-3"))!.StartSeconds);
    }

    [Fact]
    public void Missing_audio_or_audio_outside_the_folder_gives_no_preview()
    {
        Assert.Null(SongHeaderReader.ReadPreview(Song("#MP3:a.mp3\n#BPM:120", createAudio: false)));
        Assert.Null(SongHeaderReader.ReadPreview(Song("#MP3:..\\fuera.mp3\n#BPM:120", createAudio: false)));
        Assert.Null(SongHeaderReader.ReadPreview(Path.Combine(_dir, "no-existe.txt")));
    }
}

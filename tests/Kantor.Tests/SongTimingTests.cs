using Kantor.Core.Songs;
using Kantor.Core.Timing;

namespace Kantor.Tests;

public class SongTimingTests
{
    [Fact]
    public void Beat_zero_is_at_the_gap()
    {
        var timing = new SongTiming(bpm: 120, gapMilliseconds: 2000);

        Assert.Equal(0.0, timing.BeatAt(TimeSpan.FromSeconds(2)), 6);
    }

    [Fact]
    public void Beat_is_a_quarter_of_the_file_bpm()
    {
        // 120 BPM de archivo = 480 beats por minuto = 8 beats por segundo.
        var timing = new SongTiming(bpm: 120, gapMilliseconds: 0);

        Assert.Equal(8.0, timing.BeatAt(TimeSpan.FromSeconds(1)), 6);
        Assert.Equal(16.0, timing.BeatAt(TimeSpan.FromSeconds(2)), 6);
    }

    [Fact]
    public void Time_before_the_gap_gives_negative_beats()
    {
        var timing = new SongTiming(bpm: 120, gapMilliseconds: 1000);

        Assert.Equal(-4.0, timing.BeatAt(TimeSpan.FromSeconds(0.5)), 6);
    }

    [Fact]
    public void TimeOf_is_the_inverse_of_BeatAt()
    {
        var timing = new SongTiming(bpm: 178.9, gapMilliseconds: 13509);

        var t = timing.TimeOf(250);

        // TimeSpan redondea a ticks de 100 ns: alcanza con precisión de 1e-4 beats.
        Assert.Equal(250.0, timing.BeatAt(t), 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void Invalid_bpm_is_rejected(double bpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SongTiming(bpm, 0));
    }

    [Fact]
    public void Voice_picks_the_line_that_already_started()
    {
        var parsed = UltraStarParser.Parse("#TITLE:T\n#BPM:100\n: 10 2 1 a\n- 14\n: 20 2 1 b\n- 24\n: 30 2 1 c\nE\n");
        var voice = parsed.Voices[0];

        Assert.Equal(0, voice.LineIndexAt(-5));
        Assert.Equal(0, voice.LineIndexAt(10));
        Assert.Equal(0, voice.LineIndexAt(19.9));
        Assert.Equal(1, voice.LineIndexAt(20));
        Assert.Equal(2, voice.LineIndexAt(500));
    }
}

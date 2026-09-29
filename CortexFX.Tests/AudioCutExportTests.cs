using Xunit;

namespace CortexFX.Tests;

public class AudioCutExportTests
{
    [Fact]
    public void SameFormatCutKeepsOriginalAudioStream()
    {
        string args = MainWindow.BuildAudioExportArgs(
            "input.mp3", "cut.mp3", "00:00:10.000", "00:00:05.000");

        Assert.Contains("-c copy", args);
        Assert.DoesNotContain("-af ", args);
    }

    [Fact]
    public void FormatChangeDoesNotApplyPreviewVolume()
    {
        string args = MainWindow.BuildAudioExportArgs(
            "input.wav", "cut.mp3", "00:00:10.000", "00:00:05.000");

        Assert.Contains("-c:a libmp3lame", args);
        Assert.DoesNotContain("-af ", args);
        Assert.DoesNotContain("volume=", args);
    }
}

using eMusicApp;

namespace eMusicApp.Tests;

public class AudioControllerTests
{
    [Fact]
    public void Test_ReportPlaybackState_InvokesEvent()
    {
        // Arrange
        bool eventFired = false;
        bool? reportedState = null;
        
        NativeAudioController.OnPlaybackStateChanged = (isPlaying) => 
        {
            eventFired = true;
            reportedState = isPlaying;
        };

        // Act
        NativeAudioController.ReportPlaybackState(true);

        // Assert
        Assert.True(eventFired);
        Assert.True(reportedState);

        // Cleanup
        NativeAudioController.OnPlaybackStateChanged = null;
    }

    [Fact]
    public void Test_RequestPlay_InvokesEventWithCorrectParameters()
    {
        // Arrange
        bool eventFired = false;
        string? passedUrl = null;
        string? passedTitle = null;

        NativeAudioController.OnPlayRequested = (url, title, artist, thumb) =>
        {
            eventFired = true;
            passedUrl = url;
            passedTitle = title;
        };

        // Act
        NativeAudioController.RequestPlay("test_url", "test_title", "artist", "thumb");

        // Assert
        Assert.True(eventFired);
        Assert.Equal("test_url", passedUrl);
        Assert.Equal("test_title", passedTitle);

        // Cleanup
        NativeAudioController.OnPlayRequested = null;
    }
}

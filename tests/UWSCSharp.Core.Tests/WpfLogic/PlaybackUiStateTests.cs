using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class PlaybackUiStateTests
{
    [Theory]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, false, false, true)]
    public void PlaybackAndRecorderStateDetermineAvailableActions(
        bool running, bool hasRecorder, bool canEdit,
        bool canEditSettings, bool canStop)
    {
        var state = PlaybackUiState.Create(running, hasRecorder);

        Assert.Equal(canEdit, state.CanRecord);
        Assert.Equal(canEdit, state.CanEdit);
        Assert.Equal(canEditSettings, state.CanEditRecordingSettings);
        Assert.Equal(canStop, state.CanPause);
        Assert.Equal(canStop, state.CanStop);
        Assert.Equal(!canEdit, state.EditorReadOnly);
    }
}

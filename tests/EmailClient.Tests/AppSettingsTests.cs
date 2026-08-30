using EmailClient.Settings;
using Xunit;

namespace EmailClient.Tests;

public class AppSettingsTests
{
    [Theory]
    [InlineData(5, 5)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(9999, 60)]
    public void ClampedUndoSendSeconds_StaysWithinSaneRange(int raw, int expected)
    {
        var settings = new AppSettings { UndoSendSeconds = raw };
        Assert.Equal(expected, settings.ClampedUndoSendSeconds);
    }
}

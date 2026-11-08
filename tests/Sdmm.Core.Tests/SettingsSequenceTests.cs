using Sdmm.Core.Config;

namespace Sdmm.Core.Tests;

public class SettingsSequenceTests
{
    private static (SettingsSequence Seq, Func<int> Fired) Make()
    {
        var seq = new SettingsSequence();
        int fired = 0;
        seq.Completed += () => fired++;
        return (seq, () => fired);
    }

    [Fact]
    public void FullSequenceInOrderFires()
    {
        var (seq, fired) = Make();
        seq.CacheCleared();
        seq.TokenHelpExpanded();
        for (int i = 0; i < 9; i++) seq.ForksToggled();
        Assert.Equal(0, fired());
        seq.ForksToggled();
        Assert.Equal(1, fired());
        Assert.Equal(1, seq.Step);   // starts over afterwards, so it can be done again to switch back
    }

    [Fact]
    public void OutOfOrderStepsStartOver()
    {
        var (seq, fired) = Make();
        seq.TokenHelpExpanded();                       // before clearing the cache
        seq.CacheCleared();
        for (int i = 0; i < 10; i++) seq.ForksToggled(); // never reached step 3
        Assert.Equal(0, fired());

        seq.CacheCleared();
        seq.TokenHelpExpanded();
        seq.ForksToggled();
        seq.CacheCleared();                            // interrupts the toggling
        for (int i = 0; i < 10; i++) seq.ForksToggled();
        Assert.Equal(0, fired());
    }

    [Fact]
    public void TogglingBeforeTheOtherStepsDoesNotCount()
    {
        var (seq, fired) = Make();
        for (int i = 0; i < 5; i++) seq.ForksToggled();
        seq.CacheCleared();
        seq.TokenHelpExpanded();
        for (int i = 0; i < 5; i++) seq.ForksToggled();
        Assert.Equal(0, fired());                      // the early 5 don't carry over
        for (int i = 0; i < 5; i++) seq.ForksToggled();
        Assert.Equal(1, fired());
    }
}

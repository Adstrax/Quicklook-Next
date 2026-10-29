namespace QuickLook.Tests;

internal sealed class TrayMenuTextTests
{
    public void RemovesActionEllipsisAndAccessKeys()
    {
        Assert.Equal(
            "Check for Updates",
            QuickLookNext.TrayMenuWindow.FormatMenuText("Check for &Updates..."),
            "action ellipsis and access keys");
    }

    public void CollapsesDoubleAmpersandSpacing()
    {
        Assert.Equal(
            "Data cache",
            QuickLookNext.TrayMenuWindow.FormatMenuText("Data && cache..."),
            "collapsed whitespace");
    }

    public void RemovesUnicodeEllipsis()
    {
        Assert.Equal(
            "更新历史",
            QuickLookNext.TrayMenuWindow.FormatMenuText("更新历史…"),
            "Unicode ellipsis");
    }
}

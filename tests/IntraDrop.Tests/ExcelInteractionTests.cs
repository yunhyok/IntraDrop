using IntraDrop.UI;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ExcelInteractionTests
{
    [Theory]
    [InlineData("measured popup without GUI flags", "Net UI Tool Window", "NetUIHWND", 0u, false, false, true)]
    [InlineData("idle worksheet", "XLMAIN", "EXCEL7", 0u, false, false, false)]
    [InlineData("cell F2 edit", "XLMAIN", "EXCEL6", 0u, false, false, true)]
    [InlineData("menu mode", "XLMAIN", "EXCEL7", 0x04u, false, false, true)]
    [InlineData("system menu mode", "XLMAIN", "EXCEL7", 0x08u, false, false, true)]
    [InlineData("popup menu mode", "XLMAIN", "EXCEL7", 0x10u, false, false, true)]
    [InlineData("caret blink only", "XLMAIN", "EXCEL7", 0x01u, false, false, false)]
    [InlineData("mouse capture", "XLMAIN", "EXCEL7", 0u, true, false, true)]
    [InlineData("clipboard chord", "XLMAIN", "EXCEL7", 0u, false, true, true)]
    [InlineData("unknown foreground class", "unexpected", "EXCEL7", 0u, false, false, true)]
    [InlineData("class names ignore case", "xlmain", "excel6", 0u, false, false, true)]
    public void InteractionPolicyMatchesObservedExcelStates(string scenario, string activeClass,
        string focusClass, uint guiFlags, bool hasMouseCapture, bool clipboardShortcut, bool expected)
    {
        Assert.True(expected == ClipboardReader.IsExcelInteraction(
            activeClass, focusClass, guiFlags, hasMouseCapture, clipboardShortcut), scenario);
    }
}

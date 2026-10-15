using IntraDrop.UI;
using Xunit;

namespace IntraDrop.Tests;

public sealed class ExcelInteractionTests
{
    [Theory]
    [InlineData("measured popup without GUI flags", "XLMAIN", "Net UI Tool Window", "NetUIHWND", 0u, false, false, true)]
    [InlineData("idle worksheet", "XLMAIN", "XLMAIN", "EXCEL7", 0u, false, false, false)]
    [InlineData("cell F2 edit", "XLMAIN", "XLMAIN", "EXCEL6", 0u, false, false, true)]
    [InlineData("menu mode", "XLMAIN", "XLMAIN", "EXCEL7", 0x04u, false, false, true)]
    [InlineData("system menu mode", "XLMAIN", "XLMAIN", "EXCEL7", 0x08u, false, false, true)]
    [InlineData("popup menu mode", "XLMAIN", "XLMAIN", "EXCEL7", 0x10u, false, false, true)]
    [InlineData("caret blink only", "XLMAIN", "XLMAIN", "EXCEL7", 0x01u, false, false, false)]
    [InlineData("mouse capture", "XLMAIN", "XLMAIN", "EXCEL7", 0u, true, false, true)]
    [InlineData("clipboard chord or held input", "XLMAIN", "XLMAIN", "EXCEL7", 0u, false, true, true)]
    [InlineData("unknown foreground class", "XLMAIN", "unexpected", "EXCEL7", 0u, false, false, true)]
    [InlineData("class names ignore case", "xlmain", "XlMain", "excel6", 0u, false, false, true)]
    [InlineData("idle Word document", "OpusApp", "OpusApp", "_WwG", 0u, false, false, false)]
    [InlineData("Word popup", "OpusApp", "Net UI Tool Window", "NetUIHWND", 0u, false, false, true)]
    [InlineData("idle PowerPoint slide", "PPTFrameClass", "PPTFrameClass", "mdiClass", 0u, false, false, false)]
    public void InteractionPolicyMatchesObservedOfficeStates(string scenario, string mainClass, string activeClass,
        string focusClass, uint guiFlags, bool hasMouseCapture, bool inputHeld, bool expected)
    {
        Assert.True(expected == ClipboardReader.IsOfficeInteraction(
            mainClass, activeClass, focusClass, guiFlags, hasMouseCapture, inputHeld), scenario);
    }
}

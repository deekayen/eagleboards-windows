using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using EagleBoards.App;

namespace EagleBoards.UiTests;

/// <summary>
/// What the start-up window and a command-line start tell the operator when
/// the event can't start or the SignUpGenius import fails (K3): plain words
/// and what to do, never a status code and a web address read out.
/// </summary>
public class StartProblemTests
{
    /// <summary>Windows' sharing violation: ERROR_SHARING_VIOLATION (32) in the low word.</summary>
    private static IOException SharingViolation(string path) =>
        new($"The process cannot access the file '{path}' because it is being used by another process.", unchecked((int)0x80070020));

    [Fact]
    public void APortInUseIsSaidWithTheNumberAndWhatToDo()
    {
        var problem = EventSession.StartProblem(new IOException("Failed to bind to address http://[::]:8080: address already in use."), 8080);

        Assert.StartsWith("Port 8080 is already in use.", problem, StringComparison.Ordinal);
        Assert.Contains("choose another port", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOpenInExcelIsNamedByItsFileNameNotItsPath()
    {
        var problem = EventSession.StartProblem(SharingViolation(@"C:\eagleboards\2026-11-03\scouts.csv"), 8080);

        Assert.Equal("scouts.csv is open in another program, probably Excel. Close it there, and then start again.", problem);
    }

    [Fact]
    public void ALockedFileWithAnApostropheInItsPathStaysWhole()
    {
        Assert.Equal(@"C:\O'Brien\scouts.csv", EventSession.LockedFile(SharingViolation(@"C:\O'Brien\scouts.csv")));
    }

    [Fact]
    public void ASharingViolationThatNamesNoFileStillSaysAFileIsOpen()
    {
        var problem = EventSession.StartProblem(new IOException("The process cannot access the file.", unchecked((int)0x80070020)), 8080);

        Assert.StartsWith("A file the event uses is open in another program, probably Excel.", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherIoProblemsAreNotTakenForALockedFile()
    {
        Assert.Null(EventSession.LockedFile(new IOException("The disk is full.")));
        Assert.Null(EventSession.LockedFile(new InvalidOperationException("nothing to do with files")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void ARefusedKeyIsSaidToBeTheKey(HttpStatusCode status)
    {
        var problem = EventSession.ImportProblem(new HttpRequestException("SignUpGenius answered HTTP " + (int)status + " for https://example.invalid/", null, status));

        Assert.Equal("SignUpGenius didn't accept the API key. Check the key, and then start the scheduler again.", problem);
        Assert.DoesNotContain("https://", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAnswerAtAllIsAnInternetProblem()
    {
        var problem = EventSession.ImportProblem(new HttpRequestException(HttpRequestError.NameResolutionError, "name not resolved"));

        Assert.StartsWith("Couldn't reach SignUpGenius.", problem, StringComparison.Ordinal);
        Assert.StartsWith("Couldn't reach SignUpGenius.", EventSession.ImportProblem(new TaskCanceledException()), StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorStatusIsSaidToBeSignUpGeniusAndTriedAgainLater()
    {
        var problem = EventSession.ImportProblem(new HttpRequestException("answered 500", null, HttpStatusCode.InternalServerError));

        Assert.StartsWith("SignUpGenius couldn't send the sign-ups just now.", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableAnswerIsSaidToBeUnrecognised()
    {
        Assert.Contains("a form the scheduler doesn't recognize", EventSession.ImportProblem(new JsonException("bad")), StringComparison.Ordinal);
        Assert.Contains("a form the scheduler doesn't recognize", EventSession.ImportProblem(new KeyNotFoundException("missing")), StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageFromCoreIsKeptAsItIs()
    {
        // Core's own words already say what to do, such as no sign-up covering today.
        Assert.Equal("No sign-up covers today.", EventSession.ImportProblem(new InvalidOperationException("No sign-up covers today.")));
    }
}
